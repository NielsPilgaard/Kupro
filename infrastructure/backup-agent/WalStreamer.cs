using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Flowstate.BackupAgent;

/// <summary>What the WAL loop knows right now. Read by the status and the console.</summary>
public sealed record WalSnapshot(
    bool PostgresReachable,
    string? PostgresError,
    ServerInfo? Server,
    SlotInfo? Slot,
    bool StanzaReady,
    string? Problem,
    bool ReceiverRunning,
    bool ReceiverPaused,
    string? ReceiverLastLine,
    long SpoolBytes,
    int SpoolFiles,
    DateTimeOffset? LastPushAt,
    string? LastPushedSegment,
    int PushedTimeline,
    ulong PushedEnd,
    string? PushError,
    DateTimeOffset? PushFailingSince,
    DateTimeOffset? DataSecuredAt,
    DateTimeOffset? LastMarkAt);

public sealed class WalState
{
    private readonly Lock _gate = new();
    private WalSnapshot _snapshot = new(false, "Not checked yet", null, null, false, null, false, false, null, 0, 0, null, null, 0, 0, null, null, null, null);

    public WalSnapshot Current => Volatile.Read(ref _snapshot);

    public void Set(Func<WalSnapshot, WalSnapshot> change)
    {
        lock (_gate)
        {
            Volatile.Write(ref _snapshot, change(_snapshot));
        }
    }
}

/// <summary>
/// WAL leaves Postgres by streaming, not archive_command. pg_receivewal writes into the
/// spool over the shared socket using the "agent" slot; finished segments are pushed with
/// pgbackrest archive-push and deleted. If the agent falls more than max_slot_wal_keep_size behind,
/// Postgres drops the slot and keeps running: the agent sees the lost slot, records a gap, makes a
/// new slot and asks for a full backup. A broken backup setup is a gap and an alert, never an outage.
/// </summary>
public sealed class WalStreamer(
    IOptions<AgentOptions> options,
    LivePostgres live,
    PgBackRest pgBackRest,
    StateStore state,
    WalState wal,
    OpsBucket ops,
    DataVolumes volumes,
    ILogger<WalStreamer> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly AgentOptions _options = options.Value;
    private readonly List<(ulong Lsn, DateTimeOffset At)> _markers = [];
    private Process? _receiver;
    private DateTimeOffset _receiverRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextMark = DateTimeOffset.MinValue;
    private bool _stanzaReady;
    private DateTimeOffset _stanzaRetryAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.WalSpoolDirectory);
        Directory.CreateDirectory(_options.WalOutboxDirectory);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "WAL loop failed");
                }

                await Task.Delay(Tick, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
        finally
        {
            StopReceiver();
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        ServerInfo? server = null;
        try
        {
            server = await live.InfoAsync(cancellationToken);
            wal.Set(s => s with { PostgresReachable = true, PostgresError = null, Server = server });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            wal.Set(s => s with { PostgresReachable = false, PostgresError = ex.Message, Slot = null });
        }

        if (server is not null)
        {
            if (!_stanzaReady && DateTimeOffset.UtcNow >= _stanzaRetryAt)
            {
                var result = await pgBackRest.StanzaCreateAsync(null, cancellationToken);
                _stanzaReady = result.Ok;
                _stanzaRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                wal.Set(s => s with { StanzaReady = _stanzaReady, Problem = _stanzaReady ? null : $"Repo unreachable (stanza-create): {result.Tail(2)}" });
            }

            if (await CheckIdentityAsync(server, cancellationToken))
            {
                await CheckSlotAsync(cancellationToken);
            }
        }

        await PushAsync(cancellationToken);
        var spoolBytes = MeasureSpool();

        // Receive even while the repo is unreachable: the spool takes the WAL off Postgres' disk until
        // its own cap, and only then does Postgres start holding WAL for the slot.
        ManageReceiver(server is not null, spoolBytes);

        if (server is not null && DateTimeOffset.UtcNow >= _nextMark)
        {
            try
            {
                var (lsn, at) = await live.MarkAsync(cancellationToken);
                _markers.Add(((ulong)lsn, at));
                wal.Set(s => s with { LastMarkAt = at });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not mark and switch WAL");
            }

            _nextMark = DateTimeOffset.UtcNow + _options.WalSwitchInterval;
        }

        UpdateSecured();
    }

    /// <summary>
    /// Notices a new live volume (a restore went live), a new timeline or a different cluster.
    /// Returns false when streaming must not continue (a different cluster than the repo knows).
    /// </summary>
    private async Task<bool> CheckIdentityAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        var (volume, systemId, timeline, spare) = state.Read(s => (s.LiveVolumeName, s.SystemIdentifier, s.Timeline, s.Spare));
        var liveSlot = volumes.SlotOf(server.DataDirectory);
        if (liveSlot is null)
        {
            // Postgres runs on neither /pgdata volume. Keep streaming WAL; the status says what's wrong
            // and restores stay off, because the agent can't tell which volume is safe to wipe.
            return true;
        }

        var live = volumes.Name(liveSlot);
        if (systemId is null)
        {
            state.Update(s =>
            {
                s.FirstStartedAt ??= DateTimeOffset.UtcNow;
                s.LiveVolumeName = live;
                s.SystemIdentifier = server.SystemIdentifier;
                s.Timeline = server.Timeline;
                s.FullBackupRequested ??= "First agent start";
            });
            await HistoryAsync("agent", true, $"Agent running against {live} (timeline {server.Timeline})", cancellationToken);
        }

        // From now on Postgres only starts on the volume this file names. Without it, an empty
        // volume would get a fresh, empty cluster at the next start.
        if (volumes.Next is null)
        {
            volumes.SetNext(liveSlot);
        }

        if (systemId is null)
        {
            return true;
        }

        if (systemId != server.SystemIdentifier)
        {
            wal.Set(s => s with
            {
                Problem = $"Live database has system id {server.SystemIdentifier}, the agent knows {systemId}. " +
                    "A new, empty database? Check which volume /pg-control/active names. pgBackRest rejects its WAL; see the restore runbook §12.",
            });
            StopReceiver();
            return false;
        }

        if (volume != live)
        {
            var now = DateTimeOffset.UtcNow;
            var old = volumes.Name(DataVolumes.Other(liveSlot));
            state.Update(s =>
            {
                s.LiveVolumeName = live;
                s.Timeline = server.Timeline;
                s.OldVolume = new OldVolume(old, now);
                s.LastGoLive = new GoLive(now, live, spare?.TargetDescription);
                s.Spare = null;
                s.Checklist = [];
                s.FullBackupRequested = $"{live} went live";
            });
            await HistoryAsync("golive", true, $"Live volume switched from {old} to {live}. {old} kept for forensics.", cancellationToken);
            await ResetStreamingAsync($"{live} went live", gap: false, cancellationToken);
            return true;
        }

        if (timeline != server.Timeline)
        {
            state.Update(s =>
            {
                s.Timeline = server.Timeline;
                s.FullBackupRequested = $"New timeline {server.Timeline}";
            });
            await HistoryAsync("timeline", true, $"Live database on timeline {server.Timeline} (was {timeline})", cancellationToken);
            await ResetStreamingAsync($"New timeline {server.Timeline}", gap: false, cancellationToken);
        }

        return true;
    }

    private async Task CheckSlotAsync(CancellationToken cancellationToken)
    {
        var slot = await live.SlotAsync(cancellationToken);
        wal.Set(s => s with { Slot = slot });
        if (slot is null)
        {
            var hadSlot = state.Read(s => s.SlotCreatedAt is not null);
            await ResetStreamingAsync(hadSlot ? "Replication slot was missing" : "First replication slot", gap: hadSlot, cancellationToken);
        }
        else if (slot.WalStatus == "lost")
        {
            await ResetStreamingAsync("Postgres dropped the slot: the agent was more than max_slot_wal_keep_size behind", gap: true, cancellationToken);
        }
    }

    /// <summary>
    /// Starts streaming over from a fresh slot. Everything still in the receive directory moves to the
    /// outbox and gets pushed, including the partial segment: Postgres already dropped its copy of
    /// that WAL, and pgBackRest accepts .partial files. It has to leave the receive directory,
    /// or pg_receivewal would try to resume from it.
    /// </summary>
    private async Task ResetStreamingAsync(string reason, bool gap, CancellationToken cancellationToken)
    {
        StopReceiver();
        foreach (var file in Directory.EnumerateFiles(_options.WalSpoolDirectory))
        {
            File.Move(file, Path.Combine(_options.WalOutboxDirectory, Path.GetFileName(file)), overwrite: true);
        }

        await live.RecreateSlotAsync(cancellationToken);
        _markers.Clear();
        _nextMark = DateTimeOffset.UtcNow;
        var lastPushed = wal.Current.LastPushedSegment;
        state.Update(s =>
        {
            s.SlotCreatedAt = DateTimeOffset.UtcNow;
            if (gap)
            {
                s.Gaps.Add(new WalGap(DateTimeOffset.UtcNow, reason, lastPushed));
                s.FullBackupRequested = reason;
            }
        });
        wal.Set(s => s with { PushedEnd = 0, PushedTimeline = 0 });
        logger.LogWarning("WAL streaming reset: {Reason}", reason);
        await HistoryAsync(gap ? "wal-gap" : "slot", !gap, gap ? $"WAL gap: {reason}. New slot created; full backup requested." : reason, cancellationToken);
    }

    private async Task PushAsync(CancellationToken cancellationToken)
    {
        // Partial segments only from the outbox: the one in the receive directory is still being written.
        var outbox = Directory.EnumerateFiles(_options.WalOutboxDirectory)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(f => Wal.SegmentName().IsMatch(f.Name) || Wal.HistoryName().IsMatch(f.Name) || Wal.PartialName().IsMatch(f.Name));
        var received = Directory.EnumerateFiles(_options.WalSpoolDirectory)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(f => Wal.SegmentName().IsMatch(f.Name) || Wal.HistoryName().IsMatch(f.Name));
        var files = outbox.Concat(received)
            .OrderBy(f => Wal.HistoryName().IsMatch(f.Name) ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var (path, name) in files)
        {
            var result = await pgBackRest.ArchivePushAsync(path, cancellationToken);
            if (!result.Ok && Wal.PartialName().IsMatch(name))
            {
                // A partial never blocks the segments after it; it's retried next tick and shows in the log.
                logger.LogWarning("archive-push of partial {Name} failed: {Error}", name, result.Tail(2));
                continue;
            }

            if (!result.Ok)
            {
                var error = result.Tail(2);
                wal.Set(s => s with { PushError = error, PushFailingSince = s.PushFailingSince ?? DateTimeOffset.UtcNow });
                logger.LogWarning("archive-push {Name} failed: {Error}", name, error);
                return;
            }

            File.Delete(path);
            if (Wal.SegmentName().IsMatch(name))
            {
                var segment = new RepoSegment(name, DateTimeOffset.UtcNow);
                wal.Set(s => s with
                {
                    LastPushAt = DateTimeOffset.UtcNow,
                    LastPushedSegment = name,
                    PushError = null,
                    PushFailingSince = null,
                    PushedTimeline = segment.Timeline,
                    PushedEnd = segment.Timeline == s.PushedTimeline ? Math.Max(s.PushedEnd, Wal.SegmentEnd(name)) : Wal.SegmentEnd(name),
                });
            }
        }
    }

    private long MeasureSpool()
    {
        var files = Directory.EnumerateFiles(_options.WalSpoolDirectory).Concat(Directory.EnumerateFiles(_options.WalOutboxDirectory))
            .Select(f => new FileInfo(f)).ToList();
        var bytes = files.Sum(f => f.Length);
        wal.Set(s => s with { SpoolBytes = bytes, SpoolFiles = files.Count });
        return bytes;
    }

    private void ManageReceiver(bool canRun, long spoolBytes)
    {
        if (_receiver is { HasExited: true } exited)
        {
            logger.LogWarning("pg_receivewal exited with {Code}", exited.ExitCode);
            _receiverRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(exited.ExitCode == 0 ? 5 : 30);
            exited.Dispose();
            _receiver = null;
            wal.Set(s => s with { ReceiverRunning = false });
        }

        // Pause while the spool is over its cap, so a dead S3 can't fill the disk through the agent.
        var paused = wal.Current.ReceiverPaused;
        if (spoolBytes >= _options.WalSpoolMaxBytes)
        {
            paused = true;
            StopReceiver();
        }
        else if (paused && spoolBytes < _options.WalSpoolMaxBytes / 2)
        {
            paused = false;
        }

        wal.Set(s => s with { ReceiverPaused = paused });
        if (paused || !canRun || _receiver is not null || DateTimeOffset.UtcNow < _receiverRetryAt || wal.Current.Slot is not { WalStatus: not "lost" })
        {
            return;
        }

        _receiver = Shell.Start("pg_receivewal",
        [
            "--host", _options.SocketDirectory,
            "--username", _options.DatabaseUser,
            "--slot", _options.SlotName,
            "--directory", _options.WalSpoolDirectory,
            "--synchronous",
            "--no-loop",
            "--no-password",
        ]);
        _receiver.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                wal.Set(s => s with { ReceiverLastLine = e.Data });
            }
        };
        _receiver.OutputDataReceived += (_, _) => { };
        _receiver.BeginErrorReadLine();
        _receiver.BeginOutputReadLine();
        wal.Set(s => s with { ReceiverRunning = true, ReceiverLastLine = null });
        logger.LogInformation("pg_receivewal started");
    }

    private void StopReceiver()
    {
        if (_receiver is null)
        {
            return;
        }

        try
        {
            if (!_receiver.HasExited)
            {
                _receiver.Kill(entireProcessTree: true);
                _receiver.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _receiver.Dispose();
        _receiver = null;
        wal.Set(s => s with { ReceiverRunning = false });
    }

    /// <summary>Data is secured up to a marker's commit time once the segment holding it is in the repo.</summary>
    private void UpdateSecured()
    {
        var current = wal.Current;
        var timeline = current.Server?.Timeline;
        if (timeline is null || current.PushedTimeline != timeline)
        {
            return;
        }

        DateTimeOffset? secured = null;
        _markers.RemoveAll(m =>
        {
            if (m.Lsn > current.PushedEnd)
            {
                return false;
            }

            secured = secured is { } s && s > m.At ? s : m.At;
            return true;
        });
        if (secured is { } at)
        {
            wal.Set(s => s with { DataSecuredAt = s.DataSecuredAt is { } old && old > at ? old : at });
        }
    }

    private Task HistoryAsync(string kind, bool ok, string summary, CancellationToken cancellationToken) =>
        ops.AppendHistoryAsync(new HistoryEntry(DateTimeOffset.UtcNow, kind, ok, summary), cancellationToken);
}

public static class Disk
{
    /// <summary>Used percent and free bytes of the filesystem holding <paramref name="path"/> (the host disk for a volume).</summary>
    public static async Task<(int UsedPercent, long FreeBytes)?> UsageAsync(string path, CancellationToken cancellationToken)
    {
        var result = await Shell.RunAsync("df", ["-P", "-B1", path], null, cancellationToken);
        var line = result.Ok ? result.Output.Skip(1).FirstOrDefault() : null;
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 5 } || !long.TryParse(parts[3], out var free) || !int.TryParse(parts[4].TrimEnd('%'), out var used))
        {
            return null;
        }

        return (used, free);
    }
}

internal static class JsonExtensions
{
    public static JsonElement ToJson(this object value) => JsonSerializer.SerializeToElement(value, AgentJson.Options);
}
