using Kupro.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Time;

/// <summary>Every 6 hours: deletes time entries and their history past the 5-year retention (ADR-021 Retention).</summary>
public sealed partial class TimeRetentionJob(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<TimeRetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First run shortly after startup, once migrations are done, so frequent deploys can't keep skipping it. The
        // cutoff moves by a day at a time, so a run every few hours keeps it at most hours late.
        await Task.Delay(TimeSpan.FromMinutes(10), clock, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>()
                    .CreateDbContextAsync(stoppingToken);
                var deleted = await TimeService.PurgeExpiredAsync(db, TimeService.LocalDay(clock.GetUtcNow()), stoppingToken);
                if (deleted > 0)
                {
                    LogPurged(logger, deleted, TimeLimits.RetentionYears);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} time entries older than {Years} years")]
    private static partial void LogPurged(ILogger logger, int count, int years);

    [LoggerMessage(Level = LogLevel.Error, Message = "Time retention run failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
