using Microsoft.Extensions.Options;

namespace Kupro.BackupAgent;

/// <summary>
/// The agent's clock: status every 5 minutes (status.json, the migration mirror, the agent
/// heartbeat), a full backup at 02:00 and whenever one is requested (new slot, gap, go-live), weekly
/// verify and weekly drill. One heavy job at a time through <see cref="JobRunner"/>.
/// </summary>
public sealed class Scheduler(
    IOptions<AgentOptions> options,
    JobRunner jobs,
    BackupService backups,
    DrillChecks checks,
    StatusBuilder status,
    RepoInfoCache repo,
    WalState wal,
    StateStore state,
    LivePostgres live,
    OpsBucket ops,
    Heartbeats heartbeats,
    ILogger<Scheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly AgentOptions _options = options.Value;
    private DateTimeOffset _nextStatus = DateTimeOffset.MinValue;
    private Task? _running;

    /// <summary>Asks for a status refresh on the next tick, e.g. after a job.</summary>
    public void RefreshSoon() => _nextStatus = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ops.SyncDownAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not sync ops bucket down");
        }

        // Give the WAL loop a moment to find Postgres and the slot before the first status.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= _nextStatus)
                {
                    _nextStatus = DateTimeOffset.UtcNow + _options.StatusInterval;
                    await PublishStatusAsync(stoppingToken);
                }

                StartDueJob();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }

            await Task.Delay(Tick, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        await repo.RefreshAsync(cancellationToken);
        await MirrorMigrationsAsync(cancellationToken);
        var current = await status.BuildAsync(cancellationToken);
        await ops.WriteAsync(OpsBucket.StatusKey, current, cancellationToken);

        // The agent WAL heartbeat carries the overall health, so any Unhealthy issue
        // alerts within minutes, and a dead agent alerts through the missing heartbeat.
        await heartbeats.SendAsync(Heartbeats.Kind.Wal, current.Health,
            current.Issues.Count == 0 ? $"Data secured {Fmt.Ago(current.DataSecuredAt, current.GeneratedAt)}" : string.Join(" | ", current.Issues.Take(3)),
            null, cancellationToken);
    }

    private async Task MirrorMigrationsAsync(CancellationToken cancellationToken)
    {
        List<MigrationRow> rows;
        try
        {
            rows = await live.MigrationsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not read __EFMigrationsHistory: {Message}", ex.Message);
            return;
        }

        var existing = await ops.ReadAsync<List<MigrationRow>>(OpsBucket.MigrationsKey, preferRemote: false, cancellationToken) ?? [];
        var known = existing.Where(m => m.AppliedAt is not null).ToDictionary(m => m.MigrationId, m => m.AppliedAt);
        var merged = rows.Select(r => r with { AppliedAt = r.AppliedAt ?? known.GetValueOrDefault(r.MigrationId) }).ToList();
        if (!merged.SequenceEqual(existing))
        {
            await ops.WriteAsync(OpsBucket.MigrationsKey, merged, cancellationToken);
        }
    }

    private void StartDueJob()
    {
        if (_running is { IsCompleted: false } || jobs.Current is not null)
        {
            return;
        }

        var w = wal.Current;
        if (!w.PostgresReachable || !w.StanzaReady)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var requested = state.Read(s => s.FullBackupRequested);
        if (requested is not null && Attempt("requested-full", now, TimeSpan.FromMinutes(30)))
        {
            // Counts as today's full backup, so the 02:00 one doesn't run right after it.
            Run("full", JobKind.Backup, "Full backup", (job, ct) => backups.BackupAsync(job, "full", requested, ct), "requested-full");
        }
        else if (Due("full", _options.FullBackupHour, null, now))
        {
            Run("full", JobKind.Backup, "Daily full backup", (job, ct) => backups.BackupAsync(job, "full", $"Scheduled at {_options.FullBackupHour:00}:00", ct));
        }
        else if (Due("verify", _options.VerifyHour, _options.VerifyDay, now))
        {
            Run("verify", JobKind.Verify, "Weekly verify", backups.VerifyAsync);
        }
        else if (Due("drill", _options.DrillHour, _options.DrillDay, now))
        {
            Run("drill", JobKind.Drill, "Weekly drill", (job, ct) => backups.DrillAsync(job, checks, ct));
        }
    }

    private bool Due(string key, int hour, DayOfWeek? day, DateTimeOffset now)
    {
        var local = Fmt.Local(now);
        if (local.Hour < hour || (day is { } d && local.DayOfWeek != d))
        {
            return false;
        }

        var today = DateOnly.FromDateTime(local.DateTime);
        return state.Read(s => s.LastScheduled.GetValueOrDefault(key)) != today && Attempt(key, now, RetryAfter);
    }

    private bool Attempt(string key, DateTimeOffset now, TimeSpan retryAfter)
    {
        var last = state.Read(s => s.LastAttempt.TryGetValue(key, out var at) ? at : (DateTimeOffset?)null);
        if (last is { } at && now - at < retryAfter)
        {
            return false;
        }

        state.Update(s => s.LastAttempt[key] = now);
        return true;
    }

    /// <summary>Runs a scheduled job. On success the slot is marked done and the retry throttle is cleared, so the next request runs at once.</summary>
    private void Run(string key, JobKind kind, string title, Func<Job, CancellationToken, Task<JobOutcome>> work, string? attemptKey = null)
    {
        _running = Task.Run(async () =>
        {
            var job = await jobs.RunAsync(kind, title, work);
            if (job.Ok == true)
            {
                state.Update(s =>
                {
                    s.LastScheduled[key] = Fmt.LocalDate(job.StartedAt);
                    s.LastAttempt.Remove(attemptKey ?? key);
                });
            }

            RefreshSoon();
        });
    }
}
