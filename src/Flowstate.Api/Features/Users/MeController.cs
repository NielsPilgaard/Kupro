using Flowstate.Api.Auth;
using Flowstate.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Users;

/// <summary>The authenticated caller's own profile.</summary>
[ApiController]
[Route("api/v1/me")]
public sealed class MeController(UserService users) : ControllerBase
{
    /// <summary>Get the current user.</summary>
    /// <remarks>For an agent token, <c>agent</c> names the agent acting on behalf of this user.</remarks>
    [HttpGet(Name = "getMe")]
    public async Task<ActionResult<MeResponse>> Get(CancellationToken ct) =>
        await users.GetMeAsync(User.GetUserId(), User.GetAgentName(), ct) is { } me
            ? me
            : this.Problem(StatusCodes.Status404NotFound, "user_not_found");

    /// <summary>Update the current user's profile (display name, language, time zone).</summary>
    [HttpPatch(Name = "updateMe")]
    [NoAgentSurface(AgentSurface.Mcp, "Profile settings (language, time zone) are personal.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MeResponse>> Update(UpdateMeRequest request, CancellationToken ct) =>
        await users.UpdateMeAsync(User.GetUserId(), User.GetAgentName(), request, ct) is { } me
            ? me
            : this.Problem(StatusCodes.Status400BadRequest, "invalid_time_zone");
}
