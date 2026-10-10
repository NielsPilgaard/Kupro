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

    /// <summary>Set when the caller's API token is scoped to one workspace.</summary>
    public const string WorkspaceId = "fs:ws";

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(UserId)
            ?? throw new InvalidOperationException("Authenticated principal has no Flowstate user id."));

    public static string? GetAgentName(this ClaimsPrincipal principal) => principal.FindFirstValue(AgentName);

    public static Guid? GetTokenWorkspaceId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(WorkspaceId) is { } id ? Guid.Parse(id) : null;
}
