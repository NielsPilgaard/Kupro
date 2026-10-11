using Flowstate.Api.Data;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Realtime;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Features.Comments;

/// <summary>Comments on issues (task 008). Owns Comment; anyone in the workspace may comment, only the author edits.</summary>
public sealed class CommentService(
    IDbContextFactory<FlowstateDbContext> dbFactory, WorkspaceContext workspace, RealtimeEvents realtime, TimeProvider clock)
{
    public async Task<IssueResult<CommentResponse>> AddAsync(string key, CommentRequest request, CancellationToken ct)
    {
        var normalized = key.Trim().ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var issueId = await db.Issues.Where(i => i.Key == normalized).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct);
        if (issueId is null)
        {
            return IssueError.NotFound(normalized);
        }

        var comment = new Comment
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            IssueId = issueId.Value,
            AuthorId = workspace.MemberId,
            Via = workspace.Surface,
            Body = request.Body.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Issue, normalized);
        return (await GetAsync(db, comment.Id, ct))!;
    }

    public async Task<IssueResult<CommentResponse>> UpdateAsync(Guid id, CommentRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (AuthorCheck(comment, id) is { } error)
        {
            return error;
        }

        comment!.Body = request.Body.Trim();
        comment.EditedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var response = (await GetAsync(db, id, ct))!;
        await realtime.ChangedAsync(ChangeTopic.Issue, response.IssueKey);
        return response;
    }

    public async Task<IssueError?> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (AuthorCheck(comment, id) is { } error)
        {
            return error;
        }

        var key = await db.Issues.Where(i => i.Id == comment!.IssueId).Select(i => i.Key).SingleAsync(ct);
        db.Comments.Remove(comment!);
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Issue, key);
        return null;
    }

    private IssueError? AuthorCheck(Comment? comment, Guid id) =>
        comment is null ? new IssueError("comment_not_found", $"No comment {id}.")
        : comment.AuthorId != workspace.MemberId ? new IssueError("not_comment_author", "Only the author can change a comment.")
        : null;

    private static Task<CommentResponse?> GetAsync(FlowstateDbContext db, Guid id, CancellationToken ct) =>
        db.Comments.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new CommentResponse(
                c.Id,
                db.Issues.Where(i => i.Id == c.IssueId).Select(i => i.Key).First(),
                db.Members.Where(m => m.Id == c.AuthorId)
                    .Select(m => new MemberRef(
                        m.Id,
                        m.Kind == MemberKind.Agent
                            ? m.AgentName!
                            : db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                        m.Kind))
                    .First(),
                c.Via,
                c.Body,
                c.CreatedAt,
                c.EditedAt))
            .SingleOrDefaultAsync(ct);
}
