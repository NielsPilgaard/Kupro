using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Plan;

/// <summary>Delivery plan: demand vs supply per month, milestones and project capacity (task 011).</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}")]
public sealed class PlanController(PlanService plan) : ControllerBase
{
    /// <summary>
    /// The delivery plan for a horizon: epics and features per project, per-month demand / supply / gap in hours and
    /// people, milestones, dependencies and plan-health flags.
    /// </summary>
    /// <param name="from">First month (any day in it). Default: this month.</param>
    /// <param name="to">Last month. Default: December next year.</param>
    /// <param name="project">Project keys; all when omitted.</param>
    [HttpGet("plan", Name = "getPlan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PlanResponse>> Get(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string[]? project, CancellationToken ct) =>
        this.ToResult(await plan.GetPlanAsync(from, to, project, ct));

    /// <summary>A project's headcount steps ("3 from Jan, 5 from Jul").</summary>
    [HttpGet("projects/{key}/capacity", Name = "getProjectCapacity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<CapacityEntry>>> GetCapacity(string key, CancellationToken ct) =>
        RouteResult(await plan.GetCapacityAsync(key, ct));

    /// <summary>Replace a project's headcount steps.</summary>
    [HttpPut("projects/{key}/capacity", Name = "setProjectCapacity")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<CapacityEntry>>> SetCapacity(string key, SetCapacityRequest request, CancellationToken ct) =>
        RouteResult(await plan.SetCapacityAsync(key, request, ct));

    /// <summary>The project is part of the route here, so a missing one is a 404 rather than a bad request.</summary>
    private ActionResult<T> RouteResult<T>(IssueResult<T> result) =>
        result.Error is { Code: "project_not_found" } error
            ? this.Problem(StatusCodes.Status404NotFound, error.Code, error.Detail)
            : this.ToResult(result);

    /// <summary>List milestones.</summary>
    [HttpGet("milestones", Name = "listMilestones")]
    public Task<IReadOnlyList<MilestoneResponse>> ListMilestones(CancellationToken ct) => plan.ListMilestonesAsync(ct);

    /// <summary>Add a milestone, workspace-wide or for one project.</summary>
    [HttpPost("milestones", Name = "createMilestone")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<MilestoneResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MilestoneResponse>> CreateMilestone(CreateMilestoneRequest request, CancellationToken ct) =>
        this.ToResult(await plan.CreateMilestoneAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Rename or move a milestone.</summary>
    [HttpPatch("milestones/{id:guid}", Name = "updateMilestone")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MilestoneResponse>> UpdateMilestone(Guid id, UpdateMilestoneRequest request, CancellationToken ct) =>
        this.ToResult(await plan.UpdateMilestoneAsync(id, request, ct));

    /// <summary>Delete a milestone.</summary>
    [HttpDelete("milestones/{id:guid}", Name = "deleteMilestone")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMilestone(Guid id, CancellationToken ct) =>
        await plan.DeleteMilestoneAsync(id, ct)
            ? NoContent()
            : this.Problem(StatusCodes.Status404NotFound, "milestone_not_found");
}
