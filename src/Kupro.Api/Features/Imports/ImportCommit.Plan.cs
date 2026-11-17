using Kupro.Api.Features.Plan;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// The dry run against current Kupro data (ADR-019 Pipeline, plan): the same <see cref="SourceRead"/> and fit a commit
/// runs, counted by <see cref="ImportPlanner"/>, plus what depends on the workspace from a re-run's own comparison on a
/// context that is never saved.
/// </summary>
public sealed partial class ImportCommit
{
    public async Task<ImportPlan> PlanAsync(WorkspaceImport import, SnapshotReader snapshot, ImportMapping mapping, CancellationToken ct)
    {
        Start(import, snapshot, mapping);
        await LoadKnownAsync(ct);

        var read = await SourceRead.ReadAsync(snapshot, _source, Tree, counting: true, ct);
        var fit = read.Fit;
        var checklists = Checklists(fit, read.ChecklistLines);
        _fit = fit;

        var (needs, keysTaken, milestones) = await WorkspaceChecksAsync(read, ct);
        var sprintsToCreate = StandInSprints(read);
        var references = await ReferencesAsync(snapshot, read, [], ct);

        // Stand-ins for what the commit would create, so a comparison can point at it.
        foreach (var id in fit.Items.Keys)
        {
            _items.TryAdd(id, Guid.NewGuid());
        }

        var notes = await CompareAllAsync(snapshot, fit, checklists, references, ct);
        var tops = await TopTitlesAsync(snapshot, fit, ct);
        var (labelsToCreate, effort) = await LabelsAndEffortAsync(snapshot, read, tops, ct);
        var counted = await ImportPlanner.PlanAsync(snapshot, read, new ImportedHistory((kind, ids) => DoneAsync(kind, ids, ct), await ChangeMarksAsync(ct)), keysTaken, ct);

        return counted with
        {
            Update = new PlanCount(notes.Updated.Count, [.. notes.Updated.Take(MaxReportSamples)]),
            Conflicts = new PlanCount(notes.Conflicts.Count, [.. notes.Conflicts.Take(MaxReportSamples)]),
            DeletedInSource = DeletedSamples(read),
            ProjectKeysTaken = needs.Taken,
            ProjectsMissing = needs.Missing,
            LabelsToCreate = labelsToCreate,
            SprintsToCreate = sprintsToCreate,
            MilestonesToCreate = milestones,
            EffortByProject = effort,
        };
    }

    /// <summary>The commit's own checks on projects, numbers and milestones, without writing.</summary>
    private async Task<(ProjectNeeds Needs, List<string> KeysTaken, int Milestones)> WorkspaceChecksAsync(SourceRead read, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var needs = await ProjectNeedsAsync(db, read.Targets, ct);
        var keysTaken = new List<string>();
        foreach (var group in read.New.GroupBy(r => r.ProjectKey))
        {
            var used = needs.Existing.TryGetValue(group.Key, out var projectId)
                ? (await db.Issues.AsNoTracking().Where(i => i.ProjectId == projectId).Select(i => i.Number).ToListAsync(ct)).ToHashSet()
                : [];
            keysTaken.AddRange(KeepNumbers(group, used).Pending.Where(r => SourceNumber(r.Key) is not null).Select(r => r.Id));
        }

        var milestones = await MilestonesToCreateAsync(db, read.Targets.Select(t => t.Key), ct);
        return (needs, keysTaken, milestones.Count);
    }

    /// <summary>Counts the sprints to create, and gives each a stand-in ID for the comparison.</summary>
    private int StandInSprints(SourceRead read)
    {
        var created = 0;
        foreach (var (sprintId, projectKey) in SprintsToCreate(read).ToList())
        {
            created += _sprints.TryAdd(SprintKey(sprintId, projectKey), Guid.NewGuid()) ? 1 : 0;
        }

        return created;
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

    private async Task<(int Labels, List<PlanProjectEffort> Effort)> LabelsAndEffortAsync(SnapshotReader snapshot, SourceRead read, Dictionary<string, string> tops, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (seen.Add(item.Id) && read.KeptIds.Contains(item.Id) && read.Fit.Items.ContainsKey(item.Id))
            {
                names.UnionWith(_values.Labels(item, tops.GetValueOrDefault(item.Id), []));
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wanted = names.ToList();
        var existing = await db.Labels.AsNoTracking().CountAsync(l => wanted.Contains(l.Name), ct);

        var keys = read.Hours.Keys.ToList();
        var capacity = (await db.ProjectCapacity.AsNoTracking()
                .Join(db.Projects, c => c.ProjectId, p => p.Id, (c, p) => new { p.Key, c.FromMonth, c.People })
                .Where(c => keys.Contains(c.Key))
                .OrderBy(c => c.FromMonth)
                .ToListAsync(ct))
            .ToLookup(c => c.Key, c => new CapacityEntry(c.FromMonth, c.People), StringComparer.Ordinal);
        var projectsThere = (await db.Projects.AsNoTracking().Where(p => keys.Contains(p.Key)).Select(p => p.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return (names.Count - existing, [.. read.Hours.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => new PlanProjectEffort(h.Key, h.Value,
            projectsThere.Contains(h.Key) ? [.. capacity[h.Key]] : [.. CapacityOf(h.Key)]))]);
    }
}
