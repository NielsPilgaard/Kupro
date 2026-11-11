using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Resolving a conflict (ADR-019 Re-runs and conflicts). Either way the source's value becomes the one last imported, so
/// the conflict comes back only if the source changes it again.
/// </summary>
public sealed partial class ImportService
{
    private const int MaxResolveAttempts = 3;

    /// <summary>Keep Kupro's closes the conflict; Use source writes the source's value as an import update.</summary>
    public async Task<IssueResult<ImportConflict>> ResolveConflictAsync(Guid importId, Guid conflictId, bool useSource, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            try
            {
                var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var resolved = await ResolveAsync(db, importId, conflictId, useSource, ct);
                    if (resolved.Error is null)
                    {
                        await db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                    }

                    return resolved;
                });

                if (result.Error is null && useSource)
                {
                    await realtime.ChangedAsync(ChangeTopic.Issue);
                }

                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxResolveAttempts)
            {
                // Someone saved the issue meanwhile; the next attempt reads what they wrote.
            }
        }
    }

    private async Task<IssueResult<ImportConflict>> ResolveAsync(KuproDbContext db, Guid importId, Guid conflictId, bool useSource, CancellationToken ct)
    {
        var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (import is null)
        {
            return new IssueError("import_not_found", $"No import {importId}.");
        }

        if (import.State != ImportState.Active)
        {
            return new IssueError("import_closed", "This import is finished or undone.");
        }

        // A run reads and writes the same rows, and would overwrite the choice.
        if (await db.ImportRuns.AnyAsync(r => r.ImportId == importId && r.Kind == ImportRunKind.Commit && (r.State == ImportRunState.Queued || r.State == ImportRunState.Running), ct))
        {
            return new IssueError("import_busy", "A run of this import is in progress. Resolve the conflict when it has finished.");
        }

        var conflict = await db.ImportConflicts.SingleOrDefaultAsync(c => c.Id == conflictId && c.ImportId == importId, ct);
        if (conflict is null)
        {
            return new IssueError("conflict_not_found", $"No conflict {conflictId}.");
        }

        if (conflict.State != ImportConflictState.Open)
        {
            return new IssueError("conflict_resolved", "This conflict is already resolved.");
        }

        var row = await db.ImportedItems.SingleAsync(i => i.ImportId == importId && i.SourceKind == ImportedKinds.Item && i.EntityId == conflict.IssueId, ct);
        var field = ImportedField.Named(conflict.Field);
        var source = field.WithJson(ImportedField.Deserialize(row.LastValues!), conflict.SourceValue!);
        var now = clock.GetUtcNow();
        if (useSource && await ApplySourceAsync(db, import, conflict, source, now, ct) is { } error)
        {
            return error;
        }

        row.LastValues = ImportedField.Serialize(source);
        row.UpdatedAt = now;
        conflict.State = useSource ? ImportConflictState.UsedSource : ImportConflictState.Kept;
        conflict.ResolvedAt = now;
        conflict.UpdatedAt = now;
        return conflict;
    }

    private async Task<IssueError?> ApplySourceAsync(KuproDbContext db, WorkspaceImport import, ImportConflict conflict, ImportedValues source, DateTimeOffset now, CancellationToken ct)
    {
        var issue = await db.Issues.Include(i => i.Labels).SingleAsync(i => i.Id == conflict.IssueId, ct);
        if (await InvalidAsync(db, issue, conflict.Field, source, ct) is { } invalid)
        {
            return invalid;
        }

        var labelIds = conflict.Field == ImportedField.Labels ? await labels.ResolveAsync(db, source.Labels, ct) : [];
        var ownIds = issue.Labels.Select(l => l.LabelId).ToList();
        var former = await db.Labels.AsNoTracking().Where(l => ownIds.Contains(l.Id)).Select(l => l.Name).OrderBy(n => n).ToListAsync(ct);
        var actor = workspace.MemberId == Guid.Empty ? import.CreatedById : workspace.MemberId;
        await issues.ApplyImportedAsync(db, [new ImportedIssueChange(
            issue, new HashSet<string>(StringComparer.Ordinal) { conflict.Field }, source.Title, source.Description, source.State, source.Priority, source.Assignee, source.Parent,
            source.Effort, source.Start, source.Due, source.Sprint, labelIds, source.Labels, former, actor, now, null)], ct);
        return null;
    }

    /// <summary>The source's value may no longer fit: time has passed since the run that found it.</summary>
    private static async Task<IssueError?> InvalidAsync(KuproDbContext db, Issue issue, string field, ImportedValues source, CancellationToken ct)
    {
        switch (field)
        {
            case ImportedField.Parent when source.Parent is { } parentId:
                var parent = await db.Issues.AsNoTracking().Where(i => i.Id == parentId).Select(i => (IssueType?)i.Type).SingleOrDefaultAsync(ct);
                return parent is { } type && IssueRules.CanBeParent(issue.Type, type)
                    ? null
                    : new IssueError("invalid_hierarchy", $"{issue.Key} can't sit under the source's parent any more.");
            case ImportedField.Assignee when source.Assignee is { } assignee:
                return await db.Members.AnyAsync(m => m.Id == assignee && (m.Status == MemberStatus.Active || m.Status == MemberStatus.Placeholder), ct)
                    ? null
                    : new IssueError("invalid_assignee", "The source's assignee is no longer a member of this workspace.");
            case ImportedField.Sprint when source.Sprint is { } sprint:
                return await db.Sprints.AnyAsync(s => s.Id == sprint, ct) ? null : new IssueError("invalid_sprint", "The source's sprint no longer exists.");
            case ImportedField.Start when source.Start > issue.DueDate:
            case ImportedField.Due when issue.StartDate > source.Due:
                return new IssueError("invalid_dates", "Start date is after due date. Change the other date first.");
            default:
                return null;
        }
    }
}
