using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Kupro.BackupAgent;

/// <summary>Full and incremental backups, expire and verify.</summary>
public sealed class BackupService(
    IOptions<AgentOptions> options,
    PgBackRest pgBackRest,
    LivePostgres live,
    WalState wal,
    StateStore state,
    RepoInfoCache repo,
    Heartbeats heartbeats)
{
    private static readonly TimeSpan WalWait = TimeSpan.FromMinutes(10);

    public async Task<JobOutcome> BackupAsync(Job job, string type, string reason, CancellationToken cancellationToken)
    {
        job.Log($"Reason: {reason}");
        var started = DateTimeOffset.UtcNow;
        var result = await pgBackRest.BackupAsync(type, job.Log, cancellationToken);
        if (!result.Ok)
        {
            return await FailAsync(type, $"pgbackrest backup failed: {result.Tail(2)}", cancellationToken);
        }

        var snapshot = await repo.RefreshAsync(cancellationToken);
        var backup = snapshot.Backups.Where(b => b.StartedAt >= started.AddMinutes(-1)).MaxBy(b => b.StartedAt);
        if (backup is null)
        {
            return await FailAsync(type, "Backup missing from pgbackrest info", cancellationToken);
        }

        // pgBackRest runs with archive-check=n: it refuses to check the archive when Postgres has no
        // archive_mode. So the agent checks itself that the WAL the backup needs is in the
        // repo. Until then the backup can't be restored.
        job.Log($"Waiting for WAL segment {backup.WalStop} to reach the repo…");
        await live.MarkAsync(cancellationToken);
        if (!await WaitForSegmentAsync(backup.WalStop, cancellationToken))
        {
            return await FailAsync(type, $"WAL segment {backup.WalStop} didn't reach the repo within {WalWait.TotalMinutes:0} min", cancellationToken);
        }

        job.Log("WAL in the repo. Expiring old backups…");
        var expire = await pgBackRest.ExpireAsync(job.Log, cancellationToken);
        if (!expire.Ok)
        {
            job.Log($"expire failed: {expire.Tail(2)}");
        }

        snapshot = await repo.RefreshAsync(cancellationToken);
        if (type == "full")
        {
            state.Update(s =>
            {
                s.FullBackupRequested = null;
                if (s.LastGoLive is { } goLive && backup.StartedAt > goLive.At)
                {
                    s.Checklist.TryAdd("full-backup", DateTimeOffset.UtcNow);
                }

                for (var i = 0; i < s.Gaps.Count; i++)
                {
                    if (s.Gaps[i].ClosedAt is null)
                    {
                        s.Gaps[i] = s.Gaps[i] with { ClosedAt = DateTimeOffset.UtcNow, ClosedByBackup = backup.Label };
                    }
                }
            });
        }

        var summary = $"{(type == "full" ? "Full" : "Incremental")} backup {backup.Label}: {Fmt.Bytes(backup.DatabaseBytes)} " +
            $"({Fmt.Bytes(backup.RepoBytes)} in repo) in {Fmt.Duration(backup.DurationSeconds)}. {snapshot.Backups.Count} backups in repo.";
        if (type == "full")
        {
            await heartbeats.SendAsync(Heartbeats.Kind.Backup, expire.Ok ? Health.Healthy : Health.Degraded, summary, DateTimeOffset.UtcNow - started, cancellationToken);
        }

        // The backup succeeded even when expire didn't: the Degraded heartbeat and the retention check
        // report that. A failed job would make the scheduler re-run the full backup every hour.
        return new JobOutcome(true, expire.Ok ? summary : $"{summary} expire failed: {expire.Tail(2)}", new { backup.Label, type, backup.DatabaseBytes, backup.RepoBytes, backup.DurationSeconds, reason, expireOk = expire.Ok });
    }

    private async Task<bool> WaitForSegmentAsync(string segmentName, CancellationToken cancellationToken)
    {
        var segment = new RepoSegment(segmentName, default);
        var end = Wal.SegmentEnd(segmentName);
        var deadline = DateTimeOffset.UtcNow + WalWait;
        var nextRepoCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = wal.Current;
            if (current.PushedTimeline == segment.Timeline && current.PushedEnd >= end)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= nextRepoCheck)
            {
                var snapshot = await repo.RefreshAsync(cancellationToken);
                if (snapshot.Segments.Any(s => s.Timeline == segment.Timeline && s.Index >= segment.Index))
                {
                    return true;
                }

                nextRepoCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }

    private async Task<JobOutcome> FailAsync(string type, string reason, CancellationToken cancellationToken)
    {
        if (type == "full")
        {
            await heartbeats.SendAsync(Heartbeats.Kind.Backup, Health.Unhealthy, reason, null, cancellationToken);
        }

        return new JobOutcome(false, reason);
    }

    public async Task<JobOutcome> VerifyAsync(Job job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await pgBackRest.VerifyAsync(job.Log, cancellationToken);
        var summary = result.Ok ? "All backups and WAL read and decrypted" : $"verify failed: {result.Tail(3)}";
        state.Update(s => s.LastVerify = new VerifyResult(DateTimeOffset.UtcNow, result.Ok, summary));
        await heartbeats.SendAsync(Heartbeats.Kind.Verify, result.Ok ? Health.Healthy : Health.Unhealthy, summary, stopwatch.Elapsed, cancellationToken);
        return new JobOutcome(result.Ok, summary);
    }

    /// <summary>The weekly drill: restore into tmpfs, check against live, throw it away.</summary>
    public async Task<JobOutcome> DrillAsync(Job job, DrillChecks checks, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var total = Stopwatch.StartNew();
        var snapshot = await repo.RefreshAsync(cancellationToken);
        var newest = snapshot.Backups.MaxBy(b => b.StoppedAt);
        if (newest is null)
        {
            return await DrillFailedAsync("No backups to restore", total.Elapsed, cancellationToken);
        }

        // Point-in-time 30 minutes back. Too early for that (a fresh repo): just before the
        // newest heartbeat commit that is known to be in the repo, so the drill still replays WAL.
        // A time target needs a commit after it in the archive, which that heartbeat is.
        DateTimeOffset? target = DateTimeOffset.UtcNow.AddMinutes(-30);
        var range = snapshot.RangeFor(target.Value);
        if (range is null && wal.Current.DataSecuredAt is { } secured)
        {
            target = secured.AddSeconds(-1);
            range = snapshot.RangeFor(target.Value);
        }

        string[] targetArguments;
        string description;
        if (range is not null)
        {
            targetArguments = ["--type=time", $"--target={Fmt.PgTimestamp(target.Value)}", $"--target-timeline={range.Timeline}"];
            description = Fmt.DateTime(target);
        }
        else
        {
            targetArguments = ["--type=immediate", $"--set={newest.Label}"];
            description = $"backup {newest.Label}";
            target = null;
        }

        var dataDirectory = Path.Combine(opts.DrillDirectory, "pgdata");
        try
        {
            Volumes.Recreate(dataDirectory);
            job.Log($"Restoring to {description} in {dataDirectory} (tmpfs)…");
            var restoreWatch = Stopwatch.StartNew();
            var restore = await pgBackRest.RestoreAsync(dataDirectory, targetArguments, job.Log, cancellationToken);
            if (!restore.Ok)
            {
                return await DrillFailedAsync($"pgbackrest restore failed: {restore.Tail(2)}", total.Elapsed, cancellationToken);
            }

            var restoreSeconds = restoreWatch.Elapsed.TotalSeconds;
            job.Log("Starting a temporary Postgres, replaying WAL…");
            var recoveryWatch = Stopwatch.StartNew();
            CheckReport report;
            await using (var temp = await TempPostgres.StartAsync(dataDirectory, "drill", 5434, job.Log, cancellationToken))
            {
                var recoverySeconds = recoveryWatch.Elapsed.TotalSeconds;
                report = await checks.RunAsync(temp, target, job.Log, cancellationToken);
                report.Checks.Add(RetentionCheck(snapshot, opts.RetentionDays));
                await temp.StopAsync();

                var ok = report.Checks.All(c => c.Ok != false);
                var result = new DrillResult(DateTimeOffset.UtcNow, ok, total.Elapsed.TotalSeconds, restoreSeconds, recoverySeconds,
                    description, report.ReachedAt, report.Checks, report.TableCount, report.Rows, report.MigrationId);
                state.Update(s => s.LastDrill = result);

                var failed = report.Checks.Where(c => c.Ok == false).Select(c => $"{c.Name}: {c.Detail}").ToList();
                var summary = ok
                    ? $"Drill OK: {report.TableCount} tables, {Fmt.Number(report.Rows)} rows, restored in {Fmt.Duration(total.Elapsed.TotalSeconds)}"
                    : $"Drill failed: {string.Join("; ", failed)}";
                await heartbeats.SendAsync(Heartbeats.Kind.Drill, ok ? Health.Healthy : Health.Unhealthy, summary, total.Elapsed, cancellationToken);
                return new JobOutcome(ok, summary, result);
            }
        }
        finally
        {
            // No restored copy survives the drill, or deleted data would live on in it.
            Volumes.Delete(dataDirectory);
        }
    }

    private async Task<JobOutcome> DrillFailedAsync(string reason, TimeSpan took, CancellationToken cancellationToken)
    {
        var result = new DrillResult(DateTimeOffset.UtcNow, false, took.TotalSeconds, 0, 0, "—", null, [new CheckResult("Restore", false, reason)], 0, 0, null);
        state.Update(s => s.LastDrill = result);
        await heartbeats.SendAsync(Heartbeats.Kind.Drill, Health.Unhealthy, reason, took, cancellationToken);
        return new JobOutcome(false, reason, result);
    }

    public static CheckResult RetentionCheck(RepoSnapshot snapshot, int retentionDays)
    {
        var oldest = snapshot.Oldest;
        if (oldest is null)
        {
            return new CheckResult("Retention", null, "No backups");
        }

        var age = DateTimeOffset.UtcNow - oldest.StartedAt;
        return new CheckResult("Retention", age.TotalDays <= retentionDays,
            $"Oldest backup {age.TotalDays:0.#} days old (max {retentionDays}, DPA)");
    }
}

/// <summary>Wiping restore targets. Only ever the drill tmpfs or the spare volume, never the live one.</summary>
public static class Volumes
{
    public static void Recreate(string directory)
    {
        Delete(directory);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Empties a mount point (a volume root can't be deleted itself) and keeps it 0700 for Postgres.</summary>
    public static void WipeContents(string mountPoint)
    {
        foreach (var directory in Directory.EnumerateDirectories(mountPoint))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(mountPoint))
        {
            File.Delete(file);
        }

        File.SetUnixFileMode(mountPoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>"device:inode" of a directory, to tell whether two mounts are the same volume.</summary>
    public static async Task<string?> IdentityAsync(string path, CancellationToken cancellationToken)
    {
        var result = await Shell.RunAsync("stat", ["-c", "%d:%i", path], null, cancellationToken);
        return result.Ok && result.Output.Count > 0 ? result.Output[0].Trim() : null;
    }
}
