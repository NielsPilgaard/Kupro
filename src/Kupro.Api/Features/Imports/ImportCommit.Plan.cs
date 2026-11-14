using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// The dry run against current Kupro data (ADR-019 Pipeline, plan): <see cref="ImportPlanner"/> counts the snapshot, and
/// this adds what depends on the workspace by running a re-run's own comparison on a context that is never saved.
/// </summary>
public sealed partial class ImportCommit
{
    private const int HistoryChunk = 2000;

    public async Task<ImportPlan> PlanAsync(WorkspaceImport import, SnapshotReader snapshot, ImportMapping mapping, CancellationToken ct)
    {
        Start(import, snapshot, mapping);
        await LoadKnownAsync(ct);

        var tree = _known.ToDictionary(k => k.Key, k => new FitInput(k.Key, k.Value.Type, k.Value.SourceParent, k.Value.Level, Fixed: true), StringComparer.Ordinal);
        foreach (var (id, folded) in _folded)
        {
            tree[id] = new FitInput(id, IssueType.Task, folded.SourceParent, folded.Level, Checklist: true);
        }

        var counted = await ImportPlanner.PlanAsync(snapshot, mapping, _known.Keys.ToHashSet(StringComparer.Ordinal), tree, clock.GetUtcNow(), ct);

        var read = await ReadAsync(snapshot, ct);
        var fit = HierarchyFit.Run(read.Kept, mapping.Fold);
        var checklists = Checklists(fit, read.ChecklistLines);
        foreach (var checklist in fit.Checklists)
        {
            _holders[checklist.Item] = checklist.Parent;
        }

        var (taken, missing, keysTaken) = await ProjectsAndKeysAsync(read, fit, ct);
        var sprintsToCreate = StandInSprints(read);
        var milestones = await MilestonesToCreateAsync(ct);
        var references = await ReferencesAsync(snapshot, read, fit, [], ct);

        // Stand-ins for what the commit would create, so a comparison can point at it.
        foreach (var id in fit.Items.Keys)
        {
            _items.TryAdd(id, Guid.NewGuid());
        }

        var notes = await CompareAllAsync(snapshot, fit, checklists, references, ct);
        var tops = await TopTitlesAsync(snapshot, fit, ct);
        var (labelsToCreate, effort) = await LabelsAndEffortAsync(snapshot, fit, tops, ct);
        var history = await NewHistoryAsync(snapshot, ct);
        var samples = await ImportPlanner.SampleAsync(snapshot, [[.. keysTaken.Take(MaxReportSamples)]], ct);

        return counted with
        {
            Update = new PlanCount(notes.Updated.Count, [.. notes.Updated.Take(MaxReportSamples)]),
            Conflicts = new PlanCount(notes.Conflicts.Count, [.. notes.Conflicts.Take(MaxReportSamples)]),
            KeysTaken = new PlanCount(keysTaken.Count, [.. keysTaken.Take(MaxReportSamples).Where(samples.ContainsKey).Select(id => samples[id])]),
            DeletedInSource = DeletedSamples(read),
            ProjectKeysTaken = taken,
            ProjectsMissing = missing,
            LabelsToCreate = labelsToCreate,
            SprintsToCreate = sprintsToCreate,
            MilestonesToCreate = milestones,
            EffortByProject = effort,
            Comments = history.Comments,
            Attachments = history.Attachments,
            AttachmentBytes = history.Bytes,
            AttachmentsOfUnknownSize = history.UnknownSize,
            Changes = history.Changes,
        };
    }

    private async Task<(List<string> Taken, List<string> Missing, List<string> KeysTaken)> ProjectsAndKeysAsync(SourceRead read, FitResult fit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = read.Targets.Select(t => t.Key).Distinct().ToList();
        var existing = await db.Projects.AsNoTracking().Where(p => keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal, ct);
        var ours = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Project).Select(i => i.EntityId).ToListAsync(ct);

        var (taken, missing) = (new List<string>(), new List<string>());
        foreach (var group in read.Targets.GroupBy(t => t.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var wantsExisting = group.Any(t => t.Existing);
            if (existing.TryGetValue(group.Key, out var id))
            {
                if (!wantsExisting && !ours.Contains(id))
                {
                    taken.Add(group.Key);
                }
            }
            else if (wantsExisting)
            {
                missing.Add(group.Key);
            }
        }

        var keysTaken = new List<string>();
        foreach (var group in read.Refs.Where(r => fit.Items.ContainsKey(r.Id) && !_known.ContainsKey(r.Id)).GroupBy(r => r.ProjectKey))
        {
            var used = existing.TryGetValue(group.Key, out var projectId)
                ? (await db.Issues.AsNoTracking().Where(i => i.ProjectId == projectId).Select(i => i.Number).ToListAsync(ct)).ToHashSet()
                : [];
            keysTaken.AddRange(group.Where(r => SourceNumber(r.Key) is { } number && !used.Add(number)).Select(r => r.Id));
        }

        return (taken, missing, keysTaken);
    }

    /// <summary>Counts the sprints to create, and gives each a stand-in ID for the comparison.</summary>
    private int StandInSprints(SourceRead read)
    {
        var known = _header.Sprints.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var created = 0;
        foreach (var (sprintId, projectKey) in read.Sprints.Where(s => known.Contains(s.SprintId)))
        {
            created += _sprints.TryAdd(SprintKey(sprintId, projectKey), Guid.NewGuid()) ? 1 : 0;
        }

        return created;
    }

    private async Task<int> MilestonesToCreateAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var done = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Version).Select(i => i.SourceId).ToListAsync(ct);
        var dated = _header.Versions.Where(v => v.Date is not null).Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        return _mapping.MilestoneVersions.Count(v => dated.Contains(v) && !done.Contains(v));
    }

    private async Task<RerunNotes> CompareAllAsync(
        SnapshotReader snapshot, FitResult fit, ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references, CancellationToken ct)
    {
        var notes = new RerunNotes();
        if (_known.Count == 0)
        {
            return notes;
        }

        async Task CompareBatchAsync(List<SourceItem> batch)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await CompareAsync(db, batch, fit, checklists, references, [], [], new ImportRunCounts(), notes, ct);
        }

        var batch = new List<SourceItem>(BatchSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (seen.Add(item.Id) && _known.ContainsKey(item.Id) && _source.InScope(item))
            {
                batch.Add(item);
            }

            if (batch.Count == BatchSize)
            {
                await CompareBatchAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await CompareBatchAsync(batch);
        }

        return notes;
    }

    private async Task<(int Labels, List<PlanProjectEffort> Effort)> LabelsAndEffortAsync(SnapshotReader snapshot, FitResult fit, Dictionary<string, string> tops, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hours = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (!seen.Add(item.Id) || !fit.Items.ContainsKey(item.Id) || !_source.InScope(item))
            {
                continue;
            }

            names.UnionWith(_description.Labels(item, _source.Type(item), tops.GetValueOrDefault(item.Id), []));
            var key = _source.Project(item.Project).Key;
            hours[key] = hours.GetValueOrDefault(key) + (_source.Effort(item) ?? 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wanted = names.ToList();
        var existing = await db.Labels.AsNoTracking().CountAsync(l => wanted.Contains(l.Name), ct);

        var keys = hours.Keys.ToList();
        var capacity = (await db.ProjectCapacity.AsNoTracking()
                .Join(db.Projects, c => c.ProjectId, p => p.Id, (c, p) => new { p.Key, c.FromMonth, c.People })
                .Where(c => keys.Contains(c.Key))
                .OrderBy(c => c.FromMonth)
                .ToListAsync(ct))
            .ToLookup(c => c.Key, c => new CapacityEntry(c.FromMonth, c.People), StringComparer.Ordinal);
        var projectsThere = (await db.Projects.AsNoTracking().Where(p => keys.Contains(p.Key)).Select(p => p.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return (names.Count - existing, [.. hours.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => new PlanProjectEffort(h.Key, h.Value,
            projectsThere.Contains(h.Key) ? [.. capacity[h.Key]] : [.. CapacityOf(h.Key)]))]);
    }

    /// <summary>The history a commit would still write: what earlier runs brought across is not counted again.</summary>
    private async Task<(int Comments, int Attachments, long Bytes, int UnknownSize, int Changes)> NewHistoryAsync(SnapshotReader snapshot, CancellationToken ct)
    {
        var comments = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var chunk in snapshot.ReadCommentsAsync(ct).Where(c => seen.Add(c.Id) && IssueOf(c.Item) is not null).Chunk(HistoryChunk).WithCancellation(ct))
        {
            var done = await DoneAsync(ImportedKinds.Comment, chunk.Select(c => c.Id), ct);
            comments += chunk.Count(c => !done.Contains(c.Id));
        }

        var (attachments, bytes, unknownSize) = (0, 0L, 0);
        seen.Clear();
        await foreach (var chunk in snapshot.ReadAttachmentsAsync(ct).Where(a => seen.Add(a.Id) && IssueOf(a.Item) is not null).Chunk(HistoryChunk).WithCancellation(ct))
        {
            var done = await DoneAsync(ImportedKinds.Attachment, chunk.Select(a => a.Id), ct);
            foreach (var attachment in chunk.Where(a => !done.Contains(a.Id)))
            {
                attachments++;
                bytes += attachment.Size ?? 0;
                unknownSize += attachment.Size is null ? 1 : 0;
            }
        }

        Dictionary<string, string> prior;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            prior = await db.ImportedItems.AsNoTracking()
                .Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item && i.SeenChangeMark != null)
                .ToDictionaryAsync(i => i.SourceId, i => i.SeenChangeMark!, StringComparer.Ordinal, ct);
        }

        var changes = await snapshot.ReadChangesAsync(ct)
            .CountAsync(c => _items.ContainsKey(c.Item) && !(prior.TryGetValue(c.Item, out var mark) && string.CompareOrdinal(Mark(c), mark) <= 0), ct);
        return (comments, attachments, bytes, unknownSize, changes);
    }
}
