using System.Text.Json;
using System.Text.Json.Serialization;
using Flowstate.Api.Auth;
using Flowstate.Api.Common;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Attachments;
using Flowstate.Api.Features.Comments;
using Flowstate.Api.Features.Email;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Meetings;
using Flowstate.Api.Features.Plan;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Realtime;
using Flowstate.Api.Features.Sprints;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Mcp;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFlowstateDatabase();
builder.AddFlowstateAuth();
builder.AddFlowstateMcp();

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
builder.Services.AddFlowstateRequestLogging();
builder.Services.AddFlowstateOpenApi();

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
app.MapFlowstateMcp();
app.MapHub<WorkspaceHub>(WorkspaceHub.Path);
app.MapDefaultEndpoints();

await app.RunAsync();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.NumberHandling = JsonNumberHandling.Strict;
}

public partial class Program;
