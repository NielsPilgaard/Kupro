using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowstate.Api.Data;

public static class DatabaseExtensions
{
    /// <summary>Connection string name; matches the Aspire database resource in the AppHost.</summary>
    public const string ConnectionName = "flowstate";

    /// <summary>Command-line argument that runs migrations and exits instead of serving HTTP (ADR-012 IMP-003).</summary>
    public const string MigrateCommand = "migrate";

    public static IHostApplicationBuilder AddFlowstateDatabase(this IHostApplicationBuilder builder)
    {
        // Scoped (not pooled) factory: contexts take the request's WorkspaceContext for the tenant filter.
        builder.Services.AddScoped<WorkspaceContext>();
        builder.Services.AddDbContextFactory<FlowstateDbContext>(
            options => options
                .UseNpgsql(builder.Configuration.GetConnectionString(ConnectionName))
                .UseSnakeCaseNamingConvention(),
            ServiceLifetime.Scoped);

        // Adds the Npgsql health check, retries and OpenTelemetry on top of our own registration.
        builder.EnrichNpgsqlDbContext<FlowstateDbContext>();

        return builder;
    }

    /// <summary>
    /// Applies pending EF migrations, creating the database if needed. Retries while Postgres is still starting,
    /// because compose only orders container start, not readiness.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseExtensions));
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowstateDbContext>>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await db.Database.MigrateAsync(ct);
                logger.LogInformation("Database migrated");
                return;
            }
            catch (NpgsqlException ex) when (attempt < 30 && ex.IsTransient)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
