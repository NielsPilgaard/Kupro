using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Kupro.BackupAgent;

public sealed record ServerInfo(int VersionNum, long SystemIdentifier, int Timeline, NpgsqlLogSequenceNumber CurrentLsn, long SlotKeepBytes, string DataDirectory);

public sealed record SlotInfo(string WalStatus, NpgsqlLogSequenceNumber? RestartLsn, bool Active, long? RetainedBytes);

public sealed record MigrationRow(string MigrationId, DateTimeOffset? AppliedAt);

/// <summary>
/// The live cluster, over the shared socket as backup_agent (peer auth, no password). That role can
/// stream WAL, run backups, commit its own heartbeat row and read the migration history; it reads no
/// workspace data. Row counts come from pg_stat_user_tables, which needs no table privileges.
/// </summary>
public sealed class LivePostgres(IOptions<AgentOptions> options)
{
    private readonly AgentOptions _options = options.Value;

    public string ConnectionString(string database) => new NpgsqlConnectionStringBuilder
    {
        Host = _options.SocketDirectory,
        Username = _options.DatabaseUser,
        Database = database,
        Timeout = 5,
        CommandTimeout = 30,
        Pooling = false,
    }.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString(database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task<ServerInfo> InfoAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync("postgres", cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT current_setting('server_version_num')::int,
                   (SELECT system_identifier FROM pg_control_system()),
                   (SELECT timeline_id FROM pg_control_checkpoint()),
                   pg_current_wal_lsn(),
                   pg_size_bytes(current_setting('max_slot_wal_keep_size')),
                   current_setting('data_directory')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ServerInfo(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<NpgsqlLogSequenceNumber>(3), reader.GetInt64(4), reader.GetString(5));
    }

    public async Task<SlotInfo?> SlotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync("postgres", cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(wal_status, 'unknown'), restart_lsn, active,
                   pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)::bigint
            FROM pg_replication_slots WHERE slot_name = @slot
            """, connection);
        command.Parameters.AddWithValue("slot", _options.SlotName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SlotInfo(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<NpgsqlLogSequenceNumber>(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    /// <summary>Drops the slot if it exists (it must not be active) and creates it, reserving WAL from now.</summary>
    public async Task RecreateSlotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync("postgres", cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = @slot;
            SELECT pg_create_physical_replication_slot(@slot, true);
            """, connection);
        command.Parameters.AddWithValue("slot", _options.SlotName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Commits the agent's heartbeat row, then switches WAL. The commit gives every WAL segment a
    /// commit timestamp, so a time target is always reachable and a restore can tell how far it got.
    /// Returns the switch position and the committed time.
    /// </summary>
    public async Task<(NpgsqlLogSequenceNumber Lsn, DateTimeOffset At)> MarkAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync("postgres", cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO backup_agent_heartbeat (id, at) VALUES (1, now())
            ON CONFLICT (id) DO UPDATE SET at = excluded.at
            RETURNING at;
            """, connection);
        var at = (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
        await using var switchCommand = new NpgsqlCommand("SELECT pg_switch_wal()", connection);
        var lsn = (NpgsqlLogSequenceNumber)(await switchCommand.ExecuteScalarAsync(cancellationToken))!;
        return (lsn, new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)));
    }

    /// <summary>Estimated live rows per table (pg_stat_user_tables), keyed "schema.table".</summary>
    public async Task<Dictionary<string, long>> TableEstimatesAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(database, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT schemaname, relname, n_live_tup FROM pg_stat_user_tables", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result[$"{reader.GetString(0)}.{reader.GetString(1)}"] = reader.GetInt64(2);
        }

        return result;
    }

    /// <summary>
    /// The migration id of a row <c>h</c> of <c>__EFMigrationsHistory</c>. EFCore.NamingConventions snake-cases the
    /// history table's columns too, so the column is <c>migration_id</c> here and <c>MigrationId</c> in a plain EF app.
    /// </summary>
    public const string MigrationIdColumn = "coalesce(to_jsonb(h) ->> 'migration_id', to_jsonb(h) ->> 'MigrationId')";

    /// <summary>Migrations with their commit time (track_commit_timestamp), for "restore to just before migration X".</summary>
    public async Task<List<MigrationRow>> MigrationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(_options.AppDatabase, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {MigrationIdColumn},
                   CASE WHEN current_setting('track_commit_timestamp') = 'on' THEN pg_xact_commit_timestamp(h.xmin) END
            FROM "__EFMigrationsHistory" h ORDER BY 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MigrationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MigrationRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return result;
    }
}

/// <summary>
/// A throwaway Postgres server inside the agent container, on a restored data directory (drill tmpfs
/// or the spare volume). Socket only, in a private directory, trust auth, archiving off: it must
/// never push WAL to the repo. hot_standby is off, so start returns once recovery has promoted.
/// </summary>
public sealed class TempPostgres : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _socketDirectory;
    private readonly int _port;
    private readonly Action<string> _log;
    private bool _running;

    private TempPostgres(string dataDirectory, string socketDirectory, int port, Action<string> log)
    {
        _dataDirectory = dataDirectory;
        _socketDirectory = socketDirectory;
        _port = port;
        _log = log;
    }

    public static async Task<TempPostgres> StartAsync(string dataDirectory, string name, int port, Action<string> log, CancellationToken cancellationToken)
    {
        var socketDirectory = Path.Combine(Path.GetTempPath(), $"{name}-sock");
        Directory.CreateDirectory(socketDirectory);
        File.SetUnixFileMode(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var hbaFile = Path.Combine(Path.GetTempPath(), $"{name}-hba.conf");
        await File.WriteAllTextAsync(hbaFile, "local all all trust\n", cancellationToken);
        var logFile = Path.Combine(Path.GetTempPath(), $"{name}.log");

        var server = new TempPostgres(dataDirectory, socketDirectory, port, log);
        var settings = string.Join(' ',
            "-c listen_addresses=''",
            $"-c unix_socket_directories='{socketDirectory}'",
            $"-p {port}",
            $"-c hba_file='{Path.GetFullPath(hbaFile)}'",
            "-c archive_mode=off",
            "-c hot_standby=off",
            "-c shared_buffers=128MB",
            "-c max_slot_wal_keep_size=-1",
            "-c track_commit_timestamp=on");

        var result = await Shell.RunAsync("pg_ctl", ["-D", dataDirectory, "-l", logFile, "-o", settings, "-w", "-t", "600", "start"], log, cancellationToken);
        server._running = result.Ok;
        if (!result.Ok || !await server.WaitUntilPromotedAsync(cancellationToken))
        {
            foreach (var line in ReadLogLines(logFile).TakeLast(15))
            {
                log(line);
            }

            await server.StopAsync();
            throw new InvalidOperationException("The temporary Postgres didn't come up as primary. See the log above.");
        }

        foreach (var line in ReadLogLines(logFile).Where(IsRecoveryLine))
        {
            log(line);
        }

        return server;
    }

    /// <summary>
    /// pg_ctl returns while WAL is still being replayed, and with hot_standby off nobody can connect
    /// until recovery promotes. Waits for that, or returns false if the server died on the way.
    /// </summary>
    private async Task<bool> WaitUntilPromotedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromHours(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = Connect("postgres");
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("SELECT pg_is_in_recovery()", connection);
                if (await command.ExecuteScalarAsync(cancellationToken) is false)
                {
                    return true;
                }
            }
            catch (NpgsqlException)
            {
                // 57P03 "not accepting connections" while recovery runs.
            }

            var status = await Shell.RunAsync("pg_ctl", ["-D", _dataDirectory, "status"], null, cancellationToken);
            if (!status.Ok)
            {
                _running = false;
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private static string[] ReadLogLines(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    private static bool IsRecoveryLine(string line) =>
        line.Contains("recovery stopping", StringComparison.Ordinal)
        || line.Contains("selected new timeline", StringComparison.Ordinal)
        || line.Contains("archive recovery complete", StringComparison.Ordinal)
        || line.Contains("consistent recovery state", StringComparison.Ordinal)
        || (line.Contains("FATAL", StringComparison.Ordinal) && !line.Contains("not accepting connections", StringComparison.Ordinal));

    public NpgsqlConnection Connect(string database) => new(new NpgsqlConnectionStringBuilder
    {
        Host = _socketDirectory,
        Port = _port,
        Username = "postgres",
        Database = database,
        Pooling = false,
        CommandTimeout = 600,
    }.ConnectionString);

    public async Task<bool> DatabaseExistsAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = Connect("postgres");
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", database);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Removes the recovery settings pgBackRest wrote, so the volume starts as a plain primary when it goes live.</summary>
    public async Task ClearRecoverySettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = Connect("postgres");
        await connection.OpenAsync(cancellationToken);
        foreach (var setting in new[] { "restore_command", "recovery_target_time", "recovery_target", "recovery_target_action", "recovery_target_timeline", "recovery_target_inclusive" })
        {
            await using var command = new NpgsqlCommand($"ALTER SYSTEM RESET {setting}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        var result = await Shell.RunAsync("pg_ctl", ["-D", _dataDirectory, "-m", "fast", "-w", "-t", "300", "stop"], _log);
        if (!result.Ok)
        {
            await Shell.RunAsync("pg_ctl", ["-D", _dataDirectory, "-m", "immediate", "-w", "stop"], _log);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
