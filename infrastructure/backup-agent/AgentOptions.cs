namespace Flowstate.BackupAgent;

/// <summary>
/// Everything the agent needs to find Postgres, its volumes and its schedule. Bound from the
/// "Agent" section, so compose sets e.g. <c>Agent__SshTunnelCommand</c>. pgBackRest's own settings
/// (repo, S3 keys, cipher pass) come from PGBACKREST_* env vars, which child processes inherit.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string Stanza { get; init; } = "main";

    /// <summary>The Postgres socket directory, shared with the postgres container through a volume.</summary>
    public string SocketDirectory { get; init; } = "/var/run/postgresql";

    /// <summary>
    /// Both data volumes are mounted here as <c>a</c> and <c>b</c>, at the same paths as in Postgres,
    /// because pgBackRest refuses a pg1-path that differs from the server's data_directory.
    /// <see cref="DataVolumes"/> decides which one is live.
    /// </summary>
    public string DataRoot { get; init; } = "/pgdata";

    /// <summary>Volume shared with Postgres. Its file <c>active</c> (a or b) picks the data volume at Postgres' next start.</summary>
    public string ControlDirectory { get; init; } = "/pg-control";

    /// <summary>Compose volume names are this plus the slot letter. Shown in the console and typed to confirm.</summary>
    public string VolumeNamePrefix { get; init; } = "pgdata-";

    /// <summary>Persistent agent volume: state.json, the console key and local copies of the ops bucket files.</summary>
    public string StateDirectory { get; init; } = "/var/lib/backup-agent";

    /// <summary>tmpfs for the weekly drill. Nothing restored there survives the drill.</summary>
    public string DrillDirectory { get; init; } = "/drill";

    public string AppDatabase { get; init; } = "flowstate";

    /// <summary>
    /// Keycloak's database in the same cluster (production). Empty under <c>aspire run</c>, where Keycloak keeps its
    /// data in its own volume: the drill and restore checks then skip Keycloak.
    /// </summary>
    public string KeycloakDatabase { get; init; } = "keycloak";

    public string KeycloakRealm { get; init; } = "flowstate";

    public string DatabaseUser { get; init; } = "backup_agent";
    public string SlotName { get; init; } = "agent";

    /// <summary>
    /// pg_receivewal's directory, on its own named volume (ADR-016). Once a segment is flushed
    /// here the slot moves on and Postgres drops its copy, so this is the only copy until
    /// archive-push succeeds. Segments are deleted only after that.
    /// </summary>
    public string WalReceiveDirectory { get; init; } = "/wal-receive";

    /// <summary>
    /// Unpushed WAL in <see cref="WalReceiveDirectory"/> before the agent pauses pg_receivewal.
    /// Postgres then holds WAL for the slot, up to max_slot_wal_keep_size. Both caps bound the disk use.
    /// </summary>
    public long WalSpoolMaxBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Retained WAL in Postgres that raises a warning before the slot is lost.</summary>
    public long SlotWarnBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>The DPA promise (docs/design/public-pages/dpa.html). Keep in step with repo1-retention-full in pgbackrest.conf.</summary>
    public int RetentionDays { get; init; } = 35;

    public string TimeZone { get; init; } = "Europe/Copenhagen";
    public int FullBackupHour { get; init; } = 2;
    public DayOfWeek VerifyDay { get; init; } = DayOfWeek.Wednesday;
    public int VerifyHour { get; init; } = 3;
    public DayOfWeek DrillDay { get; init; } = DayOfWeek.Sunday;
    public int DrillHour { get; init; } = 4;

    /// <summary>How often the agent commits a heartbeat row and switches WAL, so the 15-minute RPO holds on quiet nights.</summary>
    public TimeSpan WalSwitchInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The RPO. Data secured longer ago than this makes the agent unhealthy.</summary>
    public TimeSpan MaxDataLoss { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Shown in the console, e.g. <c>ssh -L 9090:127.0.0.1:9090 ubuntu@vps</c>.</summary>
    public string? SshTunnelCommand { get; init; }

    /// <summary>
    /// Dev only: a fixed console key, so the Aspire dashboard can link straight into the console.
    /// Leave unset on prod, where the key is random and only readable with <c>docker exec</c>.
    /// </summary>
    public string? ConsoleKey { get; init; }

    public string WalSpoolDirectory => WalReceiveDirectory;

    /// <summary>Finished segments set aside when streaming restarts from a new slot. pg_receivewal ignores subdirectories.</summary>
    public string WalOutboxDirectory => Path.Combine(WalReceiveDirectory, "outbox");
}

/// <summary>The S3 ops bucket (ADR-016). Not the backup repo and not the files bucket.</summary>
public sealed class OpsBucketOptions
{
    public const string SectionName = "OpsBucket";

    public string ServiceUrl { get; init; } = "";
    public string AccessKey { get; init; } = "";
    public string SecretKey { get; init; } = "";
    public string BucketName { get; init; } = "flowstate-ops";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

public enum HeartbeatProvider
{
    ElmahIo,
    Ping,
}

/// <summary>
/// Heartbeats (ADR-016). <see cref="Wal"/>, <see cref="Backup"/>, <see cref="Drill"/> and
/// <see cref="Verify"/> are each heartbeat's target: the heartbeat id for elmah.io, the ping URL for
/// <see cref="HeartbeatProvider.Ping"/>. A heartbeat without a target is skipped.
/// </summary>
public sealed class HeartbeatOptions
{
    public const string SectionName = "Heartbeats";

    public HeartbeatProvider Provider { get; init; } = HeartbeatProvider.ElmahIo;

    public string Wal { get; init; } = "";
    public string Backup { get; init; } = "";
    public string Drill { get; init; } = "";
    public string Verify { get; init; } = "";

    public ElmahIoHeartbeatOptions ElmahIo { get; init; } = new();
    public PingHeartbeatOptions Ping { get; init; } = new();
}

public sealed class ElmahIoHeartbeatOptions
{
    /// <summary>Overridable so a local stack can point at a mock.</summary>
    public string BaseUrl { get; init; } = "https://api.elmah.io";

    public string ApiKey { get; init; } = "";
    public string LogId { get; init; } = "";
}

public sealed class PingHeartbeatOptions
{
    /// <summary>Appended to the ping URL to report a failure. <c>/fail</c> suits Healthchecks.io and Better Stack; Cronitor wants <c>?state=fail</c>.</summary>
    public string FailSuffix { get; init; } = "/fail";
}
