using Flowstate.Api.Data;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFlowstateDatabase();

builder.Services.AddControllers(options => options.Filters.Add(new ProducesAttribute("application/json")));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Flowstate API";
    document.Info.Version = "v1";
    return Task.CompletedTask;
}));

var app = builder.Build();

if (args.Contains(DatabaseExtensions.MigrateCommand))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapControllers();
app.MapDefaultEndpoints();

await app.RunAsync();

public partial class Program;
