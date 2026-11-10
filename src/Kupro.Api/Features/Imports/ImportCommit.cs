using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kupro.Api.Features.Imports;

/// <summary>A run's progress, saved with every batch so a resumed run keeps counting. Stored on <see cref="ImportRun.Counts"/>.</summary>
public sealed class ImportRunCounts
{
    public int Issues { get; set; }
    public int Projects { get; set; }
    public int Members { get; set; }
    public int Labels { get; set; }
    public int Sprints { get; set; }
    public int Milestones { get; set; }
    public int Relations { get; set; }
    public int Aliases { get; set; }

    /// <summary>A reference another issue already answers to. The first one keeps it; the plan and report say how many.</summary>
    public int AliasesSkipped { get; set; }

    /// <summary>Descriptions cut to fit; the block still links to the original.</summary>
    public int Truncated { get; set; }

    /// <summary>Items an earlier run already imported.</summary>
    public int AlreadyImported { get; set; }

    public void Add(ImportRunCounts other)
    {
        Issues += other.Issues;
        Projects += other.Projects;
        Members += other.Members;
        Labels += other.Labels;
        Sprints += other.Sprints;
        Milestones += other.Milestones;
        Relations += other.Relations;
        Aliases += other.Aliases;
        AliasesSkipped += other.AliasesSkipped;
        Truncated += other.Truncated;
        AlreadyImported += other.AlreadyImported;
    }
}

/// <summary>The import can't go on, and the message tells the owner why: a mapping that no longer fits the workspace.</summary>
public sealed class ImportFailedException : Exception
{
    public ImportFailedException() { }

    public ImportFailedException(string message) : base(message) { }

    public ImportFailedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Writes a stored snapshot into the workspace (ADR-019 Pipeline, commit). Every batch is one transaction holding the
/// records and the <see cref="ImportedItem"/> rows that name them, so after a restart a run skips what committed and
/// redoes nothing else. Items go in three passes over the snapshot, roots first, so a parent always exists before its
/// children, and only one small record per item stays in memory. One instance per run.
/// </summary>
public sealed partial class ImportCommit(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    IssueService issues,
    LabelService labels,
    ProjectService projects,
    SprintService sprints,
    PlanService plan,
    WorkspaceService workspaces,
    ActivityLog activity,
    RealtimeEvents realtime,
    TimeProvider clock)
{
    internal const int BatchSize = 500;

    private const int MaxProjectName = 100;
    private const int MaxSprintName = 100;
    private const int MaxAlias = 512;
    private const int MaxEffort = 1_000_000;
    private const int MaxDepth = 3;
    private const int MaxAttempts = 3;

    /// <summary>Stands for the people a run authors under the shared placeholder. A source ID can't clash: it isn't one a person has.</summary>
    private const string SkippedPeople = "*skipped";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Guid> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid?> _people = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _sprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _labels = new(StringComparer.Ordinal);

    private ImportRunCounts _counts = new();
    private Guid _importId;
    private Guid _runId;
    private Guid _ownerId;
    private string _sourceKind = "";
    private SnapshotHeader _header = null!;
    private ImportMapping _mapping = null!;
    private MappedSource _source = null!;
    private ImportDescription _description = null!;
    private List<MemberCandidate>? _candidates;

    public async Task<ImportRunCounts> RunAsync(WorkspaceImport import, ImportRun run, SnapshotReader snapshot, ImportMapping mapping, CancellationToken ct)
    {
        _importId = import.Id;
        _runId = run.Id;
        _ownerId = import.CreatedById;
        _sourceKind = import.SourceKind;
        _header = snapshot.Header;
        _mapping = mapping;
        _source = new MappedSource(_header, mapping, clock.GetUtcNow());
        _description = new ImportDescription(_header, mapping);
        _counts = run.Counts is { } saved ? JsonSerializer.Deserialize<ImportRunCounts>(saved, Json) ?? new() : new();

        await LoadKnownAsync(ct);

        var read = await ReadAsync(snapshot, ct);
        var fit = HierarchyFit.Run(read.Kept, mapping.Fold);
        var tops = await TopTitlesAsync(snapshot, fit, ct);
        var checklists = Checklists(fit, read.ChecklistLines);

        await EnsureProjectsAsync(read.Targets, ct);
        var numbers = await ReserveNumbersAsync(read.Refs, fit, ct);
        await EnsureSprintsAndMilestonesAsync(read, ct);

        var depths = Depths(fit);
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            await WriteLevelAsync(snapshot, fit, depths, depth, numbers, tops, checklists, ct);
        }

        await WriteRelationsAsync(snapshot, fit, ct);

        _counts.AlreadyImported = read.Refs.Count(r => _items.ContainsKey(r.Id)) - _counts.Issues;
        return _counts;
    }

    // ---------------------------------------------------------------- what is there already

    private async Task LoadKnownAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId)
            .Where(i => i.SourceKind == ImportedKinds.Item || i.SourceKind == ImportedKinds.Person || i.SourceKind == ImportedKinds.Sprint || i.SourceKind == ImportedKinds.Label)
            .Select(i => new { i.SourceKind, i.SourceId, i.EntityId })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            switch (row.SourceKind)
            {
                case ImportedKinds.Item:
                    _items[row.SourceId] = row.EntityId;
                    break;
                case ImportedKinds.Person:
                    _people[row.SourceId] = row.EntityId;
                    break;
                case ImportedKinds.Sprint:
                    _sprints[row.SourceId] = row.EntityId;
                    break;
                case ImportedKinds.Label:
                    _labels[row.SourceId] = row.EntityId;
                    break;
            }
        }
    }

    /// <summary>The first pass: what's in scope, as small records. Repeated IDs count once, as in the plan.</summary>
    private async Task<SourceRead> ReadAsync(SnapshotReader snapshot, CancellationToken ct)
    {
        var read = new SourceRead();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (!seen.Add(item.Id) || !_source.InScope(item))
            {
                continue;
            }

            var input = _source.Input(item);
            var target = _source.Project(item.Project);
            read.Kept.Add(input);
            read.Refs.Add(new ItemRef(item.Id, target.Key, item.Key));
            read.Targets.Add(target);
            if (input.Checklist)
            {
                read.ChecklistLines[item.Id] = new ChecklistLine(item.Title, _source.State(item) is IssueState.Done or IssueState.Canceled);
            }

            if (item.Sprint is { } sprint)
            {
                read.Sprints.Add((sprint, target.Key));
            }
        }

        foreach (var sprint in _header.Sprints.Where(s => s.Project is not null))
        {
            // A sprint with no items yet still belongs to a project the import brings over.
            read.Sprints.Add((sprint.Id, _source.Project(sprint.Project!).Key));
        }

        return read;
    }

    /// <summary>A second pass for the names of folded top-level items only: their children are labelled with them.</summary>
    private static async Task<Dictionary<string, string>> TopTitlesAsync(SnapshotReader snapshot, FitResult fit, CancellationToken ct)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fit.FoldedTops.Count == 0)
        {
            return titles;
        }

        var tops = fit.FoldedTops.Select(t => t.Top).ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(tops.Count, StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (tops.Contains(item.Id))
            {
                names[item.Id] = item.Title;
            }
        }

        foreach (var folded in fit.FoldedTops)
        {
            titles[folded.Item] = names[folded.Top];
        }

        return titles;
    }

    private static ILookup<string, (string Id, ChecklistLine Line)> Checklists(FitResult fit, Dictionary<string, ChecklistLine> lines) =>
        fit.Checklists.ToLookup(c => c.Parent, c => (c.Item, lines[c.Item]), StringComparer.Ordinal);

    private static Dictionary<string, int> Depths(FitResult fit)
    {
        var depths = new Dictionary<string, int>(fit.Items.Count, StringComparer.Ordinal);
        foreach (var item in fit.Items.Values)
        {
            var (depth, at) = (0, item);
            while (at.Parent is { } parent)
            {
                depth++;
                at = fit.Items[parent];
            }

            depths[item.Id] = depth;
        }

        return depths;
    }

    // ---------------------------------------------------------------- what the items need first

    private async Task EnsureProjectsAsync(HashSet<ProjectTarget> targets, CancellationToken ct)
    {
        foreach (var key in targets.Select(t => t.Key).Distinct().Where(k => !ProjectKey().IsMatch(k)))
        {
            throw new ImportFailedException($"{key} isn't a valid project key: 2 to 10 letters or digits, starting with a letter.");
        }

        var created = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            created.Clear();
            delta = new ImportRunCounts();
            var keys = targets.Select(t => t.Key).Distinct().ToList();
            var existing = await db.Projects.Where(p => keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal, ct);
            var ours = await db.ImportedItems.Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Project).Select(i => i.EntityId).ToListAsync(ct);
            var count = await db.Projects.CountAsync(ct);
            foreach (var group in targets.GroupBy(t => t.Key))
            {
                var wantsExisting = group.Any(t => t.Existing);
                if (existing.TryGetValue(group.Key, out var id))
                {
                    if (!wantsExisting && !ours.Contains(id))
                    {
                        throw new ImportFailedException($"The project key {group.Key} is already used in this workspace. Choose another key, or import into that project.");
                    }

                    created[group.Key] = id;
                    continue;
                }

                if (wantsExisting)
                {
                    throw new ImportFailedException($"There is no project {group.Key} to import into.");
                }

                var project = projects.AddImported(db, group.Key, Cut(group.First().Name.Trim(), MaxProjectName), count++);
                Record(db, ImportedKinds.Project, group.Key, ImportedEntityKind.Project, project.Id);
                created[group.Key] = project.Id;
                delta.Projects++;
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        foreach (var (key, id) in created)
        {
            _projects[key] = id;
        }

        _counts.Add(delta);
        if (delta.Projects > 0)
        {
            // The plan lists projects, and project writes have no event of their own.
            await realtime.ChangedAsync(ChangeTopic.Plan);
        }
    }

    /// <summary>
    /// Numbers for every item still to write: its source number when it has one that is free, else the next free one
    /// (ADR-019 Keys). Reserved before any issue is written, so a team creating issues meanwhile can't take a kept key.
    /// </summary>
    private async Task<Dictionary<string, int>> ReserveNumbersAsync(List<ItemRef> refs, FitResult fit, CancellationToken ct)
    {
        var numbers = new Dictionary<string, int>(refs.Count, StringComparer.Ordinal);
        foreach (var group in refs.Where(r => fit.Items.ContainsKey(r.Id) && !_items.ContainsKey(r.Id)).GroupBy(r => r.ProjectKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var projectId = _projects[group.Key];
            var wanted = group.Select(r => (r.Id, Number: SourceNumber(r.Key))).ToList();
            var assigned = new Dictionary<string, int>(wanted.Count, StringComparer.Ordinal);
            await WriteAsync(async db =>
            {
                assigned.Clear();
                await ProjectService.ReserveImportedNumbersAsync(db, projectId, wanted.Max(w => w.Number ?? 0), ct);
                var taken = (await db.Issues.Where(i => i.ProjectId == projectId).Select(i => i.Number).ToListAsync(ct)).ToHashSet();
                var pending = new List<string>();
                foreach (var (id, number) in wanted)
                {
                    if (number is { } kept && taken.Add(kept))
                    {
                        assigned[id] = kept;
                    }
                    else
                    {
                        pending.Add(id);
                    }
                }

                if (pending.Count > 0)
                {
                    var last = await ProjectService.AllocateIssueNumbersAsync(db, projectId, pending.Count, ct);
                    var next = last - pending.Count;
                    foreach (var id in pending)
                    {
                        assigned[id] = ++next;
                    }
                }
            }, ct);

            foreach (var (id, number) in assigned)
            {
                numbers[id] = number;
            }
        }

        return numbers;
    }

    /// <summary><c>PROJ-123</c>, <c>4567</c> and <c>owner/repo#89</c> all keep their number; anything else gets a new one.</summary>
    internal static int? SourceNumber(string? key) =>
        key is not null && NumberInKey().Match(key.Trim()) is { Success: true } match
            && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;

    /// <summary>
    /// Sprints are per project: a source sprint spanning projects becomes one per project with the same name and dates.
    /// They arrive closed or planned, never active: starting one is the team's move, and it takes the ledger's baseline.
    /// </summary>
    private async Task EnsureSprintsAndMilestonesAsync(SourceRead read, CancellationToken ct)
    {
        var bySource = _header.Sprints.DistinctBy(s => s.Id).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var added = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            added.Clear();
            delta = new ImportRunCounts();
            foreach (var (sprintId, projectKey) in read.Sprints)
            {
                var key = SprintKey(sprintId, projectKey);
                if (_sprints.ContainsKey(key) || !bySource.TryGetValue(sprintId, out var source) || !_projects.TryGetValue(projectKey, out var projectId))
                {
                    continue;
                }

                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                var start = source.Start ?? source.End ?? today;
                var end = source.End ?? source.Start ?? today;
                var sprint = sprints.AddImported(db, projectId, Cut(source.Name.Trim(), MaxSprintName), start < end ? start : end, start < end ? end : start, source.Closed);
                Record(db, ImportedKinds.Sprint, key, ImportedEntityKind.Sprint, sprint.Id);
                added[key] = sprint.Id;
                delta.Sprints++;
            }

            var doneVersions = (await db.ImportedItems.Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Version).Select(i => i.SourceId).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            var versions = _header.Versions.DistinctBy(v => v.Id).ToDictionary(v => v.Id, StringComparer.Ordinal);
            foreach (var id in _mapping.MilestoneVersions.Where(v => !doneVersions.Contains(v)))
            {
                // A milestone is a date on the plan. A version without one stays in the details block.
                if (versions.TryGetValue(id, out var version) && version.Date is { } date && _projects.TryGetValue(_source.Project(version.Project).Key, out var projectId))
                {
                    var milestone = plan.AddImportedMilestone(db, projectId, Cut(version.Name.Trim(), MaxSprintName), date);
                    Record(db, ImportedKinds.Version, id, ImportedEntityKind.Milestone, milestone.Id);
                    delta.Milestones++;
                }
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        foreach (var (key, id) in added)
        {
            _sprints[key] = id;
        }

        _counts.Add(delta);
        if (delta.Sprints > 0)
        {
            await realtime.ChangedAsync(ChangeTopic.Sprints);
        }

        if (delta.Milestones > 0)
        {
            await realtime.ChangedAsync(ChangeTopic.Plan);
        }
    }

    // ---------------------------------------------------------------- issues

    private async Task WriteLevelAsync(
        SnapshotReader snapshot, FitResult fit, Dictionary<string, int> depths, int depth, Dictionary<string, int> numbers,
        Dictionary<string, string> tops, ILookup<string, (string Id, ChecklistLine Line)> checklists, CancellationToken ct)
    {
        var batch = new List<SourceItem>(BatchSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (!seen.Add(item.Id) || !depths.TryGetValue(item.Id, out var at) || at != depth || _items.ContainsKey(item.Id))
            {
                continue;
            }

            batch.Add(item);
            if (batch.Count == BatchSize)
            {
                await WriteBatchAsync(batch, fit, numbers, tops, checklists, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch, fit, numbers, tops, checklists, ct);
        }
    }

    private async Task WriteBatchAsync(
        List<SourceItem> batch, FitResult fit, Dictionary<string, int> numbers, Dictionary<string, string> tops,
        ILookup<string, (string Id, ChecklistLine Line)> checklists, CancellationToken ct)
    {
        var newPeople = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var newLabels = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var newItems = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            newPeople.Clear();
            newLabels.Clear();
            newItems.Clear();
            delta = new ImportRunCounts();

            // A retry after a lost commit acknowledgement finds its own rows here and writes nothing twice.
            var ids = batch.Select(i => i.Id).ToList();
            var written = await db.ImportedItems.Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item && ids.Contains(i.SourceId))
                .Select(i => new { i.SourceId, i.EntityId }).ToListAsync(ct);
            foreach (var row in written)
            {
                _items[row.SourceId] = row.EntityId;
            }

            var todo = batch.Where(i => !_items.ContainsKey(i.Id)).ToList();
            if (todo.Count == 0)
            {
                return;
            }

            await ResolvePeopleAsync(db, todo.SelectMany(i => new[] { i.Assignee, i.Creator }).OfType<string>().Distinct(), newPeople, delta, ct);
            var creators = todo.ToDictionary(i => i.Id, i => Actor(i.Creator, newPeople));
            if (creators.Values.Any(c => c is null))
            {
                EnsureSkippedPlaceholder(db, newPeople, delta);
            }

            var drafts = todo.Select(item =>
            {
                var tooLong = new List<string>();
                var itemLabels = _description.Labels(item, _source.Type(item), tops.GetValueOrDefault(item.Id), tooLong);
                return (Item: item, Labels: itemLabels, TooLong: tooLong);
            }).ToList();
            await ResolveLabelsAsync(db, drafts.SelectMany(d => d.Labels).Distinct(StringComparer.Ordinal).ToList(), newLabels, delta, ct);

            var aliases = await AliasCandidatesAsync(db, todo, ct);
            var rows = new List<ImportedIssue>(todo.Count);
            var now = clock.GetUtcNow();
            foreach (var (item, itemLabels, tooLong) in drafts)
            {
                var target = _source.Project(item.Project);
                var fitted = fit.Items[item.Id];
                var id = Guid.CreateVersion7();
                var key = $"{target.Key}-{numbers[item.Id]}";
                var state = _source.State(item);
                var assignee = item.Assignee is { } a ? Member(a, newPeople) : null;
                var creator = creators[item.Id] ?? SkippedId(newPeople);
                var effort = _source.Effort(item);
                var effortFits = effort is >= 0 and < MaxEffort;
                var start = item.Start > item.Due ? null : item.Start;
                var details = _description.Details(item, tooLong, item.Assignee is not null && assignee is null);
                if (effort is not null && !effortFits)
                {
                    details.Add(("Effort", effort.Value.ToString(CultureInfo.InvariantCulture)));
                }

                if (item.Start is not null && start is null)
                {
                    details.Add(("Start date", item.Start.Value.ToString("O", CultureInfo.InvariantCulture)));
                }

                var (description, truncated) = _description.Build(item, [.. checklists[item.Id].Select(c => c.Line)], details);
                delta.Truncated += truncated ? 1 : 0;
                var title = string.IsNullOrWhiteSpace(item.Title) ? "(no title)" : Cut(item.Title.Trim(), ImportDescription.MaxTitle);
                var labelIds = itemLabels.Select(l => _labels.TryGetValue(l, out var known) ? known : newLabels[l]).ToList();
                var sprintId = item.Sprint is { } sprint && _sprints.TryGetValue(SprintKey(sprint, target.Key), out var found) ? found : (Guid?)null;
                var parentId = fitted.Parent is { } parent ? _items[parent] : (Guid?)null;
                var effortValue = effortFits ? Math.Round(effort!.Value, 2) : (decimal?)null;
                rows.Add(new ImportedIssue(
                    id, _projects[target.Key], numbers[item.Id], key, fitted.Type, title, description, state, _source.Priority(item), assignee, parentId,
                    effortValue, start, item.Due, sprintId, labelIds, creator, item.Created.ToUniversalTime(), item.Updated.ToUniversalTime(),
                    state is IssueState.Done or IssueState.Canceled ? (item.Resolved ?? item.Updated).ToUniversalTime() : null,
                    item.Creator is { } name && creators[item.Id] is null ? PersonName(name) : null));

                var values = new ImportedValues(title, description, state, _source.Priority(item), assignee, parentId, sprintId, effortValue, start, item.Due, itemLabels);
                var imported = Record(db, ImportedKinds.Item, item.Id, ImportedEntityKind.Issue, id);
                imported.LastValues = JsonSerializer.Serialize(values, Json);
                foreach (var (checklistItem, _) in checklists[item.Id])
                {
                    Record(db, ImportedKinds.Checklist, checklistItem, ImportedEntityKind.Issue, id);
                }

                AddAliases(db, item, id, key, aliases, delta);
                newItems[item.Id] = id;
                delta.Issues++;
            }

            await issues.AddImportedAsync(db, rows, ct);
            foreach (var row in rows)
            {
                activity.RecordImported(db, row.Id, "imported", _ownerId, now, new { source = _sourceKind });
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        foreach (var (id, member) in newPeople)
        {
            _people[id] = member;
        }

        foreach (var (name, id) in newLabels)
        {
            _labels[name] = id;
        }

        foreach (var (id, issue) in newItems)
        {
            _items[id] = issue;
        }

        _counts.Add(delta);
        if (delta.Issues > 0)
        {
            // One event per batch, not one per issue: a large import must not flood every open client (ADR-019 IMP-002).
            await realtime.ChangedAsync(ChangeTopic.Issue);
        }
    }

    // ---------------------------------------------------------------- people and labels

    /// <summary>Only people a record refers to get a row: a source's long-gone users don't all become placeholders.</summary>
    private async Task ResolvePeopleAsync(
        KuproDbContext db, IEnumerable<string> sourceIds, Dictionary<string, Guid?> added, ImportRunCounts delta, CancellationToken ct)
    {
        var people = _header.People.DistinctBy(p => p.Id).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var unresolved = sourceIds.Where(id => !_people.ContainsKey(id)).ToList();
        if (unresolved.Count == 0)
        {
            return;
        }

        var targets = new Dictionary<string, PersonTarget>(StringComparer.Ordinal);
        foreach (var id in unresolved)
        {
            if (_mapping.People.TryGetValue(id, out var mapped))
            {
                targets[id] = mapped;
            }
            else if (people.TryGetValue(id, out var person))
            {
                // Someone who appeared after the mapping was saved gets the proposed default.
                targets[id] = ImportMappingDefaults.ProposePerson(person, _candidates ??= await CandidatesAsync(db, ct));
            }
            else
            {
                // A reference the snapshot doesn't explain: nobody to name, so like a skipped person.
                targets[id] = new PersonTarget(PersonAction.Skip);
            }
        }

        var memberIds = targets.Values.Where(t => t.Action == PersonAction.Member).Select(t => t.MemberId).OfType<Guid>().Distinct().ToList();
        var members = await db.Members.Where(m => memberIds.Contains(m.Id) && m.Kind == MemberKind.Human && (m.Status == MemberStatus.Active || m.Status == MemberStatus.Placeholder))
            .Select(m => m.Id).ToListAsync(ct);
        foreach (var (id, target) in targets)
        {
            switch (target.Action)
            {
                case PersonAction.Member:
                    added[id] = target.MemberId is { } member && members.Contains(member)
                        ? member
                        : throw new ImportFailedException($"{PersonName(id)} is mapped to a member who is no longer active. Update the mapping.");
                    break;
                case PersonAction.Skip:
                    added[id] = null;
                    break;
                default:
                    // Inviting happens when the run finishes: the same row becomes the invite, so history follows it.
                    var source = people.GetValueOrDefault(id);
                    var placeholder = workspaces.AddPlaceholder(db, Cut(source?.Name ?? id, 200), source?.Email is { Length: <= 320 } email ? email : null);
                    Record(db, ImportedKinds.Person, id, ImportedEntityKind.Member, placeholder.Id);
                    added[id] = placeholder.Id;
                    delta.Members++;
                    break;
            }
        }
    }

    private static async Task<List<MemberCandidate>> CandidatesAsync(KuproDbContext db, CancellationToken ct) =>
        await db.Members.AsNoTracking()
            .Where(m => m.Kind == MemberKind.Human && m.Status == MemberStatus.Active)
            .Select(m => new MemberCandidate(
                m.Id,
                m.DisplayName ?? db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                db.Users.Where(u => u.Id == m.UserId).Select(u => u.Email).FirstOrDefault()))
            .ToListAsync(ct);

    private void EnsureSkippedPlaceholder(KuproDbContext db, Dictionary<string, Guid?> added, ImportRunCounts delta)
    {
        if (_people.ContainsKey(SkippedPeople) || added.ContainsKey(SkippedPeople))
        {
            return;
        }

        var placeholder = workspaces.AddPlaceholder(db, $"Skipped users from {_description.SourceDisplayName}", null);
        Record(db, ImportedKinds.Person, SkippedPeople, ImportedEntityKind.Member, placeholder.Id);
        added[SkippedPeople] = placeholder.Id;
        delta.Members++;
    }

    private Guid SkippedId(Dictionary<string, Guid?> added) =>
        (added.TryGetValue(SkippedPeople, out var id) ? id : _people[SkippedPeople])!.Value;

    private Guid? Member(string sourceId, Dictionary<string, Guid?> added) =>
        added.TryGetValue(sourceId, out var id) ? id : _people.GetValueOrDefault(sourceId);

    /// <summary>
    /// The acting member; null means the shared placeholder, for someone the source names but the owner skipped. A source
    /// that names nobody (a CSV has no author column) has nobody to misattribute, so it is whoever ran the import.
    /// </summary>
    private Guid? Actor(string? sourceId, Dictionary<string, Guid?> added) => sourceId is null ? _ownerId : Member(sourceId, added);

    private string PersonName(string sourceId) =>
        _header.People.FirstOrDefault(p => p.Id == sourceId)?.Name ?? sourceId;

    private async Task ResolveLabelsAsync(KuproDbContext db, List<string> names, Dictionary<string, Guid> added, ImportRunCounts delta, CancellationToken ct)
    {
        var missing = names.Where(n => !_labels.ContainsKey(n)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        await labels.ResolveAsync(db, missing, ct);
        var existing = await db.Labels.Where(l => missing.Contains(l.Name)).Select(l => new { l.Name, l.Id }).ToListAsync(ct);
        foreach (var label in existing)
        {
            added[label.Name] = label.Id;
        }

        foreach (var label in db.ChangeTracker.Entries<Label>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList())
        {
            added[label.Name] = label.Id;
            Record(db, ImportedKinds.Label, label.Name, ImportedEntityKind.Label, label.Id);
            delta.Labels++;
        }
    }

    // ---------------------------------------------------------------- aliases

    /// <summary>The references the batch wants that another issue already answers to.</summary>
    private static async Task<HashSet<string>> AliasCandidatesAsync(KuproDbContext db, List<SourceItem> items, CancellationToken ct)
    {
        var wanted = items.SelectMany(References).Distinct().ToList();
        return (await db.IssueAliases.Where(a => wanted.Contains(a.Alias)).Select(a => a.Alias).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
    }

    private void AddAliases(KuproDbContext db, SourceItem item, Guid issueId, string key, HashSet<string> taken, ImportRunCounts delta)
    {
        var now = clock.GetUtcNow();
        foreach (var alias in References(item))
        {
            if (string.Equals(alias, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Two ADO organisations can both have a #4567. A longer reference can't be a key anyone types.
            if (alias.Length > MaxAlias || !taken.Add(alias))
            {
                delta.AliasesSkipped++;
                continue;
            }

            db.IssueAliases.Add(new IssueAlias
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.RequireWorkspaceId(),
                IssueId = issueId,
                ImportId = _importId,
                Alias = alias,
                CreatedAt = now,
            });
            delta.Aliases++;
        }
    }

    /// <summary>What people call the item in the source: its key (a key that is only a number is written <c>#4567</c>) and its URL.</summary>
    private static IEnumerable<string> References(SourceItem item)
    {
        var key = item.Key?.Trim();
        return new[] { key is { Length: > 0 } && key.All(char.IsAsciiDigit) ? $"#{key}" : key, item.Url?.Trim() }
            .OfType<string>().Where(r => r.Length > 0).Distinct(StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- relations

    /// <summary>Source relations and the old parents the fit couldn't keep, once all issues exist. Safe to repeat: existing ones are skipped.</summary>
    private async Task WriteRelationsAsync(SnapshotReader snapshot, FitResult fit, CancellationToken ct)
    {
        var batch = new List<(Guid From, Guid To, RelationType Type)>(BatchSize);
        var seen = new HashSet<(Guid, Guid, RelationType)>();
        async Task AddAsync(string from, string to, RelationType type)
        {
            if (!_items.TryGetValue(from, out var source) || !_items.TryGetValue(to, out var target) || source == target)
            {
                return;
            }

            // Relates has no direction, so one row covers both ways round.
            var (a, b) = type == RelationType.Relates && target.CompareTo(source) < 0 ? (target, source) : (source, target);
            if (seen.Add((a, b, type)))
            {
                batch.Add((source, target, type));
            }

            if (batch.Count == BatchSize)
            {
                await WriteRelationBatchAsync(batch, ct);
                batch.Clear();
            }
        }

        foreach (var old in fit.OldParents)
        {
            await AddAsync(old.Item, old.Parent, RelationType.Relates);
        }

        await foreach (var relation in snapshot.ReadRelationsAsync(ct))
        {
            await AddAsync(relation.From, relation.To, relation.Kind switch
            {
                RelationKinds.Blocks => RelationType.Blocks,
                RelationKinds.Duplicates => RelationType.Duplicates,
                _ => RelationType.Relates,
            });
        }

        if (batch.Count > 0)
        {
            await WriteRelationBatchAsync(batch, ct);
        }
    }

    private async Task WriteRelationBatchAsync(List<(Guid From, Guid To, RelationType Type)> batch, CancellationToken ct)
    {
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            delta = new ImportRunCounts();
            var ids = batch.SelectMany(r => new[] { r.From, r.To }).Distinct().ToList();
            var existing = (await db.IssueRelations.Where(r => ids.Contains(r.SourceId) && ids.Contains(r.TargetId))
                    .Select(r => new { r.SourceId, r.TargetId, r.Type }).ToListAsync(ct))
                .Select(r => (r.SourceId, r.TargetId, r.Type)).ToHashSet();
            var now = clock.GetUtcNow();
            foreach (var (from, to, type) in batch)
            {
                if (existing.Contains((from, to, type)) || (type == RelationType.Relates && existing.Contains((to, from, type))))
                {
                    continue;
                }

                db.IssueRelations.Add(new IssueRelation
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = workspace.RequireWorkspaceId(),
                    SourceId = from,
                    TargetId = to,
                    Type = type,
                    CreatedAt = now,
                });
                delta.Relations++;
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        _counts.Add(delta);
        if (delta.Relations > 0)
        {
            await realtime.ChangedAsync(ChangeTopic.Issue);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// One transaction, retried on a lost commit or a unique clash with work outside the import (the team creating the same
    /// label). <paramref name="write"/> starts over each time, so it holds what it did in locals until this returns.
    /// </summary>
    private async Task WriteAsync(Func<KuproDbContext, Task> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            try
            {
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await write(db);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                });
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The next attempt reads what the clash was about.
            }
        }
    }

    private ImportedItem Record(KuproDbContext db, string kind, string sourceId, ImportedEntityKind entityKind, Guid entityId)
    {
        var now = clock.GetUtcNow();
        var row = new ImportedItem
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            ImportId = _importId,
            SourceKind = kind,
            SourceId = sourceId,
            EntityKind = entityKind,
            EntityId = entityId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ImportedItems.Add(row);
        return row;
    }

    private async Task SaveCountsAsync(KuproDbContext db, ImportRunCounts delta, CancellationToken ct)
    {
        var total = new ImportRunCounts();
        total.Add(_counts);
        total.Add(delta);
        var json = JsonSerializer.Serialize(total, Json);
        await db.ImportRuns.Where(r => r.Id == _runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Counts, json), ct);
    }

    private static string SprintKey(string sprintId, string projectKey) => $"{sprintId}/{projectKey}";

    private static string Cut(string text, int length) => text.Length > length ? text[..length].TrimEnd() : text;

    [GeneratedRegex(ProjectService.KeyPattern)]
    private static partial Regex ProjectKey();

    [GeneratedRegex(@"^(?:.*[-#])?(\d{1,9})$")]
    private static partial Regex NumberInKey();

    private sealed class SourceRead
    {
        public List<FitInput> Kept { get; } = [];
        public List<ItemRef> Refs { get; } = [];
        public HashSet<ProjectTarget> Targets { get; } = [];
        public HashSet<(string SprintId, string ProjectKey)> Sprints { get; } = [];
        public Dictionary<string, ChecklistLine> ChecklistLines { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ItemRef(string Id, string ProjectKey, string? Key);

    /// <summary>The Kupro values an item last imported as. A re-run updates a field only while Kupro still holds this value.</summary>
    private sealed record ImportedValues(
        string Title,
        string? Description,
        IssueState State,
        IssuePriority Priority,
        Guid? Assignee,
        Guid? Parent,
        Guid? Sprint,
        decimal? Effort,
        DateOnly? Start,
        DateOnly? Due,
        IReadOnlyList<string> Labels);
}

/// <summary>Values of <see cref="ImportedItem.SourceKind"/>.</summary>
internal static class ImportedKinds
{
    public const string Item = "item";
    public const string Checklist = "checklist";
    public const string Project = "project";
    public const string Person = "person";
    public const string Sprint = "sprint";
    public const string Version = "version";
    public const string Label = "label";
}
