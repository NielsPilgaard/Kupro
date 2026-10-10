using Flowstate.Api.Common;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Projects;

/// <summary>Projects in a workspace. Addressed by key (<c>FLOW</c>).</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/projects")]
public sealed class ProjectsController(ProjectService projects) : ControllerBase
{
    /// <summary>List projects.</summary>
    [HttpGet(Name = "listProjects")]
    public Task<IReadOnlyList<ProjectResponse>> List(CancellationToken ct) => projects.ListAsync(ct);

    /// <summary>Get a project by key.</summary>
    [HttpGet("{key}", Name = "getProject")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectResponse>> Get(string key, CancellationToken ct) =>
        await projects.GetAsync(key, ct) is { } project
            ? project
            : this.Problem(StatusCodes.Status404NotFound, "project_not_found");

    /// <summary>Create a project.</summary>
    [HttpPost(Name = "createProject")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<ProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectResponse>> Create(CreateProjectRequest request, CancellationToken ct) =>
        ToResult(await projects.CreateAsync(request, ct), created: true);

    /// <summary>Update a project. The key can only change while the project has no issues.</summary>
    [HttpPatch("{key}", Name = "updateProject")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectResponse>> Update(string key, UpdateProjectRequest request, CancellationToken ct) =>
        ToResult(await projects.UpdateAsync(key, request, ct), created: false);

    private ActionResult<ProjectResponse> ToResult((ProjectChangeResult Result, ProjectResponse? Project) outcome, bool created) =>
        outcome.Result switch
        {
            ProjectChangeResult.Ok when created => StatusCode(StatusCodes.Status201Created, outcome.Project),
            ProjectChangeResult.Ok => outcome.Project!,
            ProjectChangeResult.KeyTaken => this.Problem(StatusCodes.Status409Conflict, "project_key_taken"),
            ProjectChangeResult.KeyLocked => this.Problem(StatusCodes.Status409Conflict, "project_key_locked",
                "The key can't change once the project has issues."),
            _ => this.Problem(StatusCodes.Status404NotFound, "project_not_found"),
        };
}
