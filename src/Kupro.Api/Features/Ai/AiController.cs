using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Ai;

/// <summary>
/// A workspace's own AI connections and routes (ADR-022, task 035). Members read status; people with billing access change
/// it, since a key moves the cost to the workspace's own vendor account. Agents never manage keys: MCP reads status only.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/ai")]
public sealed class AiController(AiConnectionService ai) : ControllerBase
{
    private const string SecretsStayOffMcp = "Keys must not pass through a model's context; agents read status with the meeting tool's ai action.";

    /// <summary>The provider types a workspace can connect, with Kupro's tested default models.</summary>
    [HttpGet("providers", Name = "listAiProviders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IReadOnlyList<AiProviderResponse> Providers() => AiConnectionService.Providers();

    /// <summary>Connections (never their keys), what runs where, and this month's usage by who paid.</summary>
    [HttpGet(Name = "getAiStatus")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<AiStatusResponse> Status(CancellationToken ct) => ai.StatusAsync(ct);

    /// <summary>Connect a provider with the workspace's own key. The key is checked with the vendor before anything uses it.</summary>
    [HttpPost("connections", Name = "addAiConnection")]
    [BillingAccess]
    [NoAgentSurface(AgentSurface.Mcp, SecretsStayOffMcp)]
    [ProducesResponseType<AiConnectionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AiConnectionResponse>> Add(AddAiConnectionRequest request, CancellationToken ct) =>
        this.ToResult(await ai.AddAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Replace a connection's key. It is checked with the vendor; a key that works turns a rejected connection back on.</summary>
    [HttpPut("connections/{id:guid}/key", Name = "setAiConnectionKey")]
    [BillingAccess]
    [NoAgentSurface(AgentSurface.Mcp, SecretsStayOffMcp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AiConnectionResponse>> SetKey(Guid id, SetAiConnectionKeyRequest request, CancellationToken ct) =>
        this.ToResult(await ai.SetKeyAsync(id, request, ct));

    /// <summary>Check a connection's key with the vendor again.</summary>
    [HttpPost("connections/{id:guid}/check", Name = "checkAiConnection")]
    [BillingAccess]
    [NoAgentSurface(AgentSurface.Mcp, SecretsStayOffMcp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AiConnectionResponse>> Check(Guid id, CancellationToken ct) =>
        this.ToResult(await ai.RecheckAsync(id, ct));

    /// <summary>Remove a connection and its routes. Those capabilities go back to Kupro's included hours.</summary>
    [HttpDelete("connections/{id:guid}", Name = "removeAiConnection")]
    [BillingAccess]
    [NoAgentSurface(AgentSurface.Mcp, SecretsStayOffMcp)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) =>
        await ai.RemoveAsync(id, ct) ? NoContent() : this.Problem(StatusCodes.Status404NotFound, "ai_connection_not_found");

    /// <summary>Route a capability, for every language or one, to a connection.</summary>
    [HttpPut("routes", Name = "setAiRoute")]
    [BillingAccess]
    [NoAgentSurface(AgentSurface.Mcp, SecretsStayOffMcp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AiRouteResponse>> SetRoute(SetAiRouteRequest request, CancellationToken ct) =>
        this.ToResult(await ai.SetRouteAsync(request, ct));

    /// <summary>Remove a route: the capability goes back to the every-language route, or to Kupro's.</summary>
    [HttpDelete("routes", Name = "removeAiRoute")]
    [BillingAccess]
    [NoAgentSurface(AgentSurface.Mcp, SecretsStayOffMcp)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRoute([FromQuery] AiCapability capability, [FromQuery] string? language, CancellationToken ct) =>
        await ai.RemoveRouteAsync(capability, language, ct) ? NoContent() : this.Problem(StatusCodes.Status404NotFound, "ai_route_not_found");
}
