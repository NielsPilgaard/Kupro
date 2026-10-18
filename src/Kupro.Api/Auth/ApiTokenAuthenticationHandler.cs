using System.Security.Claims;
using System.Text.Encodings.Web;
using Kupro.Api.Features.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Auth;

/// <summary>Authenticates <c>Authorization: Bearer kp_...</c> API tokens (ADR-005).</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = AuthExtensions.GetApiToken(Request);
        if (secret is null)
        {
            return AuthenticateResult.NoResult();
        }

        var token = await tokens.AuthenticateAsync(secret, Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("Invalid, expired or revoked API token.");
        }

        List<Claim> claims =
        [
            new(KuproClaims.UserId, token.UserId.ToString()),
            new(KuproClaims.TokenId, token.TokenId.ToString()),
        ];
        if (token.WorkspaceId is { } workspaceId)
        {
            claims.Add(new(KuproClaims.WorkspaceId, workspaceId.ToString()));
        }

        if (token.AgentName is not null)
        {
            claims.Add(new(KuproClaims.AgentName, token.AgentName));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
