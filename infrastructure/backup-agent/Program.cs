using Kupro.BackupAgent;
using Kupro.BackupAgent.Pages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using OpenTelemetry;
using OpenTelemetry.Resources;

// Runs only in its Linux container next to Postgres.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// The backup agent (ADR-016): streams WAL, runs pgBackRest backups, drills and restores, writes
// status to the ops bucket, and serves the break-glass console on :9090. Compose publishes that port
// on 127.0.0.1 only and keeps the agent off the app network, so the console is reached over an SSH
// tunnel; the access key in the agent's volume (ConsoleAccess) covers Docker setups that still route
// between networks. It works without Keycloak, the API or the app database.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AgentOptions>().BindConfiguration(AgentOptions.SectionName);
builder.Services.AddOptions<OpsBucketOptions>().BindConfiguration(OpsBucketOptions.SectionName);
builder.Services.AddOptions<HeartbeatOptions>().BindConfiguration(HeartbeatOptions.SectionName);
builder.Services.AddHttpClient();

builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<WalState>();
builder.Services.AddSingleton<DataVolumes>();
builder.Services.AddSingleton<LivePostgres>();
builder.Services.AddSingleton<PgBackRest>();
builder.Services.AddSingleton<RepoInfoCache>();
builder.Services.AddSingleton<OpsBucket>();
builder.Services.AddSingleton<Heartbeats>();
builder.Services.AddSingleton<ElmahIoHeartbeatSender>();
builder.Services.AddSingleton<PingHeartbeatSender>();
builder.Services.AddSingleton<IHeartbeatSender>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HeartbeatOptions>>().Value.Provider == HeartbeatProvider.Ping
        ? sp.GetRequiredService<PingHeartbeatSender>()
        : sp.GetRequiredService<ElmahIoHeartbeatSender>());
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<DrillChecks>();
builder.Services.AddSingleton<RestoreService>();
builder.Services.AddSingleton<StatusBuilder>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddSingleton<ConsoleAccess>();
builder.Services.AddSingleton<AgentMetrics>();
builder.Services.AddHostedService<WalStreamer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());

// Metrics and logs to Alloy (ADR-014) when compose or the AppHost sets the endpoint. service.name comes from
// OTEL_SERVICE_NAME; the namespace matches the API's, so Grafana's dashboards and error alert see the agent too.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddAttributes([new("service.namespace", "kupro")]))
        .WithMetrics(metrics => metrics.AddMeter(AgentMetrics.MeterName))
        .WithLogging()
        .UseOtlpExporter();
}

// Antiforgery tokens on every form. The console has no login (the SSH key is the identity), so this
// is what stops another site in the operator's browser from posting to the tunnel.
var stateDirectory = builder.Configuration[$"{AgentOptions.SectionName}:{nameof(AgentOptions.StateDirectory)}"] ?? new AgentOptions().StateDirectory;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(stateDirectory, "keys")));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "backup-console-af";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.None; // Plain http through the SSH tunnel.
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    // Exactly what antiforgery sets, or it logs a warning on every form page.
    headers.CacheControl = "no-cache, no-store";
    headers.Pragma = "no-cache";
    await next();
});
app.Services.GetRequiredService<ConsoleAccess>(); // Writes console-link at startup, before anyone asks.
app.Services.GetRequiredService<AgentMetrics>(); // Registers the gauges.
ConsoleAccess.Map(app);

// UseAntiforgery only records the result, and minimal APIs reject a bad token while binding form
// fields, so a handler that binds none would run anyway. Check every POST here instead; all of them
// come from Html.Form with a token. This has to run first: once UseAntiforgery has marked a token
// invalid, reading the form throws.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        !await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    await next();
});
app.UseAntiforgery();

app.MapGet("/console.css", () => Results.Text(Html.Css, "text/css; charset=utf-8"));
app.MapGet("/console.js", () => Results.Text(Html.Script, "text/javascript; charset=utf-8"));
app.MapGet("/favicon.svg", () => Results.Text(Html.Favicon, "image/svg+xml"));
OverviewPages.Map(app);
BackupPages.Map(app);
RestorePages.Map(app);

app.Run();
