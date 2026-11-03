using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Sprints;

/// <summary>Sprints per project. Issues join a sprint through the issue's <c>sprint</c> field.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/sprints")]
public sealed class SprintsController(SprintService sprints) : ControllerBase
{
    /// <summary>List sprints, optionally for one project and in given states.</summary>
    [HttpGet(Name = "listSprints")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<SprintResponse>>> List(
        [FromQuery] string? project, [FromQuery] SprintState[]? state, CancellationToken ct) =>
        this.ToResult(await sprints.ListAsync(project, state, ct));

    /// <summary>Get a sprint with its issue count and effort totals.</summary>
    [HttpGet("{id:guid}", Name = "getSprint")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SprintResponse>> Get(Guid id, CancellationToken ct) =>
        this.ToResult(await sprints.GetAsync(id, ct));

    /// <summary>Plan a sprint for a project.</summary>
    [HttpPost(Name = "createSprint")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<SprintResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SprintResponse>> Create(CreateSprintRequest request, CancellationToken ct) =>
        this.ToResult(await sprints.CreateAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Rename a sprint or change its dates or goal.</summary>
    [HttpPatch("{id:guid}", Name = "updateSprint")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [NoAgentSurface(AgentSurface.Mcp, "Renaming and re-dating sprints is a planning-meeting task; agents plan with create/start/close.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SprintResponse>> Update(Guid id, UpdateSprintRequest request, CancellationToken ct) =>
        this.ToResult(await sprints.UpdateAsync(id, request, ct));

    /// <summary>Start a planned sprint. A project has at most one active sprint.</summary>
    [HttpPost("{id:guid}/start", Name = "startSprint")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SprintResponse>> Start(Guid id, CancellationToken ct) =>
        this.ToResult(await sprints.StartAsync(id, ct));

    /// <summary>Close the active sprint with an outcome per unfinished issue: carry, split, backlog, cancel or done.</summary>
    [HttpPost("{id:guid}/close", Name = "closeSprint")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloseSprintResponse>> Close(Guid id, CloseSprintRequest request, CancellationToken ct) =>
        this.ToResult(await sprints.CloseAsync(id, request, ct));

    /// <summary>What closing needs, or the sprint report once closed: summary, unfinished and completed issues, open target sprints.</summary>
    [HttpGet("{id:guid}/review", Name = "getSprintReview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SprintReview>> Review(Guid id, CancellationToken ct) =>
        this.ToResult(await sprints.ReviewAsync(id, ct));

    /// <summary>Scope, remaining and ideal hours per day; with <c>by=assignee</c>, also one series per assignee.</summary>
    [HttpGet("{id:guid}/burndown", Name = "getSprintBurndown")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SprintBurndown>> Burndown(Guid id, [FromQuery] BurndownGrouping? by, CancellationToken ct) =>
        this.ToResult(await sprints.BurndownAsync(id, by, ct));
}
