using System.Text.Json;
using System.Text.Json.Serialization;
using Kupro.Api.Auth;
using Kupro.Api.Common;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Exports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Mcp;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKuproDatabase();
builder.AddKuproAuth();
builder.AddKuproMcp();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<LabelService>();
builder.Services.AddScoped<ActivityLog>();
builder.Services.AddScoped<IssueService>();
builder.Services.AddScoped<SprintService>();
builder.Services.AddScoped<PlanService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<ActivityFeed>();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddSingleton<ObjectStorage>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddSingleton<ExportSignal>();
builder.Services.AddHostedService<ExportJob>();
builder.Services.AddScoped<RealtimeEvents>();
builder.Services.AddScoped<MeetingService>();
builder.Services.AddHostedService<MeetingRetentionJob>();
// Meeting agent vendors (ADR-015): OpenAI-compatible endpoints routed per language; without an extraction model the
// offline keyword extractor proposes clear commands.
builder.Services.Configure<MeetingAgentOptions>(builder.Configuration.GetSection(MeetingAgentOptions.Section));
builder.Services.AddHttpClient<ISpeechToText, OpenAiSpeechToText>();
if (builder.Configuration.GetSection(MeetingAgentOptions.Section).Get<MeetingAgentOptions>()?.Extraction.IsConfigured == true)
{
    builder.Services.AddHttpClient<IProposalExtractor, OpenAiProposalExtractor>();
}
else
{
    builder.Services.AddSingleton<IProposalExtractor, KeywordProposalExtractor>();
}
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services
    .AddControllers(options =>
    {
        options.Filters.Add(new ProducesAttribute("application/json"));
        options.Filters.Add<WorkspaceAccessFilter>();
    })
    .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = Problems.ValidationProblem);
// OpenAPI generation reads the minimal-API JSON options, so both must agree.
builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddKuproRequestLogging();
builder.Services.AddKuproOpenApi();

var app = builder.Build();

if (args.Contains(DatabaseExtensions.MigrateCommand))
{
    try
    {
        await app.Services.MigrateDatabaseAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "Database migration failed");
        throw;
    }
    finally
    {
        // A one-shot run exits before the exporters' first batch; without this its logs never reach Grafana (ADR-014).
        app.Services.FlushTelemetry();
    }

    return;
}

// Outermost, so the logged status and duration include exception handling.
app.UseHttpLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi().AllowAnonymous();
app.MapControllers();
app.MapKuproMcp();
app.MapHub<WorkspaceHub>(WorkspaceHub.Path);
app.MapDefaultEndpoints();

await app.RunAsync();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.NumberHandling = JsonNumberHandling.Strict;
}

public partial class Program;
