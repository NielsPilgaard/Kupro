using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>A record the import didn't create that points at one of its issues. Undo waits until it is moved or deleted.</summary>
/// <param name="Kind"><c>comment</c>, <c>attachment</c>, <c>relation</c> or <c>child</c>.</param>
public sealed record UndoBlocker(string Kind, string IssueKey, Guid Id);

/// <summary>What undoing would lose and what stands in its way. Lists stop at <see cref="ImportService.MaxListed"/>; the counts don't.</summary>
public sealed record ImportUndoCheck(int EditedCount, IReadOnlyList<string> Edited, int BlockerCount, IReadOnlyList<UndoBlocker> Blockers);

/// <summary>Undo's answer: done, or refused with the check that says why.</summary>
public sealed record ImportUndoResult(bool Undone, ImportUndoCheck Check, int Issues);

/// <summary>Undo and Finish (ADR-019 Re-runs and conflicts): the two ways an import ends.</summary>
public sealed partial class ImportService
{
    public const int MaxListed = 50;

    /// <summary>The warning before Undo: issues edited in Kupro since, and records that block it.</summary>
    public async Task<IssueResult<ImportUndoCheck>> CheckUndoAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await ClosedAsync(db, importId, ct) is { } error)
        {
            return error;
        }

        return await CheckAsync(db, importId, ct);
    }

    /// <summary>
    /// Deletes what the import created: issues with their history, its comments and files, and the sprints, labels,
    /// milestones, projects and placeholders it made that nothing else uses now. Refused while a record it didn't make
    /// points at one of its issues, so nothing outside the import is lost.
    /// </summary>
    public async Task<IssueResult<ImportUndoResult>> UndoAsync(Guid importId, CancellationToken ct)
    {
        var workspaceId = workspace.RequireWorkspaceId();
        List<Guid> files = [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await ClosedAsync(db, importId, ct) is { } error)
            {
                return (IssueResult<ImportUndoResult>)error;
            }

            // Locks the import row, so a run can't be queued while this deletes under it.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM imports WHERE id = {importId} FOR UPDATE", ct);
            var check = await CheckAsync(db, importId, ct);
            if (check.BlockerCount > 0)
            {
                return new ImportUndoResult(false, check, 0);
            }

            files = await Ours(db, importId, ImportedKinds.Attachment).ToListAsync(ct);
            var issueCount = await DeleteCreatedAsync(db, workspaceId, importId, ct);
            await db.WorkspaceImports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, ImportState.Undone)
                .SetProperty(i => i.AutoSync, false)
                .SetProperty(i => i.EncryptedToken, (string?)null)
                .SetProperty(i => i.FinishedAt, clock.GetUtcNow()), ct);
            await tx.CommitAsync(ct);
            return new ImportUndoResult(true, check, issueCount);
        });

        if (result.Value is { Undone: true })
        {
            // The rows are gone; a file that fails to delete only costs storage, so it doesn't undo the undo.
            foreach (var id in files)
            {
                await attachments.DeleteImportedAsync(id, ct);
            }

            await snapshots.DeleteAsync(workspaceId, importId, ct);
            await realtime.ChangedAsync(ChangeTopic.Issue);
            await realtime.ChangedAsync(ChangeTopic.Plan);
        }

        return result;
    }

    /// <summary>Ends the import: no more runs or auto-sync, and the source token is deleted. Aliases and history stay.</summary>
    public async Task<IssueResult<WorkspaceImport>> FinishAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await ClosedAsync(db, importId, ct) is { } error)
        {
            return error;
        }

        var import = await db.WorkspaceImports.SingleAsync(i => i.Id == importId, ct);
        import.State = ImportState.Finished;
        import.AutoSync = false;
        import.EncryptedToken = null;
        import.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await snapshots.DeleteAsync(import.WorkspaceId, importId, ct);
        return import;
    }

    /// <summary>Only an active import with no run in progress can end: a run would write under the change.</summary>
    private static async Task<IssueError?> ClosedAsync(KuproDbContext db, Guid importId, CancellationToken ct)
    {
        var state = await db.WorkspaceImports.Where(i => i.Id == importId).Select(i => (ImportState?)i.State).SingleOrDefaultAsync(ct);
        if (state is null)
        {
            return new IssueError("import_not_found", $"No import {importId}.");
        }

        if (state != ImportState.Active)
        {
            return new IssueError("import_closed", "This import is finished or undone.");
        }

        return await db.ImportRuns.AnyAsync(r => r.ImportId == importId && r.Kind == ImportRunKind.Commit && (r.State == ImportRunState.Queued || r.State == ImportRunState.Running), ct)
            ? new IssueError("import_busy", "A run of this import is in progress. Try again when it has finished.")
            : null;
    }

    private static IQueryable<Guid> Ours(KuproDbContext db, Guid importId, string kind) =>
        db.ImportedItems.Where(i => i.ImportId == importId && i.SourceKind == kind).Select(i => i.EntityId);

    private static async Task<ImportUndoCheck> CheckAsync(KuproDbContext db, Guid importId, CancellationToken ct)
    {
        var issues = Ours(db, importId, ImportedKinds.Item);
        var keyOf = db.Issues.AsNoTracking();

        // Edited in Kupro: anything the feed holds for these issues that didn't come through the import.
        var edited = db.Activity.AsNoTracking().Where(a => issues.Contains(a.IssueId) && a.Via != Surface.Import).Select(a => a.IssueId).Distinct();
        var editedKeys = await keyOf.Where(i => edited.Contains(i.Id)).OrderBy(i => i.Key).Select(i => i.Key).Take(MaxListed).ToListAsync(ct);

        var comments = db.Comments.AsNoTracking().Where(c => issues.Contains(c.IssueId) && !Ours(db, importId, ImportedKinds.Comment).Contains(c.Id))
            .Join(keyOf, c => c.IssueId, i => i.Id, (c, i) => new BlockerRow { Kind = "comment", IssueKey = i.Key, Id = c.Id });
        var files = db.Attachments.AsNoTracking().Where(a => a.IssueId != null && issues.Contains(a.IssueId.Value) && !Ours(db, importId, ImportedKinds.Attachment).Contains(a.Id))
            .Join(keyOf, a => a.IssueId, i => i.Id, (a, i) => new BlockerRow { Kind = "attachment", IssueKey = i.Key, Id = a.Id });
        var relations = db.IssueRelations.AsNoTracking()
            .Where(r => (issues.Contains(r.SourceId) || issues.Contains(r.TargetId)) && !Ours(db, importId, ImportedKinds.Relation).Contains(r.Id))
            .Join(keyOf, r => r.SourceId, i => i.Id, (r, i) => new BlockerRow { Kind = "relation", IssueKey = i.Key, Id = r.Id });
        var children = keyOf.Where(i => i.ParentId != null && issues.Contains(i.ParentId.Value) && !issues.Contains(i.Id))
            .Select(i => new BlockerRow { Kind = "child", IssueKey = i.Key, Id = i.Id });

        var blockers = new List<UndoBlocker>();
        var count = 0;
        foreach (var query in new[] { comments, files, relations, children })
        {
            count += await query.CountAsync(ct);
            blockers.AddRange((await query.OrderBy(b => b.IssueKey).Take(MaxListed - blockers.Count).ToListAsync(ct)).Select(b => new UndoBlocker(b.Kind, b.IssueKey, b.Id)));
        }

        return new ImportUndoCheck(await edited.CountAsync(ct), editedKeys, count, blockers);
    }

    private static async Task<int> DeleteCreatedAsync(KuproDbContext db, Guid workspaceId, Guid importId, CancellationToken ct)
    {
        var issues = Ours(db, importId, ImportedKinds.Item);

        // Files first: their rows would outlive the issues as orphans. Parents next, so the issues delete in any order.
        await db.Attachments.Where(a => Ours(db, importId, ImportedKinds.Attachment).Contains(a.Id)).ExecuteDeleteAsync(ct);
        await db.Issues.Where(i => issues.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ParentId, (Guid?)null), ct);

        // Comments, the feed, relations, labels on them, aliases, conflicts and ledger rows go with each issue.
        var deleted = await db.Issues.Where(i => issues.Contains(i.Id)).ExecuteDeleteAsync(ct);

        await db.Sprints.Where(s => Ours(db, importId, ImportedKinds.Sprint).Contains(s.Id) && !db.Issues.Any(i => i.SprintId == s.Id)).ExecuteDeleteAsync(ct);
        await db.Milestones.Where(m => Ours(db, importId, ImportedKinds.Version).Contains(m.Id)).ExecuteDeleteAsync(ct);
        await db.Labels.Where(l => Ours(db, importId, ImportedKinds.Label).Contains(l.Id) && !db.IssueLabels.Any(x => x.LabelId == l.Id)).ExecuteDeleteAsync(ct);
        await db.Projects.Where(p => Ours(db, importId, ImportedKinds.Project).Contains(p.Id)
                && !db.Issues.Any(i => i.ProjectId == p.Id) && !db.Sprints.Any(s => s.ProjectId == p.Id)
                && !db.Milestones.Any(m => m.ProjectId == p.Id) && !db.Meetings.Any(m => m.ProjectId == p.Id))
            .ExecuteDeleteAsync(ct);

        // A placeholder someone has since invited is a person now; one still referenced holds someone's history.
        var placeholders = await db.Members.Where(m => Ours(db, importId, ImportedKinds.Person).Contains(m.Id) && m.Status == MemberStatus.Placeholder)
            .Select(m => m.Id).ToListAsync(ct);
        foreach (var id in placeholders)
        {
            if (!await MemberMerge.IsReferencedAsync(db, workspaceId, id, ct))
            {
                await db.Members.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
            }
        }

        await db.ImportedItems.Where(i => i.ImportId == importId).ExecuteDeleteAsync(ct);
        return deleted;
    }

    /// <summary>A blocker as a query can build and sort it.</summary>
    private sealed class BlockerRow
    {
        public required string Kind { get; init; }
        public required string IssueKey { get; init; }
        public Guid Id { get; init; }
    }
}
