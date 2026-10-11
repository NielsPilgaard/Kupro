using System.Security.Claims;
using Flowstate.Api.Auth;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpLogging;

namespace Flowstate.Api.Common;

/// <summary>
/// One log line per request (method, path, status, duration, route template, caller kind). Grafana dashboards and
/// alerts aggregate these lines in Loki (ADR-014), so changing a field name here breaks panels in
/// <c>infrastructure/observability/</c>.
/// </summary>
public static class RequestLogging
{
    /// <summary>Route template, e.g. <c>/api/v1/tokens/{id}</c>; ids never end up in log labels.</summary>
    public const string RouteField = "Route";

    /// <summary>Who called: see <see cref="CallerKind"/>. Splits agent traffic from human traffic (ADR-001).</summary>
    public const string CallerField = "Caller";

    /// <summary>
    /// Logged instead of a route when no endpoint matched: scanners probing /wp-login.php and friends. They get 401
    /// rather than 404 because the fallback policy requires authentication, so filter on this, not on the status.
    /// </summary>
    public const string UnmatchedRoute = "(unmatched)";

    public static IServiceCollection AddFlowstateRequestLogging(this IServiceCollection services)
    {
        // No query string, headers or bodies: they carry tokens and user content, and these logs leave the server.
        services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestMethod
                | HttpLoggingFields.RequestPath
                | HttpLoggingFields.ResponseStatusCode
                | HttpLoggingFields.Duration;
            options.CombineLogs = true;
        });
        services.AddHttpLoggingInterceptor<RequestTelemetryInterceptor>();

        return services;
    }

    private sealed class RequestTelemetryInterceptor : IHttpLoggingInterceptor
    {
        public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext) => ValueTask.CompletedTask;

        public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext)
        {
            // Endpoints that opted out (health probes) must stay silent; any parameter would produce a line.
            if (logContext.LoggingFields == HttpLoggingFields.None)
            {
                return ValueTask.CompletedTask;
            }

            var context = logContext.HttpContext;
            var route = Route(context);
            var caller = CallerKind.Of(context.User);

            logContext.AddParameter(RouteField, route);
            logContext.AddParameter(CallerField, caller);

            // Same dimension on the server span, so slow traces can be filtered by caller in Tempo.
            context.Features.Get<IHttpActivityFeature>()?.Activity.SetTag("flowstate.caller", caller);

            return ValueTask.CompletedTask;
        }

        private static string Route(HttpContext context)
        {
            // The exception handler clears the endpoint before re-executing; it keeps the original on its feature.
            var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
            var template = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
            return template is null ? UnmatchedRoute : "/" + template.TrimStart('/');
        }
    }
}

/// <summary>Coarse caller classification for telemetry. Never the user id or token name.</summary>
public static class CallerKind
{
    public const string Anonymous = "anonymous";

    /// <summary>Keycloak session: the web app, or an MCP client signed in through OAuth.</summary>
    public const string Session = "session";

    /// <summary>Personal API token: CLI and scripts.</summary>
    public const string Token = "token";

    /// <summary>Agent token (ADR-005).</summary>
    public const string Agent = "agent";

    public static string Of(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true ? Anonymous
        : user.HasClaim(c => c.Type == FlowstateClaims.AgentName) ? Agent
        : user.HasClaim(c => c.Type == FlowstateClaims.TokenId) ? Token
        : Session;
}
