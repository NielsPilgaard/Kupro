using System.Security.Claims;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Users;
using Kupro.Api.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Kupro.Api.Auth;

public static class AuthExtensions
{
    /// <summary>Keycloak realm (ADR-005) and the audience its access tokens carry for this API.</summary>
    public const string Realm = "kupro";
    public const string Audience = "kupro-api";

    private const string SelectorScheme = "Kupro";

    public static class Policies
    {
        /// <summary>A human session or personal token, not an agent token.</summary>
        public const string Human = "human";
    }

    public static IHostApplicationBuilder AddKuproAuth(this IHostApplicationBuilder builder)
    {
        builder.Services.AddMemoryCache();

        builder.Services
            .AddAuthentication(SelectorScheme)
            // One Bearer header, two credential types: kp_ API tokens; everything else is a Keycloak JWT.
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
            {
                options.ForwardDefaultSelector = context => GetApiToken(context.Request) is not null
                    ? ApiTokenAuthenticationHandler.SchemeName
                    : JwtBearerDefaults.AuthenticationScheme;
                // 401s point MCP clients (and anyone else) at the Protected Resource Metadata (RFC 9728).
                options.ForwardChallenge = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddMcp(options => McpSetup.ConfigureResourceMetadata(options, builder.Configuration))
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationHandler.SchemeName, null)
            .AddKeycloakJwtBearer("keycloak", Realm, options =>
            {
                // kupro-api: our own clients (audience mapper). The MCP resource URL: tokens Keycloak issues with
                // a resource indicator (RFC 8707) to MCP clients (ADR-005 IMP-001).
                options.TokenValidationParameters.ValidAudiences = builder.Configuration["Api:PublicUrl"] is { } publicUrl
                    ? [Audience, publicUrl.TrimEnd('/') + McpSetup.Path]
                    : [Audience];
                options.MapInboundClaims = false;
                // Inside the compose network Keycloak is plain HTTP behind the TLS-terminating proxy.
                options.RequireHttpsMetadata = false;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ProvisionUserAsync,
                    OnMessageReceived = context =>
                    {
                        context.Token ??= GetRealtimeQueryToken(context.Request);
                        return Task.CompletedTask;
                    },
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Human, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.HasClaim(c => c.Type == KuproClaims.AgentName)));

        builder.Services.AddScoped<UserService>();
        builder.Services.AddScoped<ApiTokenService>();

        return builder;
    }

    /// <summary>A Kupro API token from the Bearer header, or from the realtime hub's query string.</summary>
    internal static string? GetApiToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return header.AsSpan(bearer.Length).StartsWith(ApiTokenService.CommonPrefix, StringComparison.Ordinal)
                ? header[bearer.Length..].Trim()
                : null;
        }

        return GetRealtimeQueryToken(request) is { } token && token.StartsWith(ApiTokenService.CommonPrefix, StringComparison.Ordinal)
            ? token
            : null;
    }

    /// <summary>
    /// Browsers can't set headers on a WebSocket, so the SignalR client sends the token as <c>access_token</c>.
    /// Accepted on the hub path only; request logging records paths, not query strings.
    /// </summary>
    private static string? GetRealtimeQueryToken(HttpRequest request) =>
        request.Path.StartsWithSegments(WorkspaceHub.Path) && request.Query["access_token"].ToString() is { Length: > 0 } token
            ? token
            : null;

    private static string? PreferredLanguage(HttpRequest request) =>
        request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(l => l.Quality ?? 1)
            .Select(l => l.Value.Value?.Split('-')[0].ToLowerInvariant())
            .FirstOrDefault(l => l is not null && UserService.SupportedLocales.Contains(l));

    /// <summary>First login creates the Kupro user; every JWT gets our user id claim.</summary>
    private static async Task ProvisionUserAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var subject = principal.FindFirstValue("sub");
        if (subject is null)
        {
            context.Fail("Token has no subject.");
            return;
        }

        var username = principal.FindFirstValue("preferred_username") ?? subject;
        var identity = new ExternalIdentity(
            subject,
            principal.FindFirstValue("email") ?? username,
            string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase),
            principal.FindFirstValue("name") ?? username,
            // Keycloak only sends a locale once the user picked one; otherwise the browser's language decides.
            principal.FindFirstValue("locale") ?? PreferredLanguage(context.HttpContext.Request));

        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        Guid userId;
        try
        {
            userId = await users.EnsureUserAsync(identity, context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The browser left (e.g. navigated during sign-in); without this JwtBearer logs an error and fires the errors alert.
            context.NoResult();
            return;
        }

        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(KuproClaims.UserId, userId.ToString()));
    }
}
