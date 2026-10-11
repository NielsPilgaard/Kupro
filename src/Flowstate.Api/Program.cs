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
using Flowstate.Api.Features.Plan;
using Flowstate.Api.Features.Projects;
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
builder.Services.AddFlowstateOpenApi();

var app = builder.Build();

if (args.Contains(DatabaseExtensions.MigrateCommand))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi().AllowAnonymous();
app.MapControllers();
app.MapFlowstateMcp();
app.MapDefaultEndpoints();

await app.RunAsync();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.NumberHandling = JsonNumberHandling.Strict;
}

public partial class Program;
