using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Kupro.Import.Snapshot;

namespace Kupro.Import.AzureDevOps;

/// <summary>
/// Azure DevOps Boards, Services and Server (task 025, ADR-019). Reads projects, work items with their fields,
/// hierarchy and links, iterations, comments, attachments and revisions into the snapshot model. Mapping onto Kupro
/// (types, states, folding the four levels, markup) is the pipeline's job; this class only reads, and reports what it
/// read in the source's own terms: type and state names, state categories, backlog levels, the process name.
/// </summary>
public sealed class AzureDevOpsConnector : ISourceConnector
{
    /// <summary>WIQL returns at most 20,000 IDs per query, so items are read in ID ranges of this size.</summary>
    internal const int WiqlPage = 20_000;

    /// <summary>The most IDs <c>workitemsbatch</c> takes per call.</summary>
    internal const int BatchSize = 200;

    private const int HistoryConcurrency = 4;

    private static readonly Dictionary<string, string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b8a3a935-7e91-48b8-a94c-606d37c3e9f2"] = "Basic",
        ["adcc42ab-9882-485e-a3ed-7678f01f66bc"] = "Agile",
        ["6b724908-ef14-45cf-84f8-768b5384da45"] = "Scrum",
        ["27450541-8e31-4150-9947-dc59f998fc01"] = "CMMI",
    };

    private const string AreaPath = "System.AreaPath";
    private const string IterationPath = "System.IterationPath";

    /// <summary>Fields that become <see cref="SourceItem"/> properties or the description, so not <see cref="SourceField"/>s.</summary>
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.VSTS.Common.Priority",
        "Microsoft.VSTS.Common.AcceptanceCriteria",
        "Microsoft.VSTS.TCM.ReproSteps",
        "Microsoft.VSTS.Scheduling.OriginalEstimate",
        "Microsoft.VSTS.Scheduling.RemainingWork",
        "Microsoft.VSTS.Scheduling.CompletedWork",
        "Microsoft.VSTS.Scheduling.StoryPoints",
        "Microsoft.VSTS.Scheduling.Effort",
        "Microsoft.VSTS.Scheduling.Size",
        "Microsoft.VSTS.Scheduling.StartDate",
        "Microsoft.VSTS.Scheduling.TargetDate",
        "Microsoft.VSTS.Scheduling.FinishDate",
        "Microsoft.VSTS.Scheduling.DueDate",
    };

    /// <summary>Bookkeeping Azure DevOps sets by itself: ordering, state timestamps, board columns, test internals.</summary>
    private static bool IsNoise(string field) =>
        field.StartsWith("WEF_", StringComparison.OrdinalIgnoreCase)
        || field.StartsWith("Microsoft.VSTS.TCM.", StringComparison.OrdinalIgnoreCase)
        || field.StartsWith("Microsoft.VSTS.CodeReview.", StringComparison.OrdinalIgnoreCase)
        || field.StartsWith("Microsoft.VSTS.Feedback.", StringComparison.OrdinalIgnoreCase)
        || field is "Microsoft.VSTS.Common.StackRank" or "Microsoft.VSTS.Common.BacklogPriority"
            or "Microsoft.VSTS.Common.StateChangeDate" or "Microsoft.VSTS.Common.ActivatedDate"
            or "Microsoft.VSTS.Common.ActivatedBy" or "Microsoft.VSTS.Common.ResolvedDate"
            or "Microsoft.VSTS.Common.ResolvedBy" or "Microsoft.VSTS.Common.ClosedDate"
            or "Microsoft.VSTS.Common.ClosedBy";

    private readonly AzureDevOpsClient _client;
    private readonly TimeProvider _time;
    private Catalog? _catalog;

    /// <param name="http">Sends the requests; the connector adds the PAT to each one, not to the client.</param>
    /// <param name="url">The organization (<c>https://dev.azure.com/acme</c>) or Server collection URL.</param>
    /// <param name="token">A PAT with Work Items (Read) and Project and Team (Read).</param>
    public AzureDevOpsConnector(HttpClient http, string url, string token, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _client = new AzureDevOpsClient(http, NormalizeUrl(url), token, _time);
    }

    internal AzureDevOpsClient Client => _client;

    /// <summary>
    /// The collection URL from what the owner pasted: <c>dev.azure.com</c> keeps only the organization, so a project or
    /// work item URL also works. On Server a page URL (<c>/tfs/Main/Web/_workitems</c>) loses the page and the project.
    /// </summary>
    public static Uri NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("The Azure DevOps URL must be an http(s) address.", nameof(url));
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length == 0
                ? throw new ArgumentException("A dev.azure.com URL needs the organization: https://dev.azure.com/<org>.", nameof(url))
                : new Uri($"https://dev.azure.com/{segments[0]}");
        }

        var kept = segments.TakeWhile(s => !s.StartsWith('_')).ToList();
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            kept = [.. kept.Take(1).Where(s => s.Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))];
        }
        else if (kept.Count < segments.Length && kept.Count(s => !s.Equals("tfs", StringComparison.OrdinalIgnoreCase)) >= 2)
        {
            // A page URL (…/Collection/Project/_workitems): the segment before the page is the project.
            kept.RemoveAt(kept.Count - 1);
        }

        var path = string.Join('/', kept);
        return new Uri($"{uri.Scheme}://{uri.Authority}{(path.Length == 0 ? "" : "/" + path)}");
    }

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        DateTimeOffset? since = cursor is null
            ? null
            : DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{cursor}' is not an Azure DevOps cursor (a UTC time).", nameof(cursor));
        var reading = new AzureDevOpsReading(catalog, _client.Collection);
        var projects = scope.Projects.Count == 0
            ? catalog.Projects
            : catalog.Projects.Where(p => scope.Projects.Contains(p.Id, StringComparer.Ordinal)).ToList();
        foreach (var project in projects)
        {
            foreach (var sprint in project.Sprints)
            {
                yield return sprint;
            }

            var after = 0;
            while (true)
            {
                var ids = await QueryIdsAsync(project, after, since, ct);
                foreach (var chunk in ids.Chunk(BatchSize))
                {
                    var batch = await _client.PostAsync(
                        "_apis/wit/workitemsbatch", new AdoBatchRequest { Ids = chunk },
                        AzureDevOpsJson.Default.AdoBatchRequest, AzureDevOpsJson.Default.AdoListAdoWorkItem, ct);
                    var items = new List<(AdoWorkItem Raw, SourceItem Item)>(chunk.Length);
                    foreach (var raw in batch.Value)
                    {
                        if (raw is null)
                        {
                            continue;
                        }

                        var item = reading.Item(project, raw);
                        reading.Newest = reading.Newest is { } newest && newest >= item.Updated ? newest : item.Updated;
                        if (scope.ResolvedAfter is null || item.Resolved is null || item.Resolved >= scope.ResolvedAfter)
                        {
                            items.Add((raw, item));
                        }
                    }

                    var histories = new History[items.Count];
                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, items.Count),
                        new ParallelOptions { MaxDegreeOfParallelism = HistoryConcurrency, CancellationToken = ct },
                        async (i, token) => histories[i] = await HistoryAsync(project, items[i].Raw, token));

                    for (var i = 0; i < items.Count; i++)
                    {
                        var (raw, item) = items[i];
                        var related = reading.Related(raw, item).ToList();
                        var comments = histories[i].Comments.Select(c => reading.Comment(item, c)).ToList();
                        var changes = reading.Changes(project, item, histories[i].Updates).ToList();
                        foreach (var person in reading.TakePeople())
                        {
                            yield return person;
                        }

                        yield return item;
                        foreach (var record in related)
                        {
                            yield return record;
                        }

                        foreach (var comment in comments)
                        {
                            yield return comment;
                        }

                        foreach (var change in changes)
                        {
                            yield return change;
                        }
                    }
                }

                if (ids.Count < WiqlPage)
                {
                    break;
                }

                after = ids[^1];
            }
        }

        yield return new ReadCursor(reading.Newest?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? cursor);
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url)
            ? _client.DownloadAsync(url, ct)
            : throw new SourceAccessException($"Attachment {attachment.Id} ({attachment.FileName}) has no URL.");

    /// <summary>The next ID range: up to <see cref="WiqlPage"/> IDs above <paramref name="after"/>, in ID order.</summary>
    private async Task<IReadOnlyList<int>> QueryIdsAsync(ProjectInfo project, int after, DateTimeOffset? since, CancellationToken ct)
    {
        var query = new StringBuilder().Append(CultureInfo.InvariantCulture, $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Id] > {after}");
        if (since is { } time)
        {
            query.Append(CultureInfo.InvariantCulture, $" AND [System.ChangedDate] >= '{time.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}'");
        }

        query.Append(" ORDER BY [System.Id] ASC");
        var result = await _client.PostAsync(
            $"{project.Id}/_apis/wit/wiql?$top={WiqlPage}&timePrecision=true", new AdoWiqlRequest(query.ToString()),
            AzureDevOpsJson.Default.AdoWiqlRequest, AzureDevOpsJson.Default.AdoWiqlResult, ct);
        return [.. result.WorkItems.Select(w => w.Id)];
    }

    private async Task<History> HistoryAsync(ProjectInfo project, AdoWorkItem item, CancellationToken ct)
    {
        // Revision 1 is the creation, which the item itself already holds. No comment count means an old server: ask.
        var updates = new List<AdoUpdate>();
        if (item.Rev > 1)
        {
            for (var skip = 0; ; skip += BatchSize)
            {
                var page = await _client.GetAsync(
                    $"_apis/wit/workItems/{item.Id}/updates?$top={BatchSize}&$skip={skip}", AzureDevOpsJson.Default.AdoListAdoUpdate, ct);
                updates.AddRange(page.Value);
                if (page.Value.Count < BatchSize)
                {
                    break;
                }
            }
        }

        var comments = new List<AdoComment>();
        if (Ado.Number(item.Fields, "System.CommentCount") is not 0)
        {
            var version = await _client.CommentsVersionAsync(ct);
            string? continuation = null;
            do
            {
                var path = $"{project.Id}/_apis/wit/workItems/{item.Id}/comments?$top={BatchSize}&api-version={version}"
                    + (continuation is null ? "" : $"&continuationToken={Uri.EscapeDataString(continuation)}");
                var page = await _client.GetAsync(path, AzureDevOpsJson.Default.AdoCommentList, ct);
                comments.AddRange(page.Comments);
                continuation = page.Comments.Count == 0 ? null : page.ContinuationToken;
            }
            while (continuation is not null);
        }

        return new History(updates, comments);
    }

    private async Task<Catalog> CatalogAsync(CancellationToken ct) => _catalog ??= await LoadCatalogAsync(ct);

    private async Task<Catalog> LoadCatalogAsync(CancellationToken ct)
    {
        var listed = new List<AdoProject>();
        for (var skip = 0; ; skip += 500)
        {
            var page = await _client.GetAsync($"_apis/projects?stateFilter=wellFormed&$top=500&$skip={skip}", AzureDevOpsJson.Default.AdoListAdoProject, ct);
            listed.AddRange(page.Value);
            if (page.Value.Count < 500)
            {
                break;
            }
        }

        var processes = new Dictionary<string, ProcessInfo>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectInfo>();
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        foreach (var listedProject in listed)
        {
            var detail = await _client.GetAsync($"_apis/projects/{listedProject.Id}?includeCapabilities=true", AzureDevOpsJson.Default.AdoProject, ct);
            var template = detail.Capabilities?.ProcessTemplate;
            var processId = template?.TemplateTypeId ?? detail.Id;
            if (!processes.TryGetValue(processId, out var process))
            {
                process = await LoadProcessAsync(detail, processId, template?.TemplateName, ct);
                processes[processId] = process;
            }

            var iterations = await _client.GetAsync($"{detail.Id}/_apis/wit/classificationnodes/Iterations?$depth=20", AzureDevOpsJson.Default.AdoClassificationNode, ct);
            var areas = await _client.GetAsync($"{detail.Id}/_apis/wit/classificationnodes/Areas?$depth=20", AzureDevOpsJson.Default.AdoClassificationNode, ct);
            projects.Add(new ProjectInfo(detail, process, Nodes(iterations), Nodes(areas), today));
        }

        var fields = await _client.GetAsync("_apis/wit/fields", AzureDevOpsJson.Default.AdoListAdoField, ct);
        return new Catalog(_client.Collection, projects, fields.Value.Where(Exported).ToList());
    }

    private async Task<ProcessInfo> LoadProcessAsync(AdoProject project, string processId, string? templateName, CancellationToken ct)
    {
        // Inherited processes name their parent; XML processes on Server have their own IDs, so fall back to the name.
        var name = SystemProcesses.GetValueOrDefault(processId);
        if (name is null && Guid.TryParse(processId, out _))
        {
            var process = await _client.TryGetAsync($"_apis/work/processes/{processId}", AzureDevOpsJson.Default.AdoProcess, ct);
            name = (process?.ParentProcessTypeId is { } parent ? SystemProcesses.GetValueOrDefault(parent) : null)
                ?? SystemProcesses.Values.FirstOrDefault(n => n.Equals(process?.Name ?? templateName, StringComparison.OrdinalIgnoreCase));
        }

        var types = await _client.GetAsync($"{project.Id}/_apis/wit/workitemtypes", AzureDevOpsJson.Default.AdoListAdoWorkItemType, ct);
        var backlogs = await _client.TryGetAsync($"{project.Id}/_apis/work/backlogconfiguration", AzureDevOpsJson.Default.AdoBacklogConfiguration, ct);
        return new ProcessInfo(processId, name ?? templateName ?? "Unknown", types.Value, Levels(backlogs));
    }

    /// <summary>Backlog levels: tasks 0, requirements 1, portfolio backlogs upwards by rank (Features 2, Epics 3).</summary>
    private static Dictionary<string, int> Levels(AdoBacklogConfiguration? backlogs)
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (backlogs is null)
        {
            return levels;
        }

        void Add(AdoBacklog? backlog, int level)
        {
            foreach (var type in backlog?.WorkItemTypes ?? [])
            {
                levels.TryAdd(type.Name, level);
            }
        }

        Add(backlogs.TaskBacklog, 0);
        Add(backlogs.RequirementBacklog, 1);
        var level = 2;
        foreach (var portfolio in backlogs.PortfolioBacklogs.OrderBy(b => b.Rank))
        {
            Add(portfolio, level++);
        }

        return levels;
    }

    /// <summary>Every node of a classification tree with its work-item path (<c>Project\Team\Sprint 1</c>).</summary>
    private static List<Node> Nodes(AdoClassificationNode root)
    {
        var nodes = new List<Node>();
        void Walk(AdoClassificationNode node, string path)
        {
            nodes.Add(new Node(node.Id, node.Name, path, Ado.Day(node.Attributes?.StartDate), Ado.Day(node.Attributes?.FinishDate)));
            foreach (var child in node.Children)
            {
                Walk(child, $"{path}\\{child.Name}");
            }
        }

        Walk(root, root.Name);
        return nodes;
    }

    private static bool Exported(AdoField field) =>
        field.ReferenceName is AreaPath or IterationPath
        || (!field.ReadOnly
            && !field.ReferenceName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            && !Handled.Contains(field.ReferenceName)
            && !IsNoise(field.ReferenceName)
            && field.Type is not ("history" or "guid"));

    private sealed record History(IReadOnlyList<AdoUpdate> Updates, IReadOnlyList<AdoComment> Comments);

    internal sealed record Node(int Id, string Name, string Path, DateOnly? Start, DateOnly? End);

    internal sealed record ProcessInfo(string Id, string Name, IReadOnlyList<AdoWorkItemType> Types, Dictionary<string, int> Levels)
    {
        public string TypeId(string type) => $"{Id}/{type}";

        public string StatusId(string type, string state) => $"{Id}/{type}/{state}";
    }

    internal sealed class ProjectInfo
    {
        public ProjectInfo(AdoProject project, ProcessInfo process, List<Node> iterations, List<Node> areas, DateOnly today)
        {
            Project = project;
            Process = process;
            Iterations = iterations;
            Areas = areas;
            SprintsByPath = iterations
                .Where(n => n.Start is not null && n.End is not null)
                .ToDictionary(n => n.Path, n => new SourceSprint
                {
                    Id = n.Id.ToString(CultureInfo.InvariantCulture),
                    Name = n.Name,
                    Project = project.Id,
                    Start = n.Start,
                    End = n.End,
                    Closed = n.End < today,
                }, StringComparer.OrdinalIgnoreCase);
        }

        public AdoProject Project { get; }
        public string Id => Project.Id;
        public ProcessInfo Process { get; }
        public List<Node> Iterations { get; }
        public List<Node> Areas { get; }

        /// <summary>Dated iterations are sprints; undated ones stay an iteration path (a label option).</summary>
        public Dictionary<string, SourceSprint> SprintsByPath { get; }

        public IEnumerable<SourceSprint> Sprints => SprintsByPath.Values;
    }

    /// <summary>What Describe found, kept for the read: projects, state categories, the fields that are exported.</summary>
    internal sealed class Catalog
    {
        public Catalog(Uri collection, List<ProjectInfo> projects, List<AdoField> fields)
        {
            Projects = projects;
            Fields = fields.ToDictionary(f => f.ReferenceName, StringComparer.OrdinalIgnoreCase);
            var types = new List<SourceItemType>();
            var statuses = new List<SourceStatus>();
            foreach (var group in projects.GroupBy(p => p.Process.Id, StringComparer.OrdinalIgnoreCase))
            {
                var process = group.First().Process;
                // A type is per process; name the project only when one project uses the process.
                var only = group.Count() == 1 ? group.First().Id : null;
                foreach (var type in process.Types)
                {
                    var typeId = process.TypeId(type.Name);
                    types.Add(new SourceItemType
                    {
                        Id = typeId,
                        Name = type.Name,
                        Project = only,
                        Level = process.Levels.TryGetValue(type.Name, out var level) ? level : null,
                    });
                    foreach (var state in type.States)
                    {
                        var id = process.StatusId(type.Name, state.Name);
                        Categories[id] = state.Category;
                        statuses.Add(new SourceStatus { Id = id, Name = state.Name, Category = state.Category, ItemType = typeId, Project = only });
                    }
                }
            }

            Description = new SourceDescription
            {
                Source = new SnapshotSource { Kind = SourceKinds.AzureDevOps, BaseUrl = collection.AbsoluteUri.TrimEnd('/') },
                Projects = [.. projects.Select(p => new SourceProject
                {
                    Id = p.Id,
                    Name = p.Project.Name,
                    Url = $"{collection.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(p.Project.Name)}",
                    Process = p.Process.Name,
                })],
                ItemTypes = types,
                Statuses = statuses,
                Fields = [.. fields.Select(f => new SourceField
                {
                    Id = f.ReferenceName,
                    Name = f.Name,
                    Kind = Ado.Kind(f),
                    Options = f.ReferenceName switch
                    {
                        AreaPath => [.. projects.SelectMany(p => p.Areas).Select(n => n.Path)],
                        IterationPath => [.. projects.SelectMany(p => p.Iterations).Select(n => n.Path)],
                        _ => [],
                    },
                })],
            };
        }

        public List<ProjectInfo> Projects { get; }
        public Dictionary<string, AdoField> Fields { get; }
        public Dictionary<string, string?> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SourceDescription Description { get; }
    }
}
