using Kupro.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Meetings;

/// <summary>
/// Hourly: ends meetings live past the 3-hour limit (task 017) and deletes transcripts past each workspace's retention
/// (task 015, privacy).
/// </summary>
public sealed partial class MeetingRetentionJob(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<MeetingRetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First run after an hour: at startup the database may still be migrating.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // The context factory is scoped (it carries the request's workspace), so each run gets its own scope.
                await using var scope = scopes.CreateAsyncScope();
                await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>()
                    .CreateDbContextAsync(stoppingToken);
                // Ending first lets the purge below delete those meetings' transcripts in the same run.
                var ended = await MeetingService.EndOverlongMeetingsAsync(db, clock.GetUtcNow(), stoppingToken);
                if (ended > 0)
                {
                    LogEnded(logger, ended, MeetingService.MaxDuration.TotalHours);
                }

                var deleted = await MeetingService.PurgeExpiredTranscriptsAsync(db, clock.GetUtcNow(), stoppingToken);
                if (deleted > 0)
                {
                    LogPurged(logger, deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ended {Count} meetings live longer than {Hours} hours")]
    private static partial void LogEnded(ILogger logger, int count, double hours);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired transcript segments")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcript retention run failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
