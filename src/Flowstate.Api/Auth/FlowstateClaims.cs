using System.Security.Claims;

namespace Flowstate.Api.Auth;

/// <summary>Claims the API adds after authentication, whatever the credential was.</summary>
public static class FlowstateClaims
{
    /// <summary>Flowstate user id (not the Keycloak subject).</summary>
    public const string UserId = "fs:uid";

    /// <summary>Set when the caller is an agent token; the agent acts on behalf of <see cref="UserId"/>.</summary>
    public const string AgentName = "fs:agent";

    /// <summary>Set when the caller authenticated with an API token.</summary>
    public const string TokenId = "fs:tid";

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(UserId)
            ?? throw new InvalidOperationException("Authenticated principal has no Flowstate user id."));

    public static string? GetAgentName(this ClaimsPrincipal principal) => principal.FindFirstValue(AgentName);
}
