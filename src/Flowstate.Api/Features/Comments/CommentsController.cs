using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Comments;

/// <summary>Issue comments and the issue's activity feed (task 008).</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}")]
public sealed class CommentsController(CommentService comments, ActivityFeed feed) : ControllerBase
{
    /// <summary>An issue's history and comments, oldest first (the most recent 500 of each).</summary>
    [HttpGet("issues/{key}/activity", Name = "listIssueActivity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<FeedItem>>> Activity(string key, CancellationToken ct) =>
        await feed.GetAsync(key, ct) is { } items ? Ok(items) : this.ToProblem(IssueError.NotFound(key));

    /// <summary>Comment on an issue. Markdown; guests may comment too.</summary>
    [HttpPost("issues/{key}/comments", Name = "addComment")]
    [WorkspaceRole(WorkspaceRole.Guest)]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommentResponse>> Add(string key, CommentRequest request, CancellationToken ct) =>
        this.ToResult(await comments.AddAsync(key, request, ct), StatusCodes.Status201Created);

    /// <summary>Edit your own comment.</summary>
    [HttpPatch("comments/{id:guid}", Name = "updateComment")]
    [WorkspaceRole(WorkspaceRole.Guest)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommentResponse>> Update(Guid id, CommentRequest request, CancellationToken ct) =>
        this.ToResult(await comments.UpdateAsync(id, request, ct));

    /// <summary>Delete your own comment.</summary>
    [HttpDelete("comments/{id:guid}", Name = "deleteComment")]
    [WorkspaceRole(WorkspaceRole.Guest)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await comments.DeleteAsync(id, ct) is { } error ? this.ToProblem(error) : NoContent();
}
