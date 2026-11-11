using System.Text.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Realtime;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>What a finished commit has to tell the owner, stored as its <see cref="ImportRun.Report"/>.</summary>
public sealed record ImportRunReport(PlanCount Conflicts, PlanCount DeletedInSource);

/// <summary>
/// Items an earlier run imported (ADR-019 Re-runs and conflicts). A field is updated only while Kupro still holds what
/// was last imported; where both sides changed it, Kupro wins and a conflict records both values. An item's type stays
/// as it is, and its parent moves only when the source moved it.
/// </summary>
public sealed partial class ImportCommit
{
    private const int MaxReportSamples = 5;

    private async Task UpdateExistingAsync(
        SnapshotReader snapshot, FitResult fit, ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references, CancellationToken ct)
    {
        if (_known.Count == 0)
        {
            return;
        }

        var batch = new List<SourceItem>(BatchSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (!seen.Add(item.Id) || !_known.ContainsKey(item.Id) || !_source.InScope(item))
            {
                continue;
            }

            batch.Add(item);
            if (batch.Count == BatchSize)
            {
                await UpdateBatchAsync(batch, fit, checklists, references, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await UpdateBatchAsync(batch, fit, checklists, references, ct);
        }
    }

    private async Task UpdateBatchAsync(
        List<SourceItem> batch, FitResult fit, ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references, CancellationToken ct)
    {
        var newPeople = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var newLabels = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            newPeople.Clear();
            newLabels.Clear();
            delta = new ImportRunCounts();
            var changes = await CompareAsync(db, batch, fit, checklists, references, newPeople, newLabels, delta, null, ct);
            await issues.ApplyImportedAsync(db, changes, ct);
            await SaveCountsAsync(db, delta, ct);
        }, ct);

        Merge(newPeople);
        foreach (var (name, id) in newLabels)
        {
            _labels[name] = id;
        }

        _counts.Add(delta);
        if (delta.Updated > 0)
        {
            await realtime.ChangedAsync(ChangeTopic.Issue);
        }
    }

    /// <summary>
    /// Decides every field of a batch against the tracked rows. Only the context changes: a commit saves it, and a plan
    /// throws it away after reading <paramref name="notes"/>.
    /// </summary>
    private async Task<List<ImportedIssueChange>> CompareAsync(
        KuproDbContext db, List<SourceItem> batch, FitResult fit, ILookup<string, (string Id, ChecklistLine Line)> checklists, ImportReferences references,
        Dictionary<string, Guid?> newPeople, Dictionary<string, Guid> newLabels, ImportRunCounts delta, RerunNotes? notes, CancellationToken ct)
    {
        var ids = batch.Select(i => i.Id).ToList();
        var rows = await db.ImportedItems.Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item && ids.Contains(i.SourceId))
            .ToDictionaryAsync(i => i.SourceId, StringComparer.Ordinal, ct);
        var issueIds = rows.Values.Select(r => r.EntityId).ToList();
        var loaded = await db.Issues.Include(i => i.Labels).Where(i => issueIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var conflicts = await db.ImportConflicts.Where(c => c.ImportId == _importId && issueIds.Contains(c.IssueId))
            .ToDictionaryAsync(c => (c.IssueId, c.Field), ct);
        var lineIds = batch.SelectMany(i => checklists[i.Id]).Select(c => c.Id).ToList();
        var lineRows = await db.ImportedItems.Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Checklist && lineIds.Contains(i.SourceId))
            .ToDictionaryAsync(i => i.SourceId, StringComparer.Ordinal, ct);

        await ResolvePeopleAsync(db, batch.Select(i => i.Assignee).OfType<string>().Distinct(), newPeople, delta, ct);
        var drafts = batch.Select(item =>
        {
            var last = ImportedField.Deserialize(rows[item.Id].LastValues!);
            var tooLong = new List<string>();
            var itemLabels = _description.Labels(item, _source.Type(item), last.FoldedTop, tooLong);
            return (Item: item, Last: last, Labels: itemLabels, TooLong: tooLong);
        }).ToList();
        await ResolveLabelsAsync(db, drafts.SelectMany(d => d.Labels).Distinct(StringComparer.Ordinal).ToList(), newLabels, delta, ct);
        var labelNames = (await db.Labels.AsNoTracking().Select(l => new { l.Id, l.Name }).ToListAsync(ct)).ToDictionary(l => l.Id, l => l.Name);

        var changes = new List<ImportedIssueChange>();
        var now = clock.GetUtcNow();
        foreach (var (item, last, itemLabels, tooLong) in drafts)
        {
            var row = rows[item.Id];
            var issue = loaded[row.EntityId];
            var assignee = item.Assignee is { } a ? Member(a, newPeople) : null;

            // A fold or mapping changed since doesn't move an imported item. Only the source moving it does.
            var moved = !string.Equals(item.Parent, row.SourceParent, StringComparison.Ordinal);
            var parent = !moved ? last.Parent : fit.Items[item.Id].Parent is { } fitted ? _items[fitted] : null;
            var (next, _) = ValuesOf(item, itemLabels, tooLong, assignee, parent, last.FoldedTop, checklists, references);
            var former = issue.Labels.Select(l => labelNames[l.LabelId]).Order(StringComparer.Ordinal).ToList();
            var current = new ImportedValues(
                issue.Title, issue.Description, issue.State, issue.Priority, issue.AssigneeId, issue.ParentId, issue.SprintId, issue.Effort, issue.StartDate, issue.DueDate, former);

            var decisions = ImportedField.All.ToDictionary(f => f, f => f.Decide(next, last, current));
            KeepDatesValid(decisions, next, current);

            var remembered = last;
            var apply = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (field, decision) in decisions)
            {
                conflicts.TryGetValue((issue.Id, field.Name), out var conflict);
                if (decision == ImportFieldDecision.Conflict)
                {
                    Disagree(db, conflicts, issue.Id, field, conflict, next, current, now);
                    notes?.Conflicts.Add(new PlanSample(field.Name, issue.Key, issue.Title));
                    continue;
                }

                if (conflict is { State: ImportConflictState.Open })
                {
                    conflict.State = ImportConflictState.Settled;
                    conflict.ResolvedAt = now;
                    conflict.UpdatedAt = now;
                }

                if (decision != ImportFieldDecision.Unchanged)
                {
                    remembered = field.Copy(remembered, next);
                }

                if (decision == ImportFieldDecision.Apply)
                {
                    apply.Add(field.Name);
                }
            }

            if (apply.Count > 0)
            {
                var labelIds = next.Labels.Select(l => _labels.TryGetValue(l, out var known) ? known : newLabels[l]).ToList();
                changes.Add(new ImportedIssueChange(
                    issue, apply, next.Title, next.Description, next.State, next.Priority, next.Assignee, next.Parent, next.Effort, next.Start, next.Due, next.Sprint,
                    labelIds, next.Labels, former, _ownerId, item.Updated.ToUniversalTime(), item.Resolved?.ToUniversalTime()));
                delta.Updated++;
                notes?.Updated.Add(new PlanSample(item.Id, item.Key, item.Title));
            }

            if (!ReferenceEquals(remembered, last))
            {
                row.LastValues = ImportedField.Serialize(remembered);
                row.UpdatedAt = now;
            }

            if (moved && decisions[ImportedField.Named(ImportedField.Parent)] != ImportFieldDecision.Conflict)
            {
                row.SourceParent = item.Parent;
                row.UpdatedAt = now;
            }

            RememberChecklists(db, lineRows, checklists[item.Id], item.Id, issue.Id, now);
        }

        return changes;
    }

    /// <summary>A start after a due date can't be stored. Two sides each moving one date can make one, so that takes the owner.</summary>
    private static void KeepDatesValid(Dictionary<ImportedField, ImportFieldDecision> decisions, ImportedValues next, ImportedValues current)
    {
        var (startField, dueField) = (ImportedField.Named(ImportedField.Start), ImportedField.Named(ImportedField.Due));
        var start = decisions[startField] == ImportFieldDecision.Apply ? next.Start : current.Start;
        var due = decisions[dueField] == ImportFieldDecision.Apply ? next.Due : current.Due;
        if (start <= due || start is null || due is null)
        {
            return;
        }

        foreach (var field in new[] { startField, dueField }.Where(f => decisions[f] == ImportFieldDecision.Apply))
        {
            decisions[field] = ImportFieldDecision.Conflict;
        }
    }

    private void Disagree(
        KuproDbContext db, Dictionary<(Guid IssueId, string Field), ImportConflict> conflicts, Guid issueId, ImportedField field, ImportConflict? conflict,
        ImportedValues next, ImportedValues current, DateTimeOffset now)
    {
        if (conflict is null)
        {
            conflict = new ImportConflict
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.RequireWorkspaceId(),
                ImportId = _importId,
                IssueId = issueId,
                Field = field.Name,
                CreatedAt = now,
            };
            db.ImportConflicts.Add(conflict);
            conflicts[(issueId, field.Name)] = conflict;
        }

        conflict.State = ImportConflictState.Open;
        conflict.KuproValue = field.ToJson(current);
        conflict.SourceValue = field.ToJson(next);
        conflict.ResolvedAt = null;
        conflict.UpdatedAt = now;
    }

    private void RememberChecklists(
        KuproDbContext db, Dictionary<string, ImportedItem> rows, IEnumerable<(string Id, ChecklistLine Line)> lines, string holder, Guid issueId, DateTimeOffset now)
    {
        foreach (var (id, line) in lines)
        {
            if (!rows.TryGetValue(id, out var row))
            {
                RecordChecklist(db, id, line, holder, issueId);
            }
            else if (_folded[id].Line != line)
            {
                row.LastValues = JsonSerializer.Serialize(line, Json);
                row.UpdatedAt = now;
            }
        }
    }

    private async Task<ImportRunReport> ReportAsync(SourceRead read, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = db.ImportConflicts.AsNoTracking().Where(c => c.ImportId == _importId && c.State == ImportConflictState.Open);
        var count = await open.CountAsync(ct);
        var samples = await open.OrderBy(c => c.Id).Take(MaxReportSamples)
            .Join(db.Issues, c => c.IssueId, x => x.Id, (c, x) => new PlanSample(c.Field, x.Key, x.Title))
            .ToListAsync(ct);

        _counts.Conflicts = count;
        _counts.DeletedInSource = read.Deleted.Count;
        return new ImportRunReport(
            new PlanCount(count, samples),
            DeletedSamples(read));
    }

    private PlanCount DeletedSamples(SourceRead read) =>
        new(read.Deleted.Count, [.. read.Deleted.Take(MaxReportSamples).Select(id => new PlanSample(id, _known[id].Key, _known[id].Title))]);

    /// <summary>What a plan reads off a comparison that is never saved.</summary>
    private sealed class RerunNotes
    {
        public List<PlanSample> Updated { get; } = [];
        public List<PlanSample> Conflicts { get; } = [];
    }
}
