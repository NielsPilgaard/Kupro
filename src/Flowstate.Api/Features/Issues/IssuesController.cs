using Flowstate.Api.Common;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Issues;

/// <summary>Issues, addressed by key (<c>FLOW-12</c>). Mutations accept an <c>Idempotency-Key</c> header.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/issues")]
public sealed class IssuesController(IssueService issues) : ControllerBase
{
    /// <summary>List issues with filters and cursor pagination.</summary>
    [HttpGet(Name = "listIssues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IssuePage>> List([FromQuery] IssueQuery query, CancellationToken ct) =>
        this.ToResult(await issues.ListAsync(query, ct));

    /// <summary>Get an issue with its children, relations and labels.</summary>
    [HttpGet("{key}", Name = "getIssue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssueDetail>> Get(string key, CancellationToken ct) =>
        await issues.GetAsync(key, ct) is { } issue ? issue : this.ToProblem(IssueError.NotFound(key));

    /// <summary>Create an issue.</summary>
    [HttpPost(Name = "createIssue")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<IssueDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IssueDetail>> Create(CreateIssueRequest request, CancellationToken ct) =>
        this.ToResult(await issues.CreateAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Create up to 100 issues at once, all-or-nothing.</summary>
    [HttpPost("bulk", Name = "createIssues")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<IReadOnlyList<IssueSummary>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<IssueSummary>>> CreateMany(CreateIssuesRequest request, CancellationToken ct) =>
        this.ToResult(await issues.CreateManyAsync(request.Issues, ct), StatusCodes.Status201Created);

    /// <summary>Update an issue. Only fields you send change; list fields in <c>clear</c> to empty them.</summary>
    [HttpPatch("{key}", Name = "updateIssue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssueDetail>> Update(string key, UpdateIssueRequest request, CancellationToken ct) =>
        this.ToResult(await issues.UpdateAsync(key, request, ct));

    /// <summary>Update up to 100 issues at once, all-or-nothing.</summary>
    [HttpPatch("bulk", Name = "updateIssues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<IssueSummary>>> UpdateMany(UpdateIssuesRequest request, CancellationToken ct) =>
        this.ToResult(await issues.UpdateManyAsync(request.Items, ct));

    /// <summary>Move an issue on the board: change state and/or position between two neighbours.</summary>
    [HttpPost("{key}/move", Name = "moveIssue")]
    [NoAgentSurface(AgentSurface.Mcp, "Board ordering is a UI gesture; agents change state with update_issues.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssueSummary>> Move(string key, MoveIssueRequest request, CancellationToken ct) =>
        this.ToResult(await issues.MoveAsync(key, request, ct));

    /// <summary>Relate an issue to another: blocks, blocked by, relates, duplicates, duplicated by.</summary>
    [HttpPost("{key}/relations", Name = "addIssueRelation")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<IssueRelationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IssueRelationResponse>> AddRelation(string key, AddRelationRequest request, CancellationToken ct) =>
        this.ToResult(await issues.AddRelationAsync(key, request, ct), StatusCodes.Status201Created);

    /// <summary>Remove a relation.</summary>
    [HttpDelete("{key}/relations/{relationId:guid}", Name = "removeIssueRelation")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRelation(string key, Guid relationId, CancellationToken ct) =>
        await issues.RemoveRelationAsync(key, relationId, ct) is { } error ? this.ToProblem(error) : NoContent();
}

/// <summary>Workspace labels. Labels are created by using them on an issue.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/labels")]
public sealed class LabelsController(LabelService labels) : ControllerBase
{
    /// <summary>List labels with usage counts.</summary>
    [HttpGet(Name = "listLabels")]
    public Task<IReadOnlyList<LabelResponse>> List(CancellationToken ct) => labels.ListAsync(ct);

    /// <summary>Rename or recolour a label.</summary>
    [HttpPatch("{name}", Name = "updateLabel")]
    [NoAgentSurface(AgentSurface.Mcp, "Label presentation (name, colour) is a human concern.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LabelResponse>> Update(string name, UpdateLabelRequest request, CancellationToken ct) =>
        this.ToResult(await labels.UpdateAsync(name, request, ct));
}

internal static class IssueResults
{
    public static ActionResult<T> ToResult<T>(this ControllerBase controller, IssueResult<T> result,
        int successStatus = StatusCodes.Status200OK) =>
        result.Error is { } error ? controller.ToProblem(error)
        : successStatus == StatusCodes.Status200OK ? new OkObjectResult(result.Value)
        : controller.StatusCode(successStatus, result.Value);

    public static ObjectResult ToProblem(this ControllerBase controller, IssueError error) =>
        controller.Problem(error.Code switch
        {
            "issue_not_found" or "label_not_found" or "relation_not_found" or "sprint_not_found" => StatusCodes.Status404NotFound,
            "relation_exists" or "label_taken" or "sprint_already_active" or "sprint_not_active" or "sprint_not_planned"
                or "no_next_sprint" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, error.Code, error.Detail);
}
