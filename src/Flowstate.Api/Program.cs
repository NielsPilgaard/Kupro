using System.Text.Json;
using System.Text.Json.Serialization;
using Flowstate.Api.Auth;
using Flowstate.Api.Common;
using Flowstate.Api.Data;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFlowstateDatabase();
builder.AddFlowstateAuth();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddControllers(options => options.Filters.Add(new ProducesAttribute("application/json")))
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

app.MapOpenApi().AllowAnonymous();
app.MapControllers();
app.MapDefaultEndpoints();

await app.RunAsync();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.NumberHandling = JsonNumberHandling.Strict;
}

public partial class Program;
