using Flowstate.Api.Auth;
using Flowstate.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Tokens;

/// <summary>The caller's API tokens. Agent tokens cannot manage tokens.</summary>
[ApiController]
[Route("api/v1/tokens")]
[Authorize(Policy = AuthExtensions.Policies.Human)]
public sealed class TokensController(ApiTokenService tokens) : ControllerBase
{
    /// <summary>List your active API tokens.</summary>
    [HttpGet(Name = "listApiTokens")]
    [NoAgentSurface(AgentSurface.Mcp, "Credential management stays with people; agents can't manage tokens.")]
    public async Task<IReadOnlyList<ApiTokenResponse>> List(CancellationToken ct) =>
        await tokens.ListAsync(User.GetUserId(), ct);

    /// <summary>Create an API token. The secret is returned once.</summary>
    [HttpPost(Name = "createApiToken")]
    [NoAgentSurface(AgentSurface.Mcp, "Credential management stays with people; agents can't manage tokens.")]
    [ProducesResponseType<CreatedApiTokenResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CreatedApiTokenResponse>> Create(CreateApiTokenRequest request, CancellationToken ct)
    {
        var (created, error) = await tokens.CreateAsync(User.GetUserId(), request, ct);
        return created is not null
            ? StatusCode(StatusCodes.Status201Created, created)
            : this.Problem(StatusCodes.Status403Forbidden, "workspace_not_allowed",
                $"Cannot create this token in that workspace ({error}).");
    }

    /// <summary>Revoke an API token. Takes effect immediately.</summary>
    [HttpDelete("{id:guid}", Name = "revokeApiToken")]
    [NoAgentSurface(AgentSurface.Mcp, "Credential management stays with people; agents can't manage tokens.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await tokens.RevokeAsync(User.GetUserId(), id, ct)
            ? NoContent()
            : this.Problem(StatusCodes.Status404NotFound, "token_not_found");
}
