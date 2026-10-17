using Microsoft.Extensions.Options;

namespace Flowstate.BackupAgent;

// status.json in the ops bucket. PII-free. The same status feeds the agent's metrics (AgentMetrics), which Grafana
// alerts on, and the WAL heartbeat.

public sealed record AgentStatus(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    Health Health,
    IReadOnlyList<string> Issues,
    DateTimeOffset? DataSecuredAt,
    WalStatus Wal,
    BackupsStatus Backups,
    DrillStatus Drill,
    VerifyStatus Verify,
    DiskStatus? Disk,
    PostgresStatus Postgres,
    ScheduleStatus Schedule,
    VolumesStatus Volumes,
    string? SshTunnelCommand);

public sealed record WalStatus(
    string SlotStatus,
    long? RetainedBytes,
    long SlotCapBytes,
    long SpoolBytes,
    bool ReceiverRunning,
    bool ReceiverPaused,
    string? LastPushedSegment,
    DateTimeOffset? LastPushAt,
    string? RepoNewestSegment,
    DateTimeOffset? RepoNewestSegmentAt,
    int OpenGaps,
    DateTimeOffset? OpenGapSince);

public sealed record BackupsStatus(
    DateTimeOffset? LastFullAt,
    string? LastFullLabel,
    long? LastFullBytes,
    double? LastFullSeconds,
    int Count,
    DateTimeOffset? OldestRestorableAt,
    DateTimeOffset? NewestRestorableAt,
    int RetentionDays,
    bool RetentionOk);

public sealed record DrillStatus(DateTimeOffset? LastAt, bool? LastOk, double? LastMinutes, string? LastTarget, DateOnly? LastManualDrill, DateOnly? NextManualDrillDue);

public sealed record VerifyStatus(DateTimeOffset? LastAt, bool? LastOk);

public sealed record DiskStatus(int UsedPercent, long FreeBytes);

public sealed record PostgresStatus(bool Reachable, int? Version, int? Timeline);

public sealed record ScheduleStatus(DateTimeOffset NextFull, DateTimeOffset NextVerify, DateTimeOffset NextDrill);

/// <summary><see cref="GoLivePending"/>: the spare goes live on the next restart.</summary>
public sealed record VolumesStatus(string Live, string Spare, DateTimeOffset? OldVolumeSince, string? SpareContents, bool GoLivePending);

/// <summary>Builds the status from the WAL loop, the repo snapshot and the agent's state, and decides the health.</summary>
public sealed class StatusBuilder(
    IOptions<AgentOptions> options,
    WalState wal,
    RepoInfoCache repo,
    StateStore state,
    OpsBucket ops,
    RestoreService restore,
    DataVolumes volumes)
{
    private readonly AgentOptions _options = options.Value;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public AgentStatus? Last { get; private set; }

    public async Task<AgentStatus> BuildAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var w = wal.Current;
        var snapshot = repo.Current;
        var s = state.Read(x => new
        {
            Gaps = x.Gaps.Where(g => g.ClosedAt is null).ToList(),
            x.LastDrill,
            x.LastVerify,
            x.OldVolume,
            x.Spare,
            x.FullBackupRequested,
            x.LastManualDrill,
            x.NextManualDrillDue,
        });
        var disk = await Disk.UsageAsync(volumes.LiveDirectory, cancellationToken) ?? await Disk.UsageAsync(_options.StateDirectory, cancellationToken);
        var spareProblem = await restore.SpareProblemAsync(cancellationToken);

        var issues = new List<(Health Severity, string Text)>();
        void Issue(Health severity, string text) => issues.Add((severity, text));

        if (!w.PostgresReachable)
        {
            Issue(Health.Unhealthy, $"Postgres down: {w.PostgresError}");
        }

        if (w.Problem is not null)
        {
            Issue(Health.Unhealthy, w.Problem);
        }

        if (!ops.IsConfigured)
        {
            Issue(Health.Degraded, "Ops bucket not set up; status and history only on the agent's volume");
        }
        else if (ops.LastError is not null)
        {
            Issue(Health.Degraded, $"Can't write to the ops bucket: {ops.LastError}");
        }

        if (w.Slot?.WalStatus == "lost")
        {
            Issue(Health.Unhealthy, "Replication slot lost");
        }

        if (s.Gaps.Count > 0)
        {
            Issue(Health.Unhealthy, $"WAL gap since {Fmt.DateTime(s.Gaps.Min(g => g.DetectedAt))} ({s.Gaps[^1].Reason}). The next full backup closes it.");
        }

        var running = now - _startedAt > _options.MaxDataLoss;
        if (w.DataSecuredAt is { } secured ? now - secured > _options.MaxDataLoss : running)
        {
            Issue(Health.Unhealthy, $"Data secured {Fmt.Ago(w.DataSecuredAt, now)} (target: {_options.MaxDataLoss.TotalMinutes:0} min)");
        }

        if (w.PushFailingSince is { } failing && now - failing > TimeSpan.FromMinutes(5))
        {
            Issue(Health.Unhealthy, $"archive-push failing since {Fmt.DateTime(failing)}: {w.PushError}");
        }

        if (w.ReceiverPaused)
        {
            Issue(Health.Unhealthy, $"WAL receiver paused: {Fmt.Bytes(w.SpoolBytes)} waiting to be pushed");
        }

        if (w.Slot?.RetainedBytes is { } retained && retained >= _options.SlotWarnBytes)
        {
            Issue(Health.Degraded, $"Postgres holds {Fmt.Bytes(retained)} WAL for the agent (slot dropped at {Fmt.Bytes(w.Server?.SlotKeepBytes)})");
        }

        if (snapshot.Error is not null && snapshot.ReadAt != DateTimeOffset.MinValue)
        {
            Issue(Health.Unhealthy, $"pgbackrest info failing: {snapshot.Error}");
        }

        // When the repo can't be read, "no backups" would be a guess; the info error above says it instead.
        var newestFull = snapshot.NewestFull;
        var repoRead = snapshot.ReadAt != DateTimeOffset.MinValue && (snapshot.Error is null || snapshot.Backups.Count > 0);
        if (repoRead && (newestFull is null || now - newestFull.StoppedAt > TimeSpan.FromHours(26)))
        {
            Issue(Health.Unhealthy, newestFull is null ? "No full backup" : $"Newest full backup is from {Fmt.DateTime(newestFull.StoppedAt)} (over 26 h)");
        }

        var retentionCheck = BackupService.RetentionCheck(snapshot, _options.RetentionDays);
        if (retentionCheck.Ok == false)
        {
            Issue(Health.Unhealthy, $"{retentionCheck.Detail}. Delete the old backups (restore runbook §11).");
        }

        if (s.LastDrill is { Ok: false } failedDrill)
        {
            Issue(Health.Unhealthy, $"Drill failed {Fmt.DateTime(failedDrill.At)}");
        }
        else if (s.LastDrill is null || now - s.LastDrill.At > TimeSpan.FromDays(8))
        {
            Issue(Health.Degraded, "No successful drill in 8 days");
        }

        if (s.LastVerify is { Ok: false } failedVerify)
        {
            Issue(Health.Unhealthy, $"Verify failed {Fmt.DateTime(failedVerify.At)}");
        }

        if (disk is { } d && d.UsedPercent >= 80)
        {
            Issue(d.UsedPercent >= 90 ? Health.Unhealthy : Health.Degraded, $"Disk {d.UsedPercent}% full");
        }

        if (s.OldVolume is { } old)
        {
            var days = (now - old.Since).TotalDays;
            if (days >= _options.RetentionDays)
            {
                Issue(Health.Unhealthy, $"{old.Name} has held the old database for {days:0} days (DPA max {_options.RetentionDays}). Delete it on the Restore page.");
            }
            else if (days >= 7)
            {
                Issue(Health.Degraded, $"{old.Name} has held the old database for {days:0} days. Delete it when you're done investigating.");
            }
        }

        if (volumes.UnknownLiveDirectory is { } unknown)
        {
            Issue(Health.Unhealthy, $"Postgres runs on {unknown}, not on /pgdata/a or /pgdata/b. Restores are off.");
        }
        else if (volumes.GoLivePending)
        {
            Issue(Health.Degraded, $"{volumes.SpareName} goes live on the next restart");
        }
        else if (spareProblem is not null)
        {
            Issue(Health.Degraded, spareProblem);
        }

        if (s.NextManualDrillDue is { } due && due < Fmt.LocalDate(now))
        {
            Issue(Health.Unhealthy, $"Quarterly manual drill overdue since {Fmt.Date(due)}. Log it on the Drills page.");
        }

        if (s.FullBackupRequested is not null && running)
        {
            Issue(Health.Degraded, $"Full backup pending: {s.FullBackupRequested}");
        }

        var health = issues.Count == 0 ? Health.Healthy : issues.Max(i => i.Severity);
        var newestSegment = snapshot.NewestSegment;
        var status = new AgentStatus(
            SchemaVersion: 1,
            GeneratedAt: now,
            Health: health,
            Issues: [.. issues.OrderByDescending(i => i.Severity).Select(i => i.Text)],
            DataSecuredAt: w.DataSecuredAt,
            Wal: new WalStatus(
                w.Slot?.WalStatus ?? (w.PostgresReachable ? "missing" : "unknown"),
                w.Slot?.RetainedBytes,
                w.Server?.SlotKeepBytes ?? 0,
                w.SpoolBytes,
                w.ReceiverRunning,
                w.ReceiverPaused,
                w.LastPushedSegment,
                w.LastPushAt,
                newestSegment?.Name,
                newestSegment?.PushedAt,
                s.Gaps.Count,
                s.Gaps.Count == 0 ? null : s.Gaps.Min(g => g.DetectedAt)),
            Backups: new BackupsStatus(
                newestFull?.StoppedAt,
                newestFull?.Label,
                newestFull?.DatabaseBytes,
                newestFull?.DurationSeconds,
                snapshot.Backups.Count,
                snapshot.OldestRestorable,
                snapshot.NewestRestorable,
                _options.RetentionDays,
                retentionCheck.Ok != false),
            Drill: new DrillStatus(s.LastDrill?.At, s.LastDrill?.Ok, s.LastDrill is null ? null : s.LastDrill.TotalSeconds / 60, s.LastDrill?.TargetDescription, s.LastManualDrill?.Date, s.NextManualDrillDue),
            Verify: new VerifyStatus(s.LastVerify?.At, s.LastVerify?.Ok),
            Disk: disk is { } usage ? new DiskStatus(usage.UsedPercent, usage.FreeBytes) : null,
            Postgres: new PostgresStatus(w.PostgresReachable, w.Server?.VersionNum, w.Server?.Timeline),
            Schedule: new ScheduleStatus(
                Schedule.Next(now, _options.FullBackupHour, null),
                Schedule.Next(now, _options.VerifyHour, _options.VerifyDay),
                Schedule.Next(now, _options.DrillHour, _options.DrillDay)),
            Volumes: new VolumesStatus(volumes.LiveName, volumes.SpareName, s.OldVolume?.Since,
                s.OldVolume is not null ? "old live database" : s.Spare is not null ? $"restored to {s.Spare.TargetDescription}" : restore.SpareIsEmpty ? "empty" : "unknown contents",
                volumes.GoLivePending),
            SshTunnelCommand: _options.SshTunnelCommand);
        Last = status;
        return status;
    }
}

public static class Schedule
{
    /// <summary>The next Europe/Copenhagen occurrence of <paramref name="hour"/>:00, optionally on <paramref name="day"/>.</summary>
    public static DateTimeOffset Next(DateTimeOffset now, int hour, DayOfWeek? day)
    {
        var local = Fmt.Local(now);
        var candidate = local.Date.AddHours(hour);
        while (candidate <= local.DateTime || (day is { } d && candidate.DayOfWeek != d))
        {
            candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(candidate, Fmt.Copenhagen.GetUtcOffset(candidate));
    }
}
