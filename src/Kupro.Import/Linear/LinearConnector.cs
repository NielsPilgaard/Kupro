using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Linear;

/// <summary>
/// Linear (task 026, ADR-019). Reads teams with their workflow states, cycles, labels, people, projects with their
/// milestones, and issues with their sub-issue parents, relations, attachments, history and comments into the snapshot
/// model. Mapping onto Kupro (state types to states, projects to Epics, priorities, points to hours) is the pipeline's
/// job; this class only reads, and reports what it read in Linear's own terms.
/// </summary>
public sealed class LinearConnector : ISourceConnector
{
    /// <summary>Item type of a Linear issue, sub-issues included.</summary>
    public const string IssueType = "issue";

    /// <summary>
    /// Item type of a Linear project. An issue without a parent issue has its project as parent, so the project sits
    /// where an Epic would. A project spanning teams is read once, under the first of its teams in scope.
    /// </summary>
    public const string ProjectType = "project";

    /// <summary>The issue's Linear project, as a project ID; also the change field when an issue moves between projects.</summary>
    public const string ProjectField = "linearProject";

    /// <summary>The names of the initiatives a project belongs to.</summary>
    public const string InitiativesField = "initiatives";

    private readonly LinearClient _client;
    private Catalog? _catalog;

    /// <param name="http">Sends the requests; the connector adds the credential to each Linear request, not to the client.</param>
    /// <param name="credential">A personal API key (Settings, Security &amp; access), or an OAuth access token with read scope.</param>
    /// <param name="auth">Which of the two <paramref name="credential"/> is.</param>
    public LinearConnector(HttpClient http, string credential, LinearAuth auth = LinearAuth.ApiKey, TimeProvider? time = null) =>
        _client = new LinearClient(http, credential, auth, time ?? TimeProvider.System);

    internal LinearClient Client => _client;

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        DateTimeOffset? since = cursor is null
            ? null
            : DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{cursor}' is not a Linear cursor (a UTC time).", nameof(cursor));

        // Archived teams are read when named, not by default.
        var teams = scope.Projects.Count == 0
            ? catalog.Teams.Where(t => t.ArchivedAt is null).ToList()
            : [.. scope.Projects.Select(id => catalog.TeamsById.GetValueOrDefault(id)
                ?? throw new SourceAccessException($"Linear has no team {id} that this key can see.", HttpStatusCode.NotFound))];
        var inScope = teams.Select(t => t.Id).ToList();
        var reading = new LinearReading(catalog);

        var projectFilter = Since(new JsonObject { ["accessibleTeams"] = new JsonObject { ["some"] = IdIn(inScope) } }, since);
        await foreach (var project in PagedAsync("Projects", LinearQueries.Projects, projectFilter, d => d.Projects, ct))
        {
            reading.Saw(project.UpdatedAt);
            reading.Learn(project);
            var home = project.Teams.Nodes.Select(t => t.Id).FirstOrDefault(inScope.Contains);
            if (project.Trashed is true || home is null)
            {
                continue;
            }

            var item = reading.Project(project, home);
            if (scope.ResolvedAfter is not null && item.Resolved is not null && item.Resolved < scope.ResolvedAfter)
            {
                continue;
            }

            var milestones = LinearReading.Milestones(project, home).ToList();
            foreach (var person in reading.TakePeople())
            {
                yield return person;
            }

            foreach (var milestone in milestones)
            {
                yield return milestone;
            }

            yield return item;
        }

        foreach (var team in teams)
        {
            await foreach (var cycle in PagedAsync("Cycles", LinearQueries.Cycles, new JsonObject { ["team"] = IdEq(team.Id) }, d => d.Cycles, ct))
            {
                yield return reading.Sprint(team, cycle);
            }

            var issueFilter = Since(new JsonObject { ["team"] = IdEq(team.Id) }, since);
            await foreach (var issue in PagedAsync("Issues", LinearQueries.Issues, issueFilter, d => d.Issues, ct))
            {
                reading.Saw(issue.UpdatedAt);
                if (issue.Trashed is true)
                {
                    reading.Skip(issue.Id);
                    continue;
                }

                var item = reading.Issue(team, issue);
                if (scope.ResolvedAfter is not null && item.Resolved is not null && item.Resolved < scope.ResolvedAfter)
                {
                    reading.Skip(issue.Id);
                    continue;
                }

                var relations = await RestAsync("IssueRelations", LinearQueries.IssueRelations, issue.Id, issue.Relations, i => i.Relations, ct);
                var attachments = await RestAsync("IssueAttachments", LinearQueries.IssueAttachments, issue.Id, issue.Attachments, i => i.Attachments, ct);
                var history = await RestAsync("IssueHistory", LinearQueries.IssueHistory, issue.Id, issue.History, i => i.History, ct);
                item = item with { Links = LinearReading.Links(attachments) };
                var related = reading.Related(issue, item, relations, attachments).ToList();
                var changes = reading.Changes(item, history).ToList();
                foreach (var person in reading.TakePeople())
                {
                    yield return person;
                }

                yield return item;
                foreach (var record in related)
                {
                    yield return record;
                }

                foreach (var change in changes)
                {
                    yield return change;
                }
            }

            // Comments are paged per team rather than per issue: far fewer requests against the hourly limit. With a
            // cursor, only comments written or edited since then.
            var commentFilter = Since(new JsonObject { ["issue"] = new JsonObject { ["team"] = IdEq(team.Id) } }, since);
            await foreach (var comment in PagedAsync("Comments", LinearQueries.Comments, commentFilter, d => d.Comments, ct))
            {
                reading.Saw(comment.UpdatedAt);
                if (comment.IssueId is null || reading.Skipped(comment.IssueId))
                {
                    continue;
                }

                var records = reading.Comment(comment).ToList();
                foreach (var person in reading.TakePeople())
                {
                    yield return person;
                }

                foreach (var record in records)
                {
                    yield return record;
                }
            }
        }

        yield return new ReadCursor(reading.Newest?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? cursor);
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url)
            ? _client.DownloadAsync(url, ct)
            : throw new SourceAccessException($"Attachment {attachment.Id} ({attachment.FileName}) has no URL.");

    private static JsonObject IdEq(string id) => new() { ["id"] = new JsonObject { ["eq"] = id } };

    private static JsonObject IdIn(IEnumerable<string> ids) =>
        new() { ["id"] = new JsonObject { ["in"] = new JsonArray([.. ids.Select(id => (JsonNode)id)]) } };

    private static JsonObject Since(JsonObject filter, DateTimeOffset? since)
    {
        if (since is { } time)
        {
            filter["updatedAt"] = new JsonObject { ["gte"] = time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) };
        }

        return filter;
    }

    /// <summary>Every node of a root connection, page by page with <c>after</c>.</summary>
    private async IAsyncEnumerable<T> PagedAsync<T>(
        string operation, string query, JsonObject? filter, Func<LinearData, LinearConnection<T>?> select, [EnumeratorCancellation] CancellationToken ct)
    {
        string? after = null;
        while (true)
        {
            var variables = new JsonObject();
            if (filter is not null)
            {
                variables["filter"] = filter.DeepClone();
            }

            if (after is not null)
            {
                variables["after"] = after;
            }

            var page = select(await _client.QueryAsync(operation, query, variables, ct))
                ?? throw new SourceAccessException($"Linear answered {operation} without its data.");
            foreach (var node in page.Nodes)
            {
                yield return node;
            }

            if (!page.PageInfo.HasNextPage || page.PageInfo.EndCursor is null || page.PageInfo.EndCursor == after)
            {
                yield break;
            }

            after = page.PageInfo.EndCursor;
        }
    }

    /// <summary>A nested connection in full: the first page came with the issue, the rest is asked for on its own.</summary>
    private async Task<List<T>> RestAsync<T>(
        string operation, string query, string issueId, LinearConnection<T> first, Func<LinearIssue, LinearConnection<T>> select, CancellationToken ct)
    {
        var nodes = new List<T>(first.Nodes);
        var after = first.PageInfo.HasNextPage ? first.PageInfo.EndCursor : null;
        while (after is not null)
        {
            var data = await _client.QueryAsync(operation, query, new JsonObject { ["id"] = issueId, ["after"] = after }, ct);
            var page = select(data.Issue ?? throw new SourceAccessException($"Linear answered {operation} without the issue.", HttpStatusCode.NotFound));
            nodes.AddRange(page.Nodes);
            after = page.PageInfo.HasNextPage && page.PageInfo.EndCursor != after ? page.PageInfo.EndCursor : null;
        }

        return nodes;
    }

    private async Task<Catalog> CatalogAsync(CancellationToken ct) => _catalog ??= await LoadCatalogAsync(ct);

    private async Task<Catalog> LoadCatalogAsync(CancellationToken ct)
    {
        var organization = (await _client.QueryAsync("Organization", LinearQueries.Organization, null, ct)).Organization
            ?? throw new SourceAccessException("Linear answered without the workspace.");
        var teams = await PagedAsync("Teams", LinearQueries.Teams, null, d => d.Teams, ct).ToListAsync(ct);
        var states = await PagedAsync("WorkflowStates", LinearQueries.WorkflowStates, null, d => d.WorkflowStates, ct).ToListAsync(ct);
        var users = await PagedAsync("Users", LinearQueries.Users, null, d => d.Users, ct).ToListAsync(ct);
        var labels = await PagedAsync("IssueLabels", LinearQueries.IssueLabels, null, d => d.IssueLabels, ct).ToListAsync(ct);
        var projectStatuses = await PagedAsync("ProjectStatuses", LinearQueries.ProjectStatuses, null, d => d.ProjectStatuses, ct).ToListAsync(ct);
        return new Catalog(organization, teams, states, users, labels, projectStatuses);
    }

    /// <summary>What Describe found, kept for the read: teams, states, people and label names.</summary>
    internal sealed class Catalog
    {
        public Catalog(
            LinearOrganization organization, List<LinearTeam> teams, List<LinearState> states, List<LinearUser> users,
            List<LinearLabel> labels, List<LinearProjectStatus> projectStatuses)
        {
            BaseUrl = $"https://linear.app/{organization.UrlKey}";
            Teams = teams;
            TeamsById = teams.ToDictionary(t => t.Id, StringComparer.Ordinal);
            States = states.ToDictionary(s => s.Id, StringComparer.Ordinal);
            Users = users.ToDictionary(u => u.Id, StringComparer.Ordinal);

            // A label in a group is shown as "Group/Label", as Linear's filters name it; groups are never on an issue.
            var byId = labels.ToDictionary(l => l.Id, StringComparer.Ordinal);
            foreach (var label in labels.Where(l => !l.IsGroup))
            {
                Labels[label.Id] = label.Parent is { } parent && byId.TryGetValue(parent.Id, out var group) ? $"{group.Name}/{label.Name}" : label.Name;
            }

            var order = teams.Select((t, i) => (t.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
            Description = new SourceDescription
            {
                Source = new SnapshotSource { Kind = SourceKinds.Linear, BaseUrl = BaseUrl },
                Projects = [.. teams.Where(t => t.ArchivedAt is null).Select(t => new SourceProject
                {
                    Id = t.Id,
                    Key = t.Key,
                    Name = t.Name,
                    Url = $"{BaseUrl}/team/{t.Key}",
                })],
                People = [.. users.Select(Linear.Person)],
                ItemTypes =
                [
                    new SourceItemType { Id = ProjectType, Name = "Project", Level = 2 },
                    new SourceItemType { Id = IssueType, Name = "Issue", Level = 1 },
                ],
                Statuses =
                [
                    .. states
                        .Where(s => s.Team is not null && order.ContainsKey(s.Team.Id))
                        .OrderBy(s => order[s.Team!.Id]).ThenBy(s => s.Position)
                        .Select(s => new SourceStatus { Id = s.Id, Name = s.Name, Category = s.Type, ItemType = IssueType, Project = s.Team!.Id }),
                    .. projectStatuses.OrderBy(s => s.Position)
                        .Select(s => new SourceStatus { Id = s.Id, Name = s.Name, Category = s.Type, ItemType = ProjectType }),
                ],
                Fields =
                [
                    new SourceField { Id = ProjectField, Name = "Project", Kind = FieldKind.Select },
                    new SourceField { Id = InitiativesField, Name = "Initiatives", Kind = FieldKind.MultiSelect },
                ],
            };
        }

        public string BaseUrl { get; }
        public List<LinearTeam> Teams { get; }
        public Dictionary<string, LinearTeam> TeamsById { get; }
        public Dictionary<string, LinearState> States { get; }
        public Dictionary<string, LinearUser> Users { get; }

        /// <summary>Label names by ID, group-qualified.</summary>
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

        public SourceDescription Description { get; }
    }
}
