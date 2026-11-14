using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kupro.Import.Snapshot;

namespace Kupro.Import.GitHub;

/// <summary>
/// GitHub Issues (task 027, ADR-019): repositories as projects, issues (pull requests skipped) with their org issue
/// type, sub-issue parent, labels, milestone and assignees, comments, and issue events as changes. github.com, or GitHub
/// Enterprise Server by its address. This class only reads; mapping onto Kupro is the pipeline's job.
/// </summary>
public sealed class GitHubConnector : ISourceConnector
{
    /// <summary>Item type of an issue without an org issue type (user-owned repositories, or types not in use).</summary>
    public const string PlainIssueType = "issue";

    /// <summary>Every assignee's login, when there is more than one; the first is the item's assignee.</summary>
    public const string AssigneesField = "assignees";

    /// <summary>The milestone's title, as a change field; the item's milestone is its version.</summary>
    public const string MilestoneField = "milestone";

    public const string Open = "open";
    public const string Closed = "closed";
    public const string NotPlanned = "not_planned";
    public const string Duplicate = "duplicate";

    private static readonly Uri GitHubCom = new("https://github.com");

    private readonly GitHubClient _client;
    private readonly Uri _web;
    private readonly TimeProvider _time;
    private Catalog? _catalog;

    /// <param name="url">Null or github.com for GitHub; a GitHub Enterprise Server address otherwise.</param>
    /// <param name="token">A fine-grained token with Issues and Metadata (read), or a classic token with <c>repo</c>.</param>
    public GitHubConnector(HttpClient http, string? url, string token, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        (_web, var api) = Addresses(url);
        _client = new GitHubClient(http, api, token, _time);
    }

    internal GitHubClient Client => _client;

    /// <summary>The web address people use, and the API root under it.</summary>
    internal static (Uri Web, Uri Api) Addresses(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (GitHubCom, new Uri("https://api.github.com/"));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var given) || given.Scheme is not ("https" or "http"))
        {
            throw new SourceAccessException($"'{url}' is not a GitHub address. Use https://github.com or your GitHub Enterprise Server's address.");
        }

        if (given.Host is "github.com" or "www.github.com" or "api.github.com")
        {
            return (GitHubCom, new Uri("https://api.github.com/"));
        }

        var path = given.AbsolutePath.TrimEnd('/');
        var root = path.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) ? path[..^"/api/v3".Length] : path;
        var web = new Uri($"{given.Scheme}://{given.Authority}{root}");
        return (web, new Uri($"{web.AbsoluteUri.TrimEnd('/')}/api/v3/"));
    }

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        DateTimeOffset? since = cursor is null
            ? null
            : DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{cursor}' is not a GitHub cursor (a UTC time).", nameof(cursor));
        var repos = scope.Projects.Count == 0
            ? catalog.Repos
            : [.. scope.Projects.Select(id => catalog.Repos.FirstOrDefault(r => r.Id.ToString(CultureInfo.InvariantCulture) == id)
                ?? throw new SourceAccessException($"GitHub has no repository {id} that this token can see.", HttpStatusCode.NotFound))];

        var people = new HashSet<long>();
        var newest = since;
        foreach (var repo in repos)
        {
            var project = repo.Id.ToString(CultureInfo.InvariantCulture);
            var sinceQuery = since is { } s ? $"&since={Uri.EscapeDataString(s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" : "";

            await foreach (var milestone in _client.PagedAsync($"repos/{repo.FullName}/milestones?state=all&per_page=100", GitHubJson.Default.ListGitHubMilestone, ct))
            {
                yield return new SourceVersion
                {
                    Id = milestone.Id.ToString(CultureInfo.InvariantCulture),
                    Project = project,
                    Name = milestone.Title,
                    Date = milestone.DueOn is { } due ? DateOnly.FromDateTime(due.UtcDateTime) : null,
                    Released = milestone.State == "closed",
                };
            }

            // Issues are held per repository: a sub-issue's parent comes from the parent's sub-issue list.
            var issues = new List<GitHubIssue>();
            await foreach (var issue in _client.PagedAsync($"repos/{repo.FullName}/issues?state=all&sort=created&direction=asc&per_page=100{sinceQuery}", GitHubJson.Default.ListGitHubIssue, ct))
            {
                if (issue.PullRequest is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
                {
                    continue;
                }

                if (newest is null || issue.UpdatedAt > newest)
                {
                    newest = issue.UpdatedAt;
                }

                if (scope.ResolvedAfter is { } after && issue.ClosedAt is { } closed && issue.State == Closed && closed < after)
                {
                    continue;
                }

                issues.Add(issue);
            }

            var byNumber = issues.ToDictionary(i => i.Number);
            var parents = new Dictionary<long, long>();
            foreach (var parent in issues.Where(i => i.SubIssuesSummary?.Total > 0))
            {
                await foreach (var child in _client.TryPagedAsync($"repos/{repo.FullName}/issues/{parent.Number}/sub_issues?per_page=100", GitHubJson.Default.ListGitHubIssue, ct))
                {
                    parents[child.Id] = parent.Id;
                }
            }

            foreach (var issue in issues)
            {
                foreach (var person in issue.Assignees.Append(issue.User).OfType<GitHubUser>().Where(p => people.Add(p.Id)))
                {
                    yield return Person(person);
                }

                yield return Item(repo, issue, parents.TryGetValue(issue.Id, out var parentId) ? parentId : null);
            }

            await foreach (var comment in _client.PagedAsync($"repos/{repo.FullName}/issues/comments?sort=created&direction=asc&per_page=100{sinceQuery}", GitHubJson.Default.ListGitHubComment, ct))
            {
                if (!byNumber.TryGetValue(NumberOf(comment.IssueUrl), out var issue) || string.IsNullOrWhiteSpace(comment.Body))
                {
                    continue;
                }

                if (comment.User is { } author && people.Add(author.Id))
                {
                    yield return Person(author);
                }

                yield return new SourceComment
                {
                    Id = comment.Id.ToString(CultureInfo.InvariantCulture),
                    Item = Id(issue.Id),
                    Author = comment.User is { } user ? Id(user.Id) : null,
                    Body = comment.Body,
                    Markup = MarkupKind.Markdown,
                    Created = comment.CreatedAt,
                    Updated = comment.UpdatedAt is { } updated && updated > comment.CreatedAt ? updated : null,
                };
            }

            var events = new Dictionary<int, List<GitHubEvent>>();
            await foreach (var e in _client.PagedAsync($"repos/{repo.FullName}/issues/events?per_page=100", GitHubJson.Default.ListGitHubEvent, ct))
            {
                if (e.Issue is { } of && byNumber.ContainsKey(of.Number) && (since is null || e.CreatedAt >= since))
                {
                    (events.TryGetValue(of.Number, out var list) ? list : events[of.Number] = []).Add(e);
                }
            }

            foreach (var (number, list) in events)
            {
                var issue = byNumber[number];
                foreach (var actor in list.Select(e => e.Actor).OfType<GitHubUser>().Where(a => people.Add(a.Id)))
                {
                    yield return Person(actor);
                }

                foreach (var change in Changes(issue, list))
                {
                    yield return change;
                }
            }
        }

        yield return new ReadCursor((newest ?? _time.GetUtcNow()).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>GitHub issues carry files as links in their Markdown, not as attachments, so there is nothing to fetch.</summary>
    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        throw new SourceAccessException("GitHub issues have no attachments to fetch; files stay links in the text.");

    private SourceItem Item(GitHubRepo repo, GitHubIssue issue, long? parent) => new()
    {
        Id = Id(issue.Id),
        Key = $"{repo.FullName}#{issue.Number}",
        Project = repo.Id.ToString(CultureInfo.InvariantCulture),
        Type = issue.Type is { } type ? Id(type.Id) : PlainIssueType,
        Status = Status(issue.State, issue.StateReason),
        Title = issue.Title,
        Description = string.IsNullOrWhiteSpace(issue.Body) ? null : issue.Body,
        DescriptionMarkup = MarkupKind.Markdown,
        Parent = parent is { } p ? Id(p) : null,
        Assignee = issue.Assignees.Count > 0 ? Id(issue.Assignees[0].Id) : null,
        Creator = issue.User is { } user ? Id(user.Id) : null,
        Labels = [.. issue.Labels.Select(l => l.Name)],
        Fields = issue.Assignees.Count > 1
            ? new Dictionary<string, JsonElement> { [AssigneesField] = FieldValue.Options([.. issue.Assignees.Select(a => a.Login)]) }
            : new Dictionary<string, JsonElement>(),
        Versions = issue.Milestone is { } milestone ? [Id(milestone.Id)] : [],
        Url = issue.HtmlUrl ?? $"{_web.AbsoluteUri.TrimEnd('/')}/{repo.FullName}/issues/{issue.Number}",
        Created = issue.CreatedAt,
        Updated = issue.UpdatedAt,
        Resolved = issue.State == Closed ? issue.ClosedAt : null,
    };

    /// <summary>
    /// Events as changes. Label events say which label moved, so the label sets are worked out backwards from the
    /// issue's labels now. A close carries its reason; a reopen goes back to Open from a close of unknown kind.
    /// </summary>
    internal static IEnumerable<SourceChange> Changes(GitHubIssue issue, IReadOnlyList<GitHubEvent> events)
    {
        var item = Id(issue.Id);
        var changes = new List<SourceChange>();
        var labels = new SortedSet<string>(issue.Labels.Select(l => l.Name), StringComparer.Ordinal);
        foreach (var e in events.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id))
        {
            SourceChange Change(string field, string? from, string? to, string? fromText = null, string? toText = null) => new()
            {
                Id = Id(e.Id),
                Item = item,
                Actor = e.Actor is { } actor ? Id(actor.Id) : null,
                At = e.CreatedAt,
                Field = field,
                From = from,
                To = to,
                FromText = fromText,
                ToText = toText,
            };

            switch (e.Event)
            {
                case "closed":
                    changes.Add(Change("status", Open, Status(Closed, e.StateReason)));
                    break;
                case "reopened":
                    changes.Add(Change("status", Closed, Open));
                    break;
                case "assigned" when e.Assignee is { } assignee:
                    changes.Add(Change("assignee", null, Id(assignee.Id), null, assignee.Login));
                    break;
                case "unassigned" when e.Assignee is { } assignee:
                    changes.Add(Change("assignee", Id(assignee.Id), null, assignee.Login, null));
                    break;
                case "renamed" when e.Rename is { } rename:
                    changes.Add(Change("title", rename.From, rename.To));
                    break;
                case "milestoned" when e.Milestone is { } milestone:
                    changes.Add(Change(MilestoneField, null, milestone.Title));
                    break;
                case "demilestoned" when e.Milestone is { } milestone:
                    changes.Add(Change(MilestoneField, milestone.Title, null));
                    break;
                case "labeled" or "unlabeled" when e.Label is { } label:
                    var after = string.Join(", ", labels);
                    if (e.Event == "labeled")
                    {
                        labels.Remove(label.Name);
                    }
                    else
                    {
                        labels.Add(label.Name);
                    }

                    changes.Add(Change("labels", null, null, string.Join(", ", labels), after));
                    break;
            }
        }

        changes.Reverse();
        return changes;
    }

    private static string Status(string state, string? reason) => state != Closed
        ? Open
        : reason switch
        {
            "not_planned" => NotPlanned,
            "duplicate" => Duplicate,
            _ => Closed,
        };

    private static SourcePerson Person(GitHubUser user) => new()
    {
        Id = Id(user.Id),
        Name = string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name,
        Email = user.Email,
        Handle = user.Login,
        Bot = user.Type == "Bot" || user.Login.EndsWith("[bot]", StringComparison.Ordinal),
    };

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The issue number at the end of a comment's <c>issue_url</c>.</summary>
    private static int NumberOf(string issueUrl) =>
        int.TryParse(issueUrl.AsSpan(issueUrl.LastIndexOf('/') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private async Task<Catalog> CatalogAsync(CancellationToken ct)
    {
        if (_catalog is not null)
        {
            return _catalog;
        }

        var repos = new List<GitHubRepo>();
        await foreach (var repo in _client.PagedAsync("user/repos?per_page=100&sort=full_name", GitHubJson.Default.ListGitHubRepo, ct))
        {
            if (repo.HasIssues)
            {
                repos.Add(repo);
            }
        }

        var types = new List<SourceItemType> { new() { Id = PlainIssueType, Name = "Issue" } };
        foreach (var org in repos.Select(r => r.Owner).Where(o => o.Type == "Organization").Select(o => o.Login).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await foreach (var type in _client.TryPagedAsync($"orgs/{org}/issue-types", GitHubJson.Default.ListGitHubIssueType, ct))
            {
                if (type.IsEnabled is not false && types.All(t => t.Id != Id(type.Id)))
                {
                    types.Add(new SourceItemType { Id = Id(type.Id), Name = type.Name });
                }
            }
        }

        // Assignable users per repository: everyone who can own an issue. Commenters outside it arrive with the read.
        var people = new Dictionary<long, GitHubUser>();
        foreach (var repo in repos)
        {
            await foreach (var user in _client.TryPagedAsync($"repos/{repo.FullName}/assignees?per_page=100", GitHubJson.Default.ListGitHubUser, ct))
            {
                people.TryAdd(user.Id, user);
            }
        }

        _catalog = new Catalog(repos, new SourceDescription
        {
            Source = new SnapshotSource { Kind = SourceKinds.GitHub, BaseUrl = _web.AbsoluteUri.TrimEnd('/') },
            // The repository name is the key people know it by: `--project api`, and API as the proposed Kupro key.
            Projects = [.. repos.Select(r => new SourceProject { Id = Id(r.Id), Key = r.Name, Name = r.FullName, Url = r.HtmlUrl })],
            People = [.. people.Values.Select(Person)],
            ItemTypes = types,
            Statuses =
            [
                new SourceStatus { Id = Open, Name = "Open", Category = "unstarted" },
                new SourceStatus { Id = Closed, Name = "Closed", Category = "completed" },
                new SourceStatus { Id = NotPlanned, Name = "Closed as not planned", Category = "canceled" },
                new SourceStatus { Id = Duplicate, Name = "Closed as duplicate", Category = "canceled" },
            ],
            Fields = [new SourceField { Id = AssigneesField, Name = "Assignees", Kind = FieldKind.MultiSelect }],
        });
        return _catalog;
    }

    private sealed record Catalog(List<GitHubRepo> Repos, SourceDescription Description);
}
