using System.Reflection;
using Kupro.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Status;

/// <summary>Service identity, version and sign-in details. Unauthenticated; the SPA and CLI start here.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/status")]
public sealed class StatusController(IConfiguration configuration) : ControllerBase
{
    /// <summary>Keycloak client used by the CLI's device login (ADR-005).</summary>
    public const string CliClientId = "cli";

    private static readonly string Version =
        typeof(StatusController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>Get API status.</summary>
    [HttpGet(Name = "getStatus")]
    [NoAgentSurface(AgentSurface.Mcp, "Connectivity check; MCP clients get server info from initialize.")]
    public ActionResult<StatusResponse> Get() =>
        new StatusResponse("kupro", Version, configuration["Auth:PublicAuthority"], CliClientId);
}

/// <param name="Service">Service name, always <c>kupro</c>.</param>
/// <param name="Version">API build version.</param>
/// <param name="Authority">OpenID Connect issuer users sign in with (Keycloak realm URL); null if not configured.</param>
/// <param name="CliClientId">OAuth client id for the CLI's device authorization grant.</param>
public sealed record StatusResponse(string Service, string Version, string? Authority, string CliClientId);
