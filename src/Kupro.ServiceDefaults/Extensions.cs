using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Shared Aspire defaults: OpenTelemetry, health checks, service discovery and HTTP resilience (ADR-012).
/// </summary>
public static class Extensions
{
    public const string HealthEndpointPath = "/health";
    public const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// <c>service.namespace</c> on every signal. Grafana dashboards and alerts select on it (ADR-014), and it keeps
    /// Kupro apart from other products in a shared Grafana stack.
    /// </summary>
    public const string ServiceNamespace = "kupro";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            // service.name comes from OTEL_SERVICE_NAME, which Aspire sets to the resource name (api, migrations).
            .ConfigureResource(resource => resource.AddAttributes(
            [
                new("service.namespace", ServiceNamespace),
                new("deployment.environment.name", builder.Environment.EnvironmentName.ToLowerInvariant()),
                // Deploy markers and traces name the build. CI stamps the commit: -p:SourceRevisionId=<sha>
                // -p:IncludeSourceRevisionInInformationalVersion=true → 1.0.0+<sha> (Directory.Build.props turns it off locally).
                new("service.version", Assembly.GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"),
            ]))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(builder.Environment.ApplicationName)
                // Active DbContexts, query/save rates: shows pool exhaustion before latency does.
                .AddMeter("Microsoft.EntityFrameworkCore"))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);
                    options.RecordException = true;
                })
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Exports buffered logs, traces and metrics now. Short-lived runs (the migrations service) must call this before
    /// exiting: the OTLP exporter creates its HTTP client on first export, which fails once the host is disposed, so
    /// whatever was still buffered at exit would be dropped.
    /// </summary>
    public static void FlushTelemetry(this IServiceProvider services, int timeoutMilliseconds = 5000)
    {
        services.GetService<TracerProvider>()?.ForceFlush(timeoutMilliseconds);
        services.GetService<MeterProvider>()?.ForceFlush(timeoutMilliseconds);
        services.GetService<LoggerProvider>()?.ForceFlush(timeoutMilliseconds);
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (readiness, all checks) and <c>/alive</c> (liveness). Mapped in every environment because the
    /// compose healthcheck and Dokploy use them; the default writer only returns the aggregate status, no details.
    /// Probes are not request-logged: they arrive every few seconds and would dominate log volume.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath).AllowAnonymous().WithHttpLogging(HttpLoggingFields.None);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
        }).AllowAnonymous().WithHttpLogging(HttpLoggingFields.None);

        return app;
    }
}
