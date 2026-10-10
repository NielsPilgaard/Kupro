using System.Security.Claims;
using System.Text.Encodings.Web;
using Flowstate.Api.Features.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Flowstate.Api.Auth;

/// <summary>Authenticates <c>Authorization: Bearer fs_...</c> API tokens (ADR-005).</summary>
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
        var secret = AuthExtensions.GetApiTokenFromHeader(Request);
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
            new(FlowstateClaims.UserId, token.UserId.ToString()),
            new(FlowstateClaims.TokenId, token.TokenId.ToString()),
        ];
        if (token.AgentName is not null)
        {
            claims.Add(new(FlowstateClaims.AgentName, token.AgentName));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
