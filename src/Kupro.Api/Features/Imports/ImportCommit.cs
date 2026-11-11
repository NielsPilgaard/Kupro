using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
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

    public int Comments { get; set; }
    public int Attachments { get; set; }

    /// <summary>Attachments with no bytes in the snapshot and no connector to fetch them from; their source link stays in the text.</summary>
    public int AttachmentsMissing { get; set; }

    /// <summary>History entries for fields Kupro has (<c>updated</c>).</summary>
    public int Changes { get; set; }

    /// <summary>History entries for fields Kupro lacks (<c>source_changed</c>).</summary>
    public int SourceChanges { get; set; }

    /// <summary>How far into the snapshot's changes the run got: a resumed run starts after them.</summary>
    public int ChangesRead { get; set; }

    /// <summary>Issues an earlier run imported that this one changed.</summary>
    public int Updated { get; set; }

    /// <summary>Items that couldn't keep their source number because something else has it; the source key is an alias.</summary>
    public int Renumbered { get; set; }

    /// <summary>Placeholders the mapping said to invite that got their invite this run.</summary>
    public int Invited { get; set; }

    /// <summary>People to invite without an email to invite, or already in the workspace: they stay placeholders.</summary>
    public int NotInvited { get; set; }

    /// <summary>Conflicts open when the run ended, carried over or new. Absolute, not added.</summary>
    public int Conflicts { get; set; }

    /// <summary>Imported items a whole snapshot no longer has. They are left alone. Absolute, not added.</summary>
    public int DeletedInSource { get; set; }

    public void Add(ImportRunCounts other)
    {
        Updated += other.Updated;
        Renumbered += other.Renumbered;
        Invited += other.Invited;
        NotInvited += other.NotInvited;
        Comments += other.Comments;
        Attachments += other.Attachments;
        Changes += other.Changes;
        SourceChanges += other.SourceChanges;
        ChangesRead += other.ChangesRead;
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
    CommentService comments,
    AttachmentService attachments,
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

    /// <summary>A folded checklist item's source ID to the item that holds it: its comments and files go there.</summary>
    private readonly Dictionary<string, string> _holders = new(StringComparer.Ordinal);

    /// <summary>What earlier runs imported, as this run started. Everything else this run writes is new.</summary>
    private readonly Dictionary<string, KnownItem> _known = new(StringComparer.Ordinal);

    /// <summary>Checklist items earlier runs folded into a description.</summary>
    private readonly Dictionary<string, KnownChecklist> _folded = new(StringComparer.Ordinal);

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

    /// <summary>What the finished run has to tell the owner, with examples. Set by <see cref="RunAsync"/>.</summary>
    public ImportRunReport? Report { get; private set; }

    public async Task<ImportRunCounts> RunAsync(WorkspaceImport import, ImportRun run, SnapshotReader snapshot, ImportMapping mapping, CancellationToken ct)
    {
        Start(import, snapshot, mapping);
        _runId = run.Id;
        _counts = run.Counts is { } saved ? JsonSerializer.Deserialize<ImportRunCounts>(saved, Json) ?? new() : new();

        await LoadKnownAsync(ct);

        var read = await ReadAsync(snapshot, ct);
        var fit = HierarchyFit.Run(read.Kept, mapping.Fold);
        var tops = await TopTitlesAsync(snapshot, fit, ct);
        var checklists = Checklists(fit, read.ChecklistLines);

        foreach (var checklist in fit.Checklists)
        {
            _holders[checklist.Item] = checklist.Parent;
        }

        await EnsureProjectsAsync(read.Targets, ct);
        var numbers = await ReserveNumbersAsync(read.Refs, fit, ct);
        await EnsureSprintsAndMilestonesAsync(read, ct);
        var references = await ReferencesAsync(snapshot, read, fit, numbers, ct);

        var depths = Depths(fit);
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            await WriteLevelAsync(snapshot, fit, depths, depth, numbers, tops, checklists, references, ct);
        }

        // After the new ones, so an item can move under a parent this run created.
        await UpdateExistingAsync(snapshot, fit, checklists, references, ct);
        await WriteRelationsAsync(snapshot, fit, ct);

        // History last: it hangs off issues, and every issue it can point at exists by now (ADR-019 History).
        var projectOf = read.Refs.ToDictionary(r => r.Id, r => r.ProjectKey, StringComparer.Ordinal);
        await WriteAttachmentsAsync(snapshot, ct);
        await WriteCommentsAsync(snapshot, references, ct);
        var marks = await ReplayChangesAsync(snapshot, projectOf, ct);
        await MarkChangesSeenAsync(marks, ct);

        await InviteAsync(ct);

        _counts.AlreadyImported = read.Refs.Count(r => _items.ContainsKey(r.Id)) - _counts.Issues;
        Report = await ReportAsync(read, ct);
        return _counts;
    }

    private void Start(WorkspaceImport import, SnapshotReader snapshot, ImportMapping mapping)
    {
        _importId = import.Id;
        _ownerId = import.CreatedById;
        _sourceKind = import.SourceKind;
        _header = snapshot.Header;
        _mapping = mapping;
        _source = new MappedSource(_header, mapping, clock.GetUtcNow());
        _description = new ImportDescription(_header, mapping);
    }

    // ---------------------------------------------------------------- what is there already

    private async Task LoadKnownAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId)
            .Where(i => i.SourceKind == ImportedKinds.Person || i.SourceKind == ImportedKinds.Sprint || i.SourceKind == ImportedKinds.Label)
            .Select(i => new { i.SourceKind, i.SourceId, i.EntityId })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            switch (row.SourceKind)
            {
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

        // One small record per item: a delta names only what changed, and the fit needs the whole tree.
        var items = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item)
            .Join(db.Issues, i => i.EntityId, x => x.Id, (i, x) => new { i.SourceId, i.EntityId, i.SourceParent, i.Level, x.Type, x.Key, x.Title })
            .ToListAsync(ct);
        foreach (var item in items)
        {
            _items[item.SourceId] = item.EntityId;
            _known[item.SourceId] = new KnownItem(item.Type, item.Key, item.Title, item.SourceParent, item.Level);
        }

        var folded = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Checklist)
            .Select(i => new { i.SourceId, i.SourceParent, i.Level, i.LastValues })
            .ToListAsync(ct);
        foreach (var item in folded)
        {
            _folded[item.SourceId] = new KnownChecklist(JsonSerializer.Deserialize<ChecklistLine>(item.LastValues!, Json)!, item.SourceParent, item.Level);
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

            var input = Shaped(_source.Input(item));
            var target = _source.Project(item.Project);
            read.Kept.Add(input);
            read.KeptIds.Add(item.Id);
            read.Refs.Add(new ItemRef(item.Id, target.Key, item.Key, item.Url));
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

        AddImportedTree(read, seen);
        return read;
    }

    /// <summary>An item already imported keeps the shape it has in Kupro, whatever the mapping and the fold say now (ADR-019 Shape).</summary>
    private FitInput Shaped(FitInput input) =>
        _known.TryGetValue(input.Id, out var known) ? input with { Type = known.Type, Level = input.Level ?? known.Level, Checklist = false, Fixed = true }
        : _folded.TryGetValue(input.Id, out var folded) ? input with { Level = input.Level ?? folded.Level, Checklist = true }
        : input;

    /// <summary>What earlier runs imported and this snapshot doesn't list, so a new child finds its parent and a holder keeps its checklist.</summary>
    private void AddImportedTree(SourceRead read, HashSet<string> seen)
    {
        foreach (var (id, known) in _known.Where(k => !read.KeptIds.Contains(k.Key)))
        {
            read.Kept.Add(new FitInput(id, known.Type, known.SourceParent, known.Level, Fixed: true));
        }

        foreach (var (id, folded) in _folded.Where(k => !read.KeptIds.Contains(k.Key)))
        {
            read.Kept.Add(new FitInput(id, IssueType.Task, folded.SourceParent, folded.Level, Checklist: true));
            read.ChecklistLines[id] = folded.Line;
        }

        if (!_header.Partial)
        {
            read.Deleted.AddRange(_known.Keys.Where(id => !seen.Contains(id)).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>A second pass for the names of folded top-level items only: their children are labelled with them.</summary>
    private async Task<Dictionary<string, string>> TopTitlesAsync(SnapshotReader snapshot, FitResult fit, CancellationToken ct)
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
            titles[folded.Item] = names.TryGetValue(folded.Top, out var name) ? name : _known[folded.Top].Title;
        }

        return titles;
    }

    /// <summary>Lines keep the place they first had: a delta lists them in another order than the first read did.</summary>
    private ILookup<string, (string Id, ChecklistLine Line)> Checklists(FitResult fit, Dictionary<string, ChecklistLine> lines)
    {
        var next = _folded.Count == 0 ? 0 : _folded.Values.Max(f => f.Line.Position) + 1;
        return fit.Checklists
            .Select(c => (c.Parent, c.Item, Line: lines[c.Item] with { Position = _folded.TryGetValue(c.Item, out var known) ? known.Line.Position : next++ }))
            .OrderBy(c => c.Line.Position)
            .ToLookup(c => c.Parent, c => (c.Item, c.Line), StringComparer.Ordinal);
    }

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
        Dictionary<string, string> tops, ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references, CancellationToken ct)
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
                await WriteBatchAsync(batch, fit, numbers, tops, checklists, references, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch, fit, numbers, tops, checklists, references, ct);
        }
    }

    private async Task WriteBatchAsync(
        List<SourceItem> batch, FitResult fit, Dictionary<string, int> numbers, Dictionary<string, string> tops,
        ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references, CancellationToken ct)
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
                var parentId = fitted.Parent is { } parent ? _items[parent] : (Guid?)null;
                var (values, truncated) = ValuesOf(item, itemLabels, tooLong, assignee, parentId, tops.GetValueOrDefault(item.Id), checklists, references);
                delta.Truncated += truncated ? 1 : 0;
                delta.Renumbered += SourceNumber(item.Key) is { } wanted && wanted != numbers[item.Id] ? 1 : 0;
                var labelIds = itemLabels.Select(l => _labels.TryGetValue(l, out var known) ? known : newLabels[l]).ToList();
                rows.Add(new ImportedIssue(
                    id, _projects[target.Key], numbers[item.Id], key, fitted.Type, values.Title, values.Description, state, values.Priority, assignee, parentId,
                    values.Effort, values.Start, values.Due, values.Sprint, labelIds, creator, item.Created.ToUniversalTime(), item.Updated.ToUniversalTime(),
                    state is IssueState.Done or IssueState.Canceled ? (item.Resolved ?? item.Updated).ToUniversalTime() : null,
                    item.Creator is { } name && creators[item.Id] is null ? PersonName(name) : null));

                var imported = Record(db, ImportedKinds.Item, item.Id, ImportedEntityKind.Issue, id);
                imported.LastValues = ImportedField.Serialize(values);
                imported.SourceParent = item.Parent;
                imported.Level = _source.Input(item).Level;
                foreach (var (checklistItem, line) in checklists[item.Id])
                {
                    RecordChecklist(db, checklistItem, line, item.Id, id);
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

    /// <summary>The item as Kupro values: what the first write stores and what a re-run compares against.</summary>
    private (ImportedValues Values, bool Truncated) ValuesOf(
        SourceItem item, IReadOnlyList<string> itemLabels, List<string> tooLong, Guid? assignee, Guid? parent, string? top,
        ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references)
    {
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

        var (description, truncated) = _description.Build(item, [.. checklists[item.Id].Select(c => c.Line)], details, references);
        var title = string.IsNullOrWhiteSpace(item.Title) ? "(no title)" : Cut(item.Title.Trim(), ImportDescription.MaxTitle);
        var sprintId = item.Sprint is { } sprint && _sprints.TryGetValue(SprintKey(sprint, _source.Project(item.Project).Key), out var found) ? found : (Guid?)null;
        var effortValue = effortFits ? Math.Round(effort!.Value, 2) : (decimal?)null;
        return (new ImportedValues(title, description, _source.State(item), _source.Priority(item), assignee, parent, sprintId, effortValue, start, item.Due, itemLabels, top), truncated);
    }

    private void RecordChecklist(KuproDbContext db, string sourceId, ChecklistLine line, string holder, Guid issueId)
    {
        var row = Record(db, ImportedKinds.Checklist, sourceId, ImportedEntityKind.Issue, issueId);
        row.LastValues = JsonSerializer.Serialize(line, Json);
        row.SourceParent = holder;
    }

    // ---------------------------------------------------------------- people and labels

    /// <summary>Only people a record refers to get a row: a source's long-gone users don't all become placeholders.</summary>
    private async Task ResolvePeopleAsync(
        KuproDbContext db, IEnumerable<string> sourceIds, Dictionary<string, Guid?> added, ImportRunCounts delta, CancellationToken ct)
    {
        var people = _header.People.DistinctBy(p => p.Id).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var unresolved = sourceIds.Where(id => !_people.ContainsKey(id) && !added.ContainsKey(id)).ToList();
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

    /// <summary>
    /// Invites the people the mapping says to invite, once their placeholder holds what the run brought: the same row
    /// becomes the invite, so their history follows them (ADR-019 People). Saved per person, so a resumed run doesn't
    /// invite anyone twice.
    /// </summary>
    private async Task InviteAsync(CancellationToken ct)
    {
        var wanted = _mapping.People.Where(p => p.Value.Action == PersonAction.Invite && _people.GetValueOrDefault(p.Key) is not null)
            .Select(p => _people[p.Key]!.Value).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        List<Guid> waiting;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            waiting = await db.Members.AsNoTracking().Where(m => wanted.Contains(m.Id) && m.Status == MemberStatus.Placeholder).Select(m => m.Id).ToListAsync(ct);
        }

        var delta = new ImportRunCounts();
        foreach (var id in waiting)
        {
            var (result, _, _) = await workspaces.InvitePlaceholderAsync(id, new InvitePlaceholderRequest(WorkspaceRole.Member), ct);
            if (result == MemberChangeResult.Ok)
            {
                delta.Invited++;
            }
            else
            {
                delta.NotInvited++;
            }
        }

        await WriteAsync(db => SaveCountsAsync(db, delta, ct), ct);
        _counts.Add(delta);
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

                var relation = new IssueRelation
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = workspace.RequireWorkspaceId(),
                    SourceId = from,
                    TargetId = to,
                    Type = type,
                    CreatedAt = now,
                };
                db.IssueRelations.Add(relation);

                // Undo tells the import's relations from ones the team added between its issues.
                Record(db, ImportedKinds.Relation, $"{from:N}/{to:N}/{type}", ImportedEntityKind.Relation, relation.Id);
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
    /// One transaction, retried on a lost commit or a clash with work outside the import (the team creating the same
    /// label, or editing an issue a re-run is updating). <paramref name="write"/> starts over each time, so it holds what it did in locals until this returns.
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
            catch (DbUpdateException ex) when (attempt < MaxAttempts && ex is DbUpdateConcurrencyException or { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
            {
                // The next attempt reads what the clash was about, or what the team just saved to the same issue.
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
        public HashSet<string> KeptIds { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public List<ItemRef> Refs { get; } = [];
        public HashSet<ProjectTarget> Targets { get; } = [];
        public HashSet<(string SprintId, string ProjectKey)> Sprints { get; } = [];
        public Dictionary<string, ChecklistLine> ChecklistLines { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ItemRef(string Id, string ProjectKey, string? Key, string? Url);

    private sealed record KnownItem(IssueType Type, string Key, string Title, string? SourceParent, int? Level);

    private sealed record KnownChecklist(ChecklistLine Line, string? SourceParent, int? Level);
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
    public const string Comment = "comment";
    public const string Attachment = "attachment";
    public const string Relation = "relation";
}
