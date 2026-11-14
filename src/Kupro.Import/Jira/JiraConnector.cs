using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Jira;

/// <summary>
/// Jira Cloud and Data Center/Server (task 024, ADR-019): one connector, two API dialects. Cloud speaks platform API v3
/// (ADF markup, token-paged <c>search/jql</c>), Data Center v2 (wiki markup, <c>startAt</c> paging); which one comes
/// from <c>serverInfo</c>. Reads projects, issues with hierarchy, sprints, versions, links, comments, attachments and the
/// changelog into the snapshot model, in Jira's own terms. Mapping onto Kupro is the pipeline's job.
/// </summary>
public sealed class JiraConnector : ISourceConnector
{
    /// <summary>Issues per search page, changelog entries and comments per page.</summary>
    internal const int PageSize = 100;

    internal const string SprintSchema = "com.pyxis.greenhopper.jira:gh-sprint";
    internal const string EpicLinkSchema = "com.pyxis.greenhopper.jira:gh-epic-link";

    /// <summary>Sums of the worklogs (time tracking is out of v1), in hours, for the details block.</summary>
    internal const string TimeLoggedField = "timespent";

    internal const string ComponentsField = "components";
    internal const string ResolutionField = "resolution";

    private static readonly string[] StoryPointNames = ["Story Points", "Story point estimate"];
    private static readonly string[] StartDateNames = ["Start date", "Target start"];

    /// <summary>System fields every search asks for.</summary>
    private static readonly string[] SystemFields =
    [
        "summary", "description", "issuetype", "status", "priority", "assignee", "reporter", "creator", "created", "updated",
        "resolutiondate", "resolution", "labels", "components", "fixVersions", "duedate", "timeoriginalestimate", "timespent",
        "parent", "issuelinks", "attachment", "comment",
    ];

    private readonly JiraClient _client;
    private readonly TimeProvider _time;
    private Catalog? _catalog;

    /// <param name="http">Sends the requests; the connector adds the token to each one, not to the client.</param>
    /// <param name="url">The site: <c>https://acme.atlassian.net</c>, or a Data Center base URL with its context path.</param>
    /// <param name="token"><c>email:api-token</c> on Cloud; a personal access token on Data Center and Server.</param>
    public JiraConnector(HttpClient http, string url, string token, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _client = new JiraClient(http, NormalizeUrl(url), token.Trim(), _time);
    }

    internal JiraClient Client => _client;

    /// <summary>
    /// The site from what the owner pasted. An issue, board or REST URL works too: Cloud keeps the host, Data Center
    /// keeps the context path before the first page segment (<c>/jira/browse/X-1</c> → <c>/jira</c>).
    /// </summary>
    public static Uri NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("The Jira URL must be an http(s) address.", nameof(url));
        }

        if (uri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri($"https://{uri.Host}");
        }

        string[] pages = ["browse", "secure", "projects", "issues", "rest", "plugins", "login.jsp", "servicedesk", "dashboards"];
        var kept = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(s => !pages.Contains(s, StringComparer.OrdinalIgnoreCase));
        var path = string.Join('/', kept);
        return new Uri($"{uri.Scheme}://{uri.Authority}{(path.Length == 0 ? "" : "/" + path)}");
    }

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        // The next delta starts when this read did: anything changed while it ran is read again, nothing is missed.
        var started = _time.GetUtcNow();
        var catalog = await CatalogAsync(ct);
        DateTimeOffset? since = cursor is null
            ? null
            : DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{cursor}' is not a Jira cursor (a UTC time).", nameof(cursor));
        var reading = new JiraReading(catalog, _client.Site);
        var zone = since is null ? null : await ZoneAsync(catalog, ct);
        var sprints = new Dictionary<string, SourceSprint>(StringComparer.Ordinal);
        var projects = scope.Projects.Count == 0
            ? catalog.Projects
            : catalog.Projects.Where(p => scope.Projects.Contains(p.Id, StringComparer.Ordinal)).ToList();
        foreach (var project in projects)
        {
            var versions = await _client.GetAsync($"rest/api/{catalog.Api}/project/{project.Id}/versions", JiraJson.Default.ListJiraVersion, ct);
            foreach (var version in versions)
            {
                yield return new SourceVersion
                {
                    Id = version.Id,
                    Project = project.Id,
                    Name = version.Name,
                    Date = Jira.Day(version.ReleaseDate),
                    Released = version.Released,
                };
            }

            var jql = $"project = {project.Id}";
            if (since is { } from)
            {
                jql += $" AND updated >= {JqlTime(from, zone, started)}";
            }

            // Closed before the cutoff is left out by Jira, not after fetching its history; a day early, as JQL reads
            // the date in the user's zone, and the item check below is exact.
            if (scope.ResolvedAfter is { } cutoff)
            {
                jql += string.Create(CultureInfo.InvariantCulture, $" AND (resolutiondate is EMPTY OR resolutiondate >= \"{cutoff.UtcDateTime.AddDays(-1):yyyy-MM-dd}\")");
            }

            // Key order is stable while issues change, so startAt paging neither skips nor repeats one.
            jql += " ORDER BY key ASC";
            await foreach (var issue in SearchAsync(catalog, jql, ct))
            {
                var histories = await HistoriesAsync(catalog, issue, ct);
                var comments = await CommentsAsync(catalog, issue, ct);
                var epic = await EpicAsync(catalog, reading, issue, ct);
                var sprint = await SprintAsync(catalog, issue, sprints, ct);
                if (sprint.New is { } newSprint)
                {
                    yield return newSprint;
                }

                var item = reading.Item(project, issue, epic, sprint.Id);
                if (scope.ResolvedAfter is not null && item.Resolved < scope.ResolvedAfter)
                {
                    continue;
                }

                item.Links = await RemoteLinksAsync(catalog, issue, ct);

                var attachments = reading.Attachments(issue, item).ToList();
                var relations = reading.Relations(issue, item).ToList();
                var files = Jira.Array(issue.Fields, "attachment").ToList();
                var sourceComments = comments.Select(c => reading.Comment(item, c, files)).ToList();
                var changes = reading.Changes(item, histories).ToList();
                foreach (var person in reading.TakePeople())
                {
                    yield return person;
                }

                yield return item;
                foreach (var attachment in attachments)
                {
                    yield return attachment;
                }

                foreach (var relation in relations)
                {
                    yield return relation;
                }

                foreach (var comment in sourceComments)
                {
                    yield return comment;
                }

                foreach (var change in changes)
                {
                    yield return change;
                }
            }
        }

        yield return new ReadCursor(started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url)
            ? _client.DownloadAsync(url, ct)
            : throw new SourceAccessException($"Attachment {attachment.Id} ({attachment.FileName}) has no URL.");

    /// <summary>
    /// A fixed point in time as JQL takes it: in the token user's zone, a minute early (JQL has no seconds). Relative
    /// (<c>"-90m"</c>) only when the zone is unknown: Jira works that out again on every page, so the window moves.
    /// </summary>
    internal static string JqlTime(DateTimeOffset at, TimeZoneInfo? zone, DateTimeOffset now)
    {
        if (zone is null)
        {
            var minutes = (long)Math.Ceiling((now - at).TotalMinutes) + 1;
            return $"\"-{Math.Max(minutes, 1)}m\"";
        }

        return string.Create(CultureInfo.InvariantCulture, $"\"{TimeZoneInfo.ConvertTime(at.AddMinutes(-1), zone):yyyy-MM-dd HH:mm}\"");
    }

    private async Task<TimeZoneInfo?> ZoneAsync(Catalog catalog, CancellationToken ct)
    {
        var me = await _client.TryGetAsync($"rest/api/{catalog.Api}/myself", JiraJson.Default.JiraMyself, ct);
        return me?.TimeZone is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    /// <summary>
    /// Every issue the JQL finds: Cloud pages by token (<c>search/jql</c>), Data Center by <c>startAt</c>. POST, as a
    /// site's custom fields can make the field list longer than a URL may be.
    /// </summary>
    private async IAsyncEnumerable<JiraIssue> SearchAsync(Catalog catalog, string jql, [EnumeratorCancellation] CancellationToken ct)
    {
        var fields = new JsonArray([.. catalog.SearchFields.Split(',').Select(f => (JsonNode)f)]);
        string? token = null;
        var startAt = 0;
        while (true)
        {
            var body = new JsonObject { ["jql"] = jql, ["maxResults"] = PageSize, ["fields"] = fields.DeepClone() };
            if (catalog.Cloud)
            {
                body["expand"] = "changelog";
                if (token is not null)
                {
                    body["nextPageToken"] = token;
                }
            }
            else
            {
                body["expand"] = new JsonArray("changelog");
                body["startAt"] = startAt;
            }

            var page = await _client.PostAsync(catalog.Cloud ? "rest/api/3/search/jql" : "rest/api/2/search", body, JiraJson.Default.JiraSearchPage, ct);
            foreach (var issue in page.Issues)
            {
                yield return issue;
            }

            if (catalog.Cloud)
            {
                token = page.NextPageToken;
                if (token is null || page.IsLast == true || page.Issues.Count == 0)
                {
                    yield break;
                }
            }
            else
            {
                startAt = page.StartAt + page.Issues.Count;
                if (page.Issues.Count == 0 || startAt >= (page.Total ?? int.MaxValue))
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>The whole changelog. A search embeds only the first page of a long one; the rest is paged in.</summary>
    private async Task<IReadOnlyList<JiraHistory>> HistoriesAsync(Catalog catalog, JiraIssue issue, CancellationToken ct)
    {
        var embedded = issue.Changelog;
        if (embedded is null || (embedded.StartAt == 0 && embedded.Histories.Count >= embedded.Total))
        {
            return embedded?.Histories ?? [];
        }

        if (!catalog.Cloud)
        {
            var full = await _client.GetAsync($"rest/api/2/issue/{issue.Id}?fields=created&expand=changelog", JiraJson.Default.JiraIssue, ct);
            return full.Changelog?.Histories ?? [];
        }

        var histories = new List<JiraHistory>();
        for (var startAt = 0; ;)
        {
            var page = await _client.GetAsync(
                $"rest/api/3/issue/{issue.Id}/changelog?startAt={startAt}&maxResults={PageSize}", JiraJson.Default.JiraChangelogPage, ct);
            histories.AddRange(page.Values);
            startAt += page.Values.Count;
            if (page.IsLast || page.Values.Count == 0 || startAt >= page.Total)
            {
                return histories;
            }
        }
    }

    /// <summary>All comments. A search embeds a page of them; the rest is paged in.</summary>
    private async Task<IReadOnlyList<JiraComment>> CommentsAsync(Catalog catalog, JiraIssue issue, CancellationToken ct)
    {
        var embedded = issue.Fields.TryGetValue("comment", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(JiraJson.Default.JiraCommentPage)
            : null;
        if (embedded is not null && embedded.StartAt == 0 && embedded.Comments.Count >= embedded.Total)
        {
            return embedded.Comments;
        }

        var comments = new List<JiraComment>();
        for (var startAt = 0; ;)
        {
            var page = await _client.GetAsync(
                $"rest/api/{catalog.Api}/issue/{issue.Id}/comment?startAt={startAt}&maxResults={PageSize}&orderBy=created", JiraJson.Default.JiraCommentPage, ct);
            comments.AddRange(page.Comments);
            startAt += page.Comments.Count;
            if (page.Comments.Count == 0 || startAt >= page.Total)
            {
                return comments;
            }
        }
    }

    /// <summary>
    /// The epic's issue ID from the <c>Epic Link</c> field (Data Center, and old Cloud data), which holds its key.
    /// Keys seen in this read resolve for free; others are looked up once.
    /// </summary>
    private async Task<string?> EpicAsync(Catalog catalog, JiraReading reading, JiraIssue issue, CancellationToken ct)
    {
        if (catalog.EpicLinkField is not { } field || Jira.Text(issue.Fields, field) is not { Length: > 0 } key)
        {
            return null;
        }

        if (reading.IdsByKey.TryGetValue(key, out var id))
        {
            return id;
        }

        var found = await _client.TryGetAsync($"rest/api/{catalog.Api}/issue/{Uri.EscapeDataString(key)}?fields=summary", JiraJson.Default.JiraIssueRef, ct);
        reading.IdsByKey[key] = found?.Id;
        return found?.Id;
    }

    /// <summary>
    /// The issue's sprint: the active one, else the next future one, else the last closed one. A sprint first seen here
    /// is returned to stream; one the field holds without dates is completed from the Agile API.
    /// </summary>
    private async Task<(string? Id, SourceSprint? New)> SprintAsync(
        Catalog catalog, JiraIssue issue, Dictionary<string, SourceSprint> known, CancellationToken ct)
    {
        if (catalog.SprintField is not { } field || !issue.Fields.TryGetValue(field, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        var listed = value.EnumerateArray().Select(Jira.Sprint).OfType<JiraSprint>().ToList();
        if (listed.Count == 0)
        {
            return (null, null);
        }

        var pick = listed.FirstOrDefault(s => Is(s, "active"))
            ?? listed.Where(s => Is(s, "future")).OrderBy(s => s.Id).FirstOrDefault()
            ?? listed.OrderBy(s => Jira.Time(s.EndDate) ?? DateTimeOffset.MinValue).ThenBy(s => s.Id).Last();
        var id = pick.Id.ToString(CultureInfo.InvariantCulture);
        if (known.ContainsKey(id))
        {
            return (id, null);
        }

        if (!Is(pick, "future") && (pick.StartDate is null || pick.EndDate is null)
            && await _client.TryGetAsync($"rest/agile/1.0/sprint/{id}", JiraJson.Default.JiraSprint, ct) is { } full)
        {
            pick = full;
        }

        var sprint = new SourceSprint
        {
            Id = id,
            Name = pick.Name ?? $"Sprint {id}",
            Start = Jira.Day(pick.StartDate),
            End = Jira.Day(pick.EndDate),
            Closed = Is(pick, "closed"),
        };
        known[id] = sprint;
        return (id, sprint);

        static bool Is(JiraSprint sprint, string state) => string.Equals(sprint.State, state, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Catalog> CatalogAsync(CancellationToken ct) => _catalog ??= await LoadCatalogAsync(ct);

    private async Task<Catalog> LoadCatalogAsync(CancellationToken ct)
    {
        var info = await _client.GetAsync("rest/api/2/serverInfo", JiraJson.Default.JiraServerInfo, ct);
        var cloud = string.Equals(info.DeploymentType, "Cloud", StringComparison.OrdinalIgnoreCase);
        if (cloud && !_client.Basic)
        {
            throw new SourceAccessException(
                "Jira Cloud signs in with your Atlassian email and an API token. Paste them as email:token.", System.Net.HttpStatusCode.Unauthorized);
        }

        var api = cloud ? "3" : "2";
        var projects = new List<JiraProject>();
        if (cloud)
        {
            for (var startAt = 0; ; startAt += 50)
            {
                var page = await _client.GetAsync($"rest/api/3/project/search?startAt={startAt}&maxResults=50", JiraJson.Default.JiraProjectPage, ct);
                projects.AddRange(page.Values);
                if (page.IsLast || page.Values.Count == 0)
                {
                    break;
                }
            }
        }
        else
        {
            projects.AddRange(await _client.GetAsync("rest/api/2/project", JiraJson.Default.ListJiraProject, ct));
        }

        var types = await _client.GetAsync($"rest/api/{api}/issuetype", JiraJson.Default.ListJiraIssueType, ct);
        var workflows = new Dictionary<string, List<JiraTypeStatuses>>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            workflows[project.Id] = await _client.GetAsync($"rest/api/{api}/project/{project.Id}/statuses", JiraJson.Default.ListJiraTypeStatuses, ct);
        }

        var fields = await _client.GetAsync($"rest/api/{api}/field", JiraJson.Default.ListJiraField, ct);
        return new Catalog(_client.Site, cloud, projects, types, workflows, fields, await ResolutionsAsync(cloud, ct));
    }

    /// <summary>The resolution names, so the mapping can say which ones cancel. Cloud deprecated the unpaged list.</summary>
    private async Task<List<string>> ResolutionsAsync(bool cloud, CancellationToken ct)
    {
        if (!cloud)
        {
            return [.. (await _client.GetAsync("rest/api/2/resolution", JiraJson.Default.ListJiraResolution, ct)).Select(r => r.Name)];
        }

        var names = new List<string>();
        while (true)
        {
            // Jira may return fewer than asked, so the next page starts after what came back.
            var page = await _client.GetAsync($"rest/api/3/resolution/search?startAt={names.Count}&maxResults=50", JiraJson.Default.JiraResolutionPage, ct);
            names.AddRange(page.Values.Select(r => r.Name));
            if (page.IsLast || page.Values.Count == 0)
            {
                return names;
            }
        }
    }

    /// <summary>
    /// Confluence pages and web links: one call per issue, since a search doesn't include them. An instance that hides
    /// them (404, 403) gives none.
    /// </summary>
    private async Task<IReadOnlyList<SourceLink>> RemoteLinksAsync(Catalog catalog, JiraIssue issue, CancellationToken ct)
    {
        var links = await _client.TryGetAsync($"rest/api/{catalog.Api}/issue/{issue.Id}/remotelink", JiraJson.Default.ListJiraRemoteLink, ct);
        return [.. (links ?? [])
            .Where(l => Uri.TryCreate(l.Object?.Url, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
            .Select(l => new SourceLink { Url = l.Object!.Url!, Title = string.IsNullOrWhiteSpace(l.Object.Title) ? null : l.Object.Title })];
    }

    /// <summary>What Describe found, kept for the read: dialect, projects, and which custom fields mean what.</summary>
    internal sealed class Catalog
    {
        public Catalog(
            Uri site, bool cloud, List<JiraProject> projects, List<JiraIssueType> types,
            Dictionary<string, List<JiraTypeStatuses>> workflows, List<JiraField> fields, List<string> resolutions)
        {
            Cloud = cloud;
            Projects = projects;
            SprintField = fields.FirstOrDefault(f => f.Schema?.Custom == SprintSchema)?.Id;
            EpicLinkField = fields.FirstOrDefault(f => f.Schema?.Custom == EpicLinkSchema)?.Id;
            StoryPointFields = [.. StoryPointNames.SelectMany(n => fields.Where(f => f.Custom && f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).Select(f => f.Id)];
            StartDateFields = [.. StartDateNames.SelectMany(n => fields.Where(f => f.Custom && f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).Select(f => f.Id)];
            FieldIdsByName = fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            var handled = new HashSet<string>([.. StoryPointFields, .. StartDateFields], StringComparer.Ordinal);
            if (SprintField is not null)
            {
                handled.Add(SprintField);
            }

            if (EpicLinkField is not null)
            {
                handled.Add(EpicLinkField);
            }

            Exported = fields
                .Where(f => f.Custom && !handled.Contains(f.Id) && Jira.Kind(f) is not null
                    && !(f.Schema?.Custom ?? "").StartsWith("com.pyxis.greenhopper.jira:", StringComparison.Ordinal))
                .ToDictionary(f => f.Id, StringComparer.Ordinal);
            SearchFields = string.Join(',', SystemFields.Concat(handled).Concat(Exported.Keys));

            var levels = types.ToDictionary(t => t.Id, Jira.Level, StringComparer.Ordinal);
            var itemTypes = new Dictionary<string, (JiraTypeStatuses Type, List<string> Projects)>(StringComparer.Ordinal);
            var statuses = new Dictionary<string, (JiraStatus Status, List<string> Projects)>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                foreach (var type in workflows.GetValueOrDefault(project.Id) ?? [])
                {
                    if (!itemTypes.TryGetValue(type.Id, out var seen))
                    {
                        itemTypes[type.Id] = seen = (type, []);
                    }

                    seen.Projects.Add(project.Id);
                    foreach (var status in type.Statuses)
                    {
                        if (!statuses.TryGetValue(status.Id, out var known))
                        {
                            statuses[status.Id] = known = (status, []);
                        }

                        if (!known.Projects.Contains(project.Id))
                        {
                            known.Projects.Add(project.Id);
                        }
                    }
                }
            }

            var siteUrl = site.AbsoluteUri.TrimEnd('/');
            Description = new SourceDescription
            {
                Source = new SnapshotSource { Kind = SourceKinds.Jira, BaseUrl = siteUrl },
                Projects = [.. projects.Select(p => new SourceProject
                {
                    Id = p.Id,
                    Key = p.Key,
                    Name = p.Name,
                    Url = $"{siteUrl}/browse/{p.Key}",
                    Process = p.ProjectTypeKey,
                })],
                ItemTypes = [.. itemTypes.Values.Select(t => new SourceItemType
                {
                    Id = t.Type.Id,
                    Name = t.Type.Name,
                    Project = t.Projects.Count == 1 ? t.Projects[0] : null,
                    Level = levels.TryGetValue(t.Type.Id, out var level) ? level : Jira.Level(new JiraIssueType { Id = t.Type.Id, Name = t.Type.Name, Subtask = t.Type.Subtask }),
                })],
                Statuses = [.. statuses.Values.Select(s => new SourceStatus
                {
                    Id = s.Status.Id,
                    Name = s.Status.Name,
                    Category = s.Status.StatusCategory?.Key,
                    Project = s.Projects.Count == 1 ? s.Projects[0] : null,
                })],
                Fields =
                [
                    new SourceField { Id = ComponentsField, Name = "Components", Kind = FieldKind.MultiSelect },
                    new SourceField { Id = ResolutionField, Name = "Resolution", Kind = FieldKind.Select, Options = resolutions },
                    new SourceField { Id = TimeLoggedField, Name = "Time logged (hours)", Kind = FieldKind.Number },
                    .. Exported.Values.Select(f => new SourceField { Id = f.Id, Name = f.Name, Kind = Jira.Kind(f)!.Value }),
                ],
            };
        }

        public bool Cloud { get; }

        /// <summary>The platform API version: 3 on Cloud (ADF), 2 on Data Center and Server (wiki markup).</summary>
        public string Api => Cloud ? "3" : "2";

        public List<JiraProject> Projects { get; }
        public string? SprintField { get; }
        public string? EpicLinkField { get; }
        public IReadOnlyList<string> StoryPointFields { get; }
        public IReadOnlyList<string> StartDateFields { get; }

        /// <summary>Field names to IDs, for changelogs from servers that name the field only.</summary>
        public Dictionary<string, string> FieldIdsByName { get; }

        /// <summary>Custom fields that go to the details block or labels, by ID.</summary>
        public Dictionary<string, JiraField> Exported { get; }

        public string SearchFields { get; }
        public SourceDescription Description { get; }
    }
}
