using Microsoft.Extensions.Options;
using Npgsql;

namespace Kupro.BackupAgent;

public sealed record CheckReport(
    List<CheckResult> Checks,
    List<TableComparison> Tables,
    int TableCount,
    long Rows,
    string? MigrationId,
    long? KeycloakUsers,
    DateTimeOffset? ReachedAt);

/// <summary>
/// The drill checks, used by the weekly drill and the restore wizard. The restored copy is
/// counted exactly (it's our own throwaway server); live is only read through pg_stat_user_tables
/// estimates and the two tables the agent may read. Results are names and counts, never row content.
/// </summary>
public sealed class DrillChecks(IOptions<AgentOptions> options, LivePostgres live)
{
    private readonly AgentOptions _options = options.Value;

    public async Task<CheckReport> RunAsync(TempPostgres restored, DateTimeOffset? target, Action<string> log, CancellationToken cancellationToken)
    {
        var checks = new List<CheckResult>();
        var tables = new List<TableComparison>();
        var liveApp = await TryAsync(() => live.TableEstimatesAsync(_options.AppDatabase, cancellationToken), log, "Live database down; skipping comparisons with live");
        var hasKeycloak = !string.IsNullOrWhiteSpace(_options.KeycloakDatabase);
        var liveKeycloak = hasKeycloak ? await TryAsync(() => live.TableEstimatesAsync(_options.KeycloakDatabase, cancellationToken), log, null) : null;
        var liveMigration = (await TryAsync(() => live.MigrationsAsync(cancellationToken), log,
            "Can't read live __EFMigrationsHistory (GRANT SELECT to backup_agent missing?)"))?.LastOrDefault()?.MigrationId;

        string? migrationId = null;
        long rows = 0;
        var tableCount = 0;
        if (!await restored.DatabaseExistsAsync(_options.AppDatabase, cancellationToken))
        {
            checks.Add(new CheckResult("App database", false, $"{_options.AppDatabase} missing"));
        }
        else
        {
            await using var app = restored.Connect(_options.AppDatabase);
            await app.OpenAsync(cancellationToken);
            var counts = await CountTablesAsync(app, cancellationToken);
            tableCount = counts.Count;
            rows = counts.Values.Sum();
            migrationId = await ScalarAsync<string>(app, $"""SELECT max({LivePostgres.MigrationIdColumn}) FROM "__EFMigrationsHistory" h""", cancellationToken);
            foreach (var (table, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                tables.Add(new TableComparison(ShortName(table), count, liveApp?.GetValueOrDefault(table)));
            }

            var liveIsNewer = liveMigration is not null && migrationId is not null && string.CompareOrdinal(liveMigration, migrationId) > 0;
            checks.Add(TablesCheck(counts, liveApp, liveIsNewer));
            checks.Add(liveMigration is null
                ? new CheckResult("Migration", null, $"{migrationId ?? "none"} (live unreadable)")
                : new CheckResult("Migration", migrationId == liveMigration || liveIsNewer,
                    migrationId == liveMigration ? $"{migrationId} (= live)" : $"{migrationId ?? "none"}, live {liveMigration}"));
            checks.Add(RowsCheck(counts, liveApp));
            checks.Add(WorkspacesCheck(counts, liveApp));
            checks.Add(await TenantIsolationCheckAsync(app, cancellationToken));
        }

        var reachedAt = await ReachedAtAsync(restored, cancellationToken);
        if (target is { } wanted)
        {
            var tolerance = _options.WalSwitchInterval * 2 + TimeSpan.FromMinutes(1);
            checks.Add(reachedAt is { } reached
                ? new CheckResult("Point reached", reached <= wanted && reached >= wanted - tolerance,
                    $"{Fmt.DateTimeSeconds(reached)} (target {Fmt.DateTimeSeconds(wanted)})")
                : new CheckResult("Point reached", null, "No agent heartbeat in the restore"));
        }

        // No Keycloak database under aspire run: Keycloak keeps its data in its own volume there.
        long? keycloakUsers = null;
        if (!hasKeycloak)
        {
            checks.Add(new CheckResult("Keycloak", null, "Not in this cluster"));
        }
        else if (await restored.DatabaseExistsAsync(_options.KeycloakDatabase, cancellationToken))
        {
            await using var keycloak = restored.Connect(_options.KeycloakDatabase);
            await keycloak.OpenAsync(cancellationToken);
            keycloakUsers = await KeycloakChecksAsync(keycloak, liveKeycloak, checks, cancellationToken);
        }
        else
        {
            checks.Add(new CheckResult("Keycloak", false, $"{_options.KeycloakDatabase} missing"));
        }

        foreach (var check in checks)
        {
            log($"{(check.Ok switch { true => "OK  ", false => "FAIL", null => "--  " })} {check.Name}: {check.Detail}");
        }

        return new CheckReport(checks, tables, tableCount, rows, migrationId, keycloakUsers, reachedAt);
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> read, Action<string> log, string? message)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            if (message is not null)
            {
                log($"{message} ({ex.Message})");
            }

            return default;
        }
    }

    private static string ShortName(string table) => table.StartsWith("public.", StringComparison.Ordinal) ? table[7..] : table;

    private static async Task<Dictionary<string, long>> CountTablesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var names = new List<(string Schema, string Table)>();
        await using (var command = new NpgsqlCommand("""
            SELECT table_schema, table_name FROM information_schema.tables
            WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var counts = new Dictionary<string, long>();
        foreach (var (schema, table) in names)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {Quote(schema)}.{Quote(table)}", connection);
            counts[$"{schema}.{table}"] = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        return counts;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is DBNull or null ? default : (T)value;
        }
        catch (PostgresException)
        {
            return default;
        }
    }

    private static CheckResult TablesCheck(Dictionary<string, long> restored, Dictionary<string, long>? live, bool liveIsNewer)
    {
        if (live is null)
        {
            return new CheckResult("Tables", null, $"{restored.Count} (live unreadable)");
        }

        var missing = live.Keys.Where(t => !restored.ContainsKey(t)).Select(ShortName).ToList();
        if (missing.Count == 0)
        {
            return new CheckResult("Tables", true, $"All {live.Count} live tables present");
        }

        return new CheckResult("Tables", liveIsNewer,
            $"{missing.Count} live tables missing{(liveIsNewer ? " (from later migrations)" : "")}: {string.Join(", ", missing.Take(8))}");
    }

    /// <summary>Catches empty or partial restores: each table live has rows in must have at least half of them.</summary>
    private static CheckResult RowsCheck(Dictionary<string, long> restored, Dictionary<string, long>? live)
    {
        if (live is null)
        {
            return new CheckResult("Rows", null, $"{Fmt.Number(restored.Values.Sum())} (live unreadable)");
        }

        // pg_stat estimates are rough on small tables, so only tables with some volume count.
        var tooFew = live
            .Where(l => l.Value >= 20 && restored.TryGetValue(l.Key, out var count) && count < l.Value / 2)
            .Select(l => $"{ShortName(l.Key)} {restored[l.Key]}/{l.Value}")
            .ToList();
        return tooFew.Count == 0
            ? new CheckResult("Rows", true, $"{Fmt.Number(restored.Values.Sum())}, no table under half of live")
            : new CheckResult("Rows", false, $"Under half of live: {string.Join(", ", tooFew.Take(8))}");
    }

    private static CheckResult WorkspacesCheck(Dictionary<string, long> restored, Dictionary<string, long>? live)
    {
        var count = restored.GetValueOrDefault("public.workspaces");
        if (live?.GetValueOrDefault("public.workspaces") is not { } liveCount || liveCount == 0)
        {
            return new CheckResult("Workspaces", null, $"{count}");
        }

        return new CheckResult("Workspaces", Math.Abs(count - liveCount) <= 2, $"{count}, live ~{liveCount} (±2)");
    }

    /// <summary>No row in a tenant-owned table (ADR-009) may point at a workspace that doesn't exist.</summary>
    private static async Task<CheckResult> TenantIsolationCheckAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<(string Schema, string Table)>();
        await using (var command = new NpgsqlCommand("""
            SELECT table_schema, table_name FROM information_schema.columns
            WHERE column_name = 'workspace_id' AND table_schema NOT IN ('pg_catalog', 'information_schema')
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var orphans = new List<string>();
        foreach (var (schema, table) in tables)
        {
            await using var command = new NpgsqlCommand(
                $"""SELECT count(*) FROM {Quote(schema)}.{Quote(table)} t WHERE NOT EXISTS (SELECT 1 FROM workspaces w WHERE w.id = t.workspace_id)""",
                connection);
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (count > 0)
            {
                orphans.Add($"{table} ({count})");
            }
        }

        return orphans.Count == 0
            ? new CheckResult("Tenant isolation", true, $"No orphan rows in {tables.Count} tables")
            : new CheckResult("Tenant isolation", false, $"Rows without a workspace: {string.Join(", ", orphans)}");
    }

    /// <summary>The agent's last heartbeat commit in the restore: how far recovery really got.</summary>
    private static async Task<DateTimeOffset?> ReachedAtAsync(TempPostgres restored, CancellationToken cancellationToken)
    {
        await using var connection = restored.Connect("postgres");
        await connection.OpenAsync(cancellationToken);
        var at = await ScalarAsync<DateTime>(connection, "SELECT max(at) FROM backup_agent_heartbeat", cancellationToken);
        return at == default ? null : new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
    }

    private async Task<long> KeycloakChecksAsync(NpgsqlConnection keycloak, Dictionary<string, long>? live, List<CheckResult> checks, CancellationToken cancellationToken)
    {
        await using var realm = new NpgsqlCommand("SELECT count(*) FROM realm WHERE name = @name", keycloak);
        realm.Parameters.AddWithValue("name", _options.KeycloakRealm);
        var realmCount = await SafeCountAsync(realm, cancellationToken);
        checks.Add(new CheckResult("Keycloak realm", realmCount > 0, realmCount > 0 ? _options.KeycloakRealm : $"{_options.KeycloakRealm} missing"));

        await using var users = new NpgsqlCommand("SELECT count(*) FROM user_entity", keycloak);
        var userCount = await SafeCountAsync(users, cancellationToken);
        if (live?.GetValueOrDefault("public.user_entity") is { } liveUsers and > 0)
        {
            var allowed = Math.Max(2, liveUsers * 5 / 100);
            checks.Add(new CheckResult("Keycloak users", Math.Abs(userCount - liveUsers) <= allowed, $"{userCount}, live ~{liveUsers} (±{allowed})"));
        }
        else
        {
            checks.Add(new CheckResult("Keycloak users", null, $"{userCount}"));
        }

        await using var credentials = new NpgsqlCommand("SELECT count(*) FROM credential", keycloak);
        var credentialCount = await SafeCountAsync(credentials, cancellationToken);
        checks.Add(new CheckResult("Keycloak credentials", userCount == 0 || credentialCount > 0, $"{credentialCount}"));
        return userCount;
    }

    private static async Task<long> SafeCountAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException)
        {
            return 0;
        }
    }
}
