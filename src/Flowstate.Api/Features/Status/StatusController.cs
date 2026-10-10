using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Status;

/// <summary>Service identity and version. Unauthenticated; used by the SPA and CLI to check connectivity.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/status")]
public sealed class StatusController : ControllerBase
{
    private static readonly string Version =
        typeof(StatusController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>Get API status.</summary>
    [HttpGet(Name = "getStatus")]
    public ActionResult<StatusResponse> Get() => new StatusResponse("flowstate", Version);
}

/// <param name="Service">Service name, always <c>flowstate</c>.</param>
/// <param name="Version">API build version.</param>
public sealed record StatusResponse(string Service, string Version);
