using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Exports;

/// <summary>Export a whole workspace as a ZIP (JSON, CSV and attachment files). Owners only.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/exports")]
public sealed class ExportsController(ExportService exports) : ControllerBase
{
    private const string NoMcp = "The result is a ZIP file to save, not something to reason over; agents use `kupro workspace export -o`.";

    /// <summary>Start an export. It builds in the background; poll it until <c>state</c> is <c>ready</c>. One runs at a time.</summary>
    [HttpPost(Name = "createExport")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [NoAgentSurface(AgentSurface.Mcp, NoMcp)]
    [ProducesResponseType<ExportResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ExportResponse>> Create(CancellationToken ct) =>
        this.ToResult(await exports.StartAsync(ct), StatusCodes.Status202Accepted);

    /// <summary>Recent exports, newest first. Files are deleted 7 days after they were started.</summary>
    [HttpGet(Name = "listExports")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [NoAgentSurface(AgentSurface.Mcp, NoMcp)]
    [ProducesResponseType<IReadOnlyList<ExportResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<ExportResponse>>> List(CancellationToken ct) =>
        this.ToResult(await exports.ListAsync(ct));

    /// <summary>An export's state, with a download link valid for 15 minutes once it is ready.</summary>
    [HttpGet("{id:guid}", Name = "getExport")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [NoAgentSurface(AgentSurface.Mcp, NoMcp)]
    [ProducesResponseType<ExportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExportResponse>> Get(Guid id, CancellationToken ct) =>
        this.ToResult(await exports.GetAsync(id, ct));
}
