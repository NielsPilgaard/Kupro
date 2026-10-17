using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Flowstate.BackupAgent;

public enum Health
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>One check in a drill or a restore. <see cref="Ok"/> is null when it was skipped (e.g. live is down).</summary>
public sealed record CheckResult(string Name, bool? Ok, string Detail);

public sealed record TableComparison(string Table, long Restored, long? Live);

public sealed record DrillResult(
    DateTimeOffset At,
    bool Ok,
    double TotalSeconds,
    double RestoreSeconds,
    double RecoverySeconds,
    string TargetDescription,
    DateTimeOffset? ReachedAt,
    IReadOnlyList<CheckResult> Checks,
    int Tables,
    long Rows,
    string? MigrationId);

public sealed record VerifyResult(DateTimeOffset At, bool Ok, string Summary);

/// <summary>A break in the WAL chain. PITR can't cross it; the next full backup closes it.</summary>
public sealed record WalGap(DateTimeOffset DetectedAt, string Reason, string? LastPushedSegment, DateTimeOffset? ClosedAt = null, string? ClosedByBackup = null);

/// <summary>The spare volume holds the previous live database since <see cref="Since"/>, kept for forensics.</summary>
public sealed record OldVolume(string Name, DateTimeOffset Since);

/// <summary>What a restore wizard run left on the spare volume.</summary>
public sealed record SpareRestore(
    DateTimeOffset RestoredAt,
    bool Ok,
    string TargetDescription,
    DateTimeOffset? ReachedAt,
    double DurationSeconds,
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<TableComparison> Tables,
    string? MigrationId,
    long? KeycloakUsers);

public sealed record GoLive(DateTimeOffset At, string Volume, string? TargetDescription);

public sealed record ManualDrill(DateOnly Date, int RtoMinutes, string Notes, DateTimeOffset LoggedAt);

/// <summary>
/// The agent's own memory, in state.json on its volume. Not in the ops bucket: it describes this
/// box (which volume is live, open WAL gaps, what's on the spare). Everything a human may need after
/// losing the box goes to the ops bucket as history instead.
/// </summary>
public sealed class AgentState
{
    /// <summary>When this agent first ran here. The first quarterly manual drill is due 3 months after.</summary>
    public DateTimeOffset? FirstStartedAt { get; set; }

    /// <summary>The volume the agent last saw Postgres run on. Counts as live while Postgres is down.</summary>
    public string? LiveVolumeName { get; set; }
    public long? SystemIdentifier { get; set; }
    public int? Timeline { get; set; }
    public DateTimeOffset? SlotCreatedAt { get; set; }
    public List<WalGap> Gaps { get; set; } = [];

    /// <summary>Why a full backup must run as soon as possible (new slot, new timeline, first start), or null.</summary>
    public string? FullBackupRequested { get; set; }

    /// <summary>Europe/Copenhagen dates the scheduled jobs last succeeded, so each runs once per slot.</summary>
    public Dictionary<string, DateOnly> LastScheduled { get; set; } = [];

    /// <summary>Last attempt per scheduled job, so a failing job is retried hourly, not every tick.</summary>
    public Dictionary<string, DateTimeOffset> LastAttempt { get; set; } = [];

    public DateTimeOffset? LastManualBackupAt { get; set; }
    public DrillResult? LastDrill { get; set; }
    public VerifyResult? LastVerify { get; set; }
    public OldVolume? OldVolume { get; set; }
    public SpareRestore? Spare { get; set; }
    public GoLive? LastGoLive { get; set; }

    /// <summary>Post-restore checklist: item key → when it was ticked. Reset by each go-live.</summary>
    public Dictionary<string, DateTimeOffset> Checklist { get; set; } = [];

    public ManualDrill? LastManualDrill { get; set; }

    /// <summary>The quarterly manual drill is due 3 months after the last one, the first one 3 months after the agent's first start.</summary>
    [JsonIgnore]
    public DateOnly? NextManualDrillDue =>
        LastManualDrill is { } manual ? manual.Date.AddMonths(3)
        : FirstStartedAt is { } first ? Fmt.LocalDate(first).AddMonths(3)
        : null;
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };
}

/// <summary>Thread-safe access to <see cref="AgentState"/>, saved to disk on every change.</summary>
public sealed class StateStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly AgentState _state;

    public StateStore(IOptions<AgentOptions> options)
    {
        _path = Path.Combine(options.Value.StateDirectory, "state.json");
        _state = File.Exists(_path)
            ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_path), AgentJson.Options) ?? new AgentState()
            : new AgentState();
    }

    public T Read<T>(Func<AgentState, T> read)
    {
        lock (_gate)
        {
            return read(_state);
        }
    }

    public void Update(Action<AgentState> change)
    {
        lock (_gate)
        {
            change(_state);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, AgentJson.Options));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
