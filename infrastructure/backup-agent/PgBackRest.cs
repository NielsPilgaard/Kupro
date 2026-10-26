using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Kupro.BackupAgent;

public sealed record RepoBackup(
    string Label,
    string Type,
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    long DatabaseBytes,
    long RepoBytes,
    string WalStart,
    string WalStop)
{
    public double DurationSeconds => (StoppedAt - StartedAt).TotalSeconds;
}

/// <summary>A WAL segment in the repo. <see cref="PushedAt"/> is the object's time, i.e. when the agent pushed it.</summary>
public sealed record RepoSegment(string Name, DateTimeOffset PushedAt)
{
    public int Timeline => int.Parse(Name[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Position in the WAL stream; 16 MB segments, so 256 per log id.</summary>
    public long Index => Wal.SegmentIndex(Name);
}

/// <summary>A span of time a restore can reach: a backup plus unbroken WAL after it, on one timeline.</summary>
public sealed record RestorableRange(int Timeline, DateTimeOffset From, DateTimeOffset To, string FromBackup);

/// <summary>Everything the console and the status need from the repo, refreshed by <see cref="RepoInfoCache"/>.</summary>
public sealed record RepoSnapshot(
    DateTimeOffset ReadAt,
    bool StanzaOk,
    string? Error,
    IReadOnlyList<RepoBackup> Backups,
    IReadOnlyList<RepoSegment> Segments,
    IReadOnlyList<RestorableRange> Ranges)
{
    public static readonly RepoSnapshot Empty = new(DateTimeOffset.MinValue, false, "Not read yet", [], [], []);

    public RepoBackup? NewestFull => Backups.Where(b => b.Type == "full").MaxBy(b => b.StoppedAt);
    public RepoBackup? Oldest => Backups.MinBy(b => b.StartedAt);
    /// <summary>The segment pushed last. After flipping back to an older volume that's a lower timeline, so order by time first.</summary>
    public RepoSegment? NewestSegment => Segments.MaxBy(s => (s.PushedAt, s.Timeline, s.Index));
    public DateTimeOffset? OldestRestorable => Ranges.Count == 0 ? null : Ranges.Min(r => r.From);
    public DateTimeOffset? NewestRestorable => Ranges.Count == 0 ? null : Ranges.Max(r => r.To);

    public RestorableRange? RangeFor(DateTimeOffset target) =>
        Ranges.Where(r => r.From <= target && target <= r.To).OrderByDescending(r => r.Timeline).ThenByDescending(r => r.From).FirstOrDefault();
}

public static partial class Wal
{
    public const long SegmentBytes = 16 * 1024 * 1024;

    [GeneratedRegex("^[0-9A-F]{24}$")]
    public static partial Regex SegmentName();

    [GeneratedRegex("^[0-9A-F]{8}\\.history$")]
    public static partial Regex HistoryName();

    [GeneratedRegex("^[0-9A-F]{24}\\.partial$")]
    public static partial Regex PartialName();

    public static long SegmentIndex(string name) =>
        (long.Parse(name[8..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 8)
        + long.Parse(name[16..24], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>The first LSN after the segment, as a 64-bit position.</summary>
    public static ulong SegmentEnd(string name) => (ulong)(SegmentIndex(name) + 1) * SegmentBytes;
}

/// <summary>
/// Thin wrapper over the pgbackrest CLI. Repo settings come from PGBACKREST_* env vars. pg1-path is
/// the live volume, which changes when a restore goes live, so it's passed per call, not in the config.
/// </summary>
public sealed class PgBackRest(IOptions<AgentOptions> options, DataVolumes volumes)
{
    private readonly AgentOptions _options = options.Value;

    /// <summary>Commands that read the cluster. pgBackRest rejects --pg1-path on the repo-only ones (info, verify, expire).</summary>
    private static readonly string[] ClusterCommands = ["stanza-create", "check", "backup", "archive-push", "restore"];

    public Task<ShellResult> RunAsync(IEnumerable<string> arguments, Action<string>? log, CancellationToken cancellationToken)
    {
        var list = arguments.ToList();
        if (list.Any(ClusterCommands.Contains) && !list.Any(a => a.StartsWith("--pg1-path=", StringComparison.Ordinal)))
        {
            list.Insert(0, $"--pg1-path={volumes.LiveDirectory}");
        }

        return Shell.RunAsync("pgbackrest", [$"--stanza={_options.Stanza}", .. list], log, cancellationToken);
    }

    /// <summary>
    /// Short I/O timeout for the quick calls the WAL loop and the status tick make, so a dead S3
    /// shows up as an error within seconds instead of stalling them for minutes of retries.
    /// </summary>
    private const string QuickTimeout = "--io-timeout=15";

    public Task<ShellResult> StanzaCreateAsync(Action<string>? log, CancellationToken cancellationToken) =>
        RunAsync([QuickTimeout, "stanza-create"], log, cancellationToken);

    public Task<ShellResult> BackupAsync(string type, Action<string>? log, CancellationToken cancellationToken) =>
        RunAsync([$"--type={type}", "backup"], log, cancellationToken);

    public Task<ShellResult> ExpireAsync(Action<string>? log, CancellationToken cancellationToken) =>
        RunAsync(["expire"], log, cancellationToken);

    public Task<ShellResult> VerifyAsync(Action<string>? log, CancellationToken cancellationToken) =>
        RunAsync(["verify"], log, cancellationToken);

    public Task<ShellResult> ArchivePushAsync(string path, CancellationToken cancellationToken) =>
        RunAsync([QuickTimeout, "--log-level-console=warn", "archive-push", path], null, cancellationToken);

    public Task<ShellResult> RestoreAsync(string dataDirectory, IEnumerable<string> targetArguments, Action<string>? log, CancellationToken cancellationToken) =>
        RunAsync([$"--pg1-path={dataDirectory}", "--target-action=promote", .. targetArguments, "restore"], log, cancellationToken);

    public async Task<RepoSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var info = await RunAsync([QuickTimeout, "--output=json", "--log-level-console=error", "info"], null, cancellationToken);
        if (!info.Ok)
        {
            return RepoSnapshot.Empty with { ReadAt = DateTimeOffset.UtcNow, Error = info.Reason(3) };
        }

        var json = string.Join('\n', info.Output);
        bool stanzaOk;
        string? error;
        List<RepoBackup> backups;
        try
        {
            (stanzaOk, error, backups) = ParseInfo(json);
        }
        catch (JsonException)
        {
            // A wrong cipher pass decrypts the info files to binary, which pgBackRest copies into its JSON unescaped.
            // Throwing here would fail every status tick, so the agent would go silent instead of unhealthy.
            var formatError = json.IndexOf("[FormatError]", StringComparison.Ordinal);
            var message = formatError < 0 ? "pgbackrest info returned output that isn't JSON" : json[formatError..].Split(["\\n", "\n", "\""], StringSplitOptions.None)[0];
            return RepoSnapshot.Empty with { ReadAt = DateTimeOffset.UtcNow, Error = Explain(message, encrypted: formatError >= 0) };
        }

        var segments = new List<RepoSegment>();
        var ls = await RunAsync([QuickTimeout, "--output=json", "--recurse", "--log-level-console=error", "repo-ls", $"archive/{_options.Stanza}"], null, cancellationToken);
        if (ls.Ok)
        {
            segments = ParseSegments(string.Join('\n', ls.Output));
        }
        else
        {
            error ??= ls.Reason(3);
        }

        return new RepoSnapshot(DateTimeOffset.UtcNow, stanzaOk, error, backups, segments, Ranges(backups, segments));
    }

    private static (bool StanzaOk, string? Error, List<RepoBackup> Backups) ParseInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var stanza = document.RootElement.EnumerateArray().FirstOrDefault();
        if (stanza.ValueKind != JsonValueKind.Object)
        {
            return (false, "No stanza in the repo", []);
        }

        var status = stanza.GetProperty("status");
        var code = status.GetProperty("code").GetInt32();
        var message = status.GetProperty("message").GetString();
        var backups = new List<RepoBackup>();
        var list = stanza.TryGetProperty("backup", out var found) ? found.EnumerateArray().ToList() : [];
        foreach (var backup in list)
        {
            if (backup.TryGetProperty("error", out var failed) && failed.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var timestamp = backup.GetProperty("timestamp");
            var info = backup.GetProperty("info");
            var archive = backup.GetProperty("archive");
            backups.Add(new RepoBackup(
                backup.GetProperty("label").GetString()!,
                backup.GetProperty("type").GetString()!,
                DateTimeOffset.FromUnixTimeSeconds(timestamp.GetProperty("start").GetInt64()),
                DateTimeOffset.FromUnixTimeSeconds(timestamp.GetProperty("stop").GetInt64()),
                info.GetProperty("size").GetInt64(),
                info.GetProperty("repository").GetProperty("size").GetInt64(),
                archive.GetProperty("start").GetString()!,
                archive.GetProperty("stop").GetString()!));
        }

        // Code 2 is "no valid backups": the stanza exists but is empty.
        return (code is 0 or 2, code is 0 or 2 ? null : InfoError(stanza, message), backups);
    }

    /// <summary>
    /// The stanza's status message is only "other" when the repo fails; the reason is on the repo. A wrong cipher pass
    /// decrypts the info files to garbage, which pgBackRest reports as a FormatError with binary in it and a hint about
    /// stanza-create, so say what it most likely is (rehearsal, task 021).
    /// </summary>
    private static string? InfoError(JsonElement stanza, string? message)
    {
        var repoMessage = stanza.TryGetProperty("repo", out var repos)
            ? repos.EnumerateArray()
                .Select(r => r.TryGetProperty("status", out var s) && s.TryGetProperty("message", out var m) ? m.GetString() : null)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            : null;
        if (repoMessage is null)
        {
            return message;
        }

        var encrypted = stanza.TryGetProperty("cipher", out var cipher) && cipher.GetString() is { } c && c != "none";
        return Explain(repoMessage, encrypted);
    }

    private static string Explain(string message, bool encrypted)
    {
        var firstLine = message.Split('\n')[0].Trim();
        return encrypted && message.Contains("FormatError", StringComparison.Ordinal)
            ? $"Can't read the repo's info files. Most likely a wrong cipher pass (PGBACKREST_REPO1_CIPHER_PASS). pgBackRest: {firstLine}"
            : firstLine;
    }

    private static List<RepoSegment> ParseSegments(string json)
    {
        using var document = JsonDocument.Parse(json);
        var segments = new List<RepoSegment>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var file = Path.GetFileName(entry.Name);
            if (entry.Value.GetProperty("type").GetString() != "file" || file.Length < 24 || !Wal.SegmentName().IsMatch(file[..24]))
            {
                continue;
            }

            segments.Add(new RepoSegment(file[..24], DateTimeOffset.FromUnixTimeSeconds(entry.Value.GetProperty("time").GetInt64())));
        }

        return segments;
    }

    /// <summary>
    /// Each backup is restorable from its stop time to the push time of the last segment in the
    /// unbroken run that starts at its first segment. A gap in the WAL ends the run, so a target
    /// past a gap is only reachable from a later backup.
    /// </summary>
    public static List<RestorableRange> Ranges(IReadOnlyList<RepoBackup> backups, IReadOnlyList<RepoSegment> segments)
    {
        var byTimeline = segments
            .GroupBy(s => s.Timeline)
            .ToDictionary(g => g.Key, g => g.GroupBy(s => s.Index).ToDictionary(x => x.Key, x => x.Max(s => s.PushedAt)));

        var ranges = new List<RestorableRange>();
        foreach (var backup in backups)
        {
            var start = new RepoSegment(backup.WalStart, default);
            var stop = new RepoSegment(backup.WalStop, default);
            if (!byTimeline.TryGetValue(start.Timeline, out var timeline))
            {
                continue;
            }

            var index = start.Index;
            DateTimeOffset? end = null;
            while (timeline.TryGetValue(index, out var pushedAt))
            {
                end = pushedAt;
                index++;
            }

            // The backup needs every segment up to its stop segment; without them it can't be restored at all.
            if (end is { } to && index > stop.Index)
            {
                ranges.Add(new RestorableRange(start.Timeline, backup.StoppedAt, to < backup.StoppedAt ? backup.StoppedAt : to, backup.Label));
            }
        }

        return ranges;
    }
}

/// <summary>The last repo snapshot. pgbackrest info and repo-ls list S3, so they run on the status tick and after jobs, not per page view.</summary>
public sealed class RepoInfoCache(PgBackRest pgBackRest)
{
    private RepoSnapshot _snapshot = RepoSnapshot.Empty;

    public RepoSnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Reads the repo again. If it can't be read (S3 down), keeps the last good lists with the error,
    /// so the console still shows what was restorable instead of "no backups".
    /// </summary>
    public async Task<RepoSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await pgBackRest.SnapshotAsync(cancellationToken);
        var previous = Current;
        if (snapshot.Error is not null && snapshot.Backups.Count == 0 && previous.Backups.Count > 0)
        {
            snapshot = previous with { ReadAt = snapshot.ReadAt, StanzaOk = false, Error = snapshot.Error };
        }

        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }
}
