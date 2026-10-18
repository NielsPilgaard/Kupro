using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

/// <summary>
/// Postgres on the A/B data volumes and the backup agent next to it (ADR-016, infrastructure/backup-agent/README.md).
/// Same images and volumes under `aspire run` and in the published compose; locally Silo stands in for the OVHcloud
/// buckets.
/// </summary>
internal static class Backups
{
    private const string Prefix = "kupro-";

    /// <summary>The agent's own network. Only Alloy joins it, so app containers can't reach the console.</summary>
    private const string Network = "backup";

    /// <summary>
    /// These volumes are the database (plus WAL not yet in the repo, and the agent's state). Compose would prefix their
    /// names with the project, so a renamed Dokploy app would start an empty database; external keeps the name exact,
    /// and `down -v` or Dokploy's "delete volumes" can't remove them. Created once per server (deploy runbook).
    /// </summary>
    private static readonly string[] PinnedVolumes =
        [$"{Prefix}pgdata-a", $"{Prefix}pgdata-b", $"{Prefix}pg-control", $"{Prefix}wal-receive", $"{Prefix}backup-agent-state"];

    /// <summary>Dev only: a fixed console key, so the Aspire dashboard links straight into the console.</summary>
    private const string DevConsoleKey = "dev-console";

    /// <summary>
    /// Our Postgres image (infrastructure/postgres): no backup tooling, starts on /pgdata/a or /pgdata/b as
    /// /pg-control/active says, and shares its socket with the agent.
    /// </summary>
    public static IResourceBuilder<PostgresServerResource> WithBackupVolumes(this IResourceBuilder<PostgresServerResource> postgres)
    {
        postgres
            .WithDockerfile("../../infrastructure/postgres")
            .WithVolume($"{Prefix}pgdata-a", "/pgdata/a")
            .WithVolume($"{Prefix}pgdata-b", "/pgdata/b")
            .WithVolume($"{Prefix}pg-control", "/pg-control")
            .WithVolume($"{Prefix}pg-socket", "/var/run/postgresql")
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Healthcheck = new()
                {
                    Test = ["CMD", "pg_isready", "-h", "/var/run/postgresql"],
                    Interval = "10s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "30s",
                };
                service.StopGracePeriod = "60s";
            });

        if (!postgres.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            // Small slot cap so the lost-slot case can be tried in minutes (4 GB in production), and less memory.
            postgres.WithArgs("postgres", "-c", "config_file=/etc/postgresql/postgresql.conf",
                "-c", "max_slot_wal_keep_size=256MB", "-c", "shared_buffers=128MB", "-c", "effective_cache_size=512MB");
        }

        return postgres;
    }

    /// <summary>
    /// The agent: streams WAL, takes backups, runs the weekly drill and serves the restore console. No WaitFor(postgres):
    /// the console must come up when Postgres is broken.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddBackupAgent(
        this IDistributedApplicationBuilder builder, IResourceBuilder<ContainerResource> collector)
    {
        // Fixed local port next to the app's 5180/8180; 9090 is often taken by another project's agent.
        var agent = builder.AddDockerfile("backup-agent", "../..", "infrastructure/backup-agent/Dockerfile")
            .WithHttpEndpoint(port: builder.ExecutionContext.IsPublishMode ? null : 9190, targetPort: 9090, name: "console")
            .WithVolume($"{Prefix}pg-socket", "/var/run/postgresql")
            .WithVolume($"{Prefix}pgdata-a", "/pgdata/a")
            .WithVolume($"{Prefix}pgdata-b", "/pgdata/b")
            .WithVolume($"{Prefix}pg-control", "/pg-control")
            .WithVolume($"{Prefix}wal-receive", "/wal-receive")
            .WithVolume($"{Prefix}backup-agent-state", "/var/lib/backup-agent")
            .WithEnvironment("Agent__VolumeNamePrefix", $"{Prefix}pgdata-")
            // Metrics for Grafana's backup alerts, and logs (ADR-014).
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", collector.GetEndpoint("otlp"))
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithEnvironment("OTEL_SERVICE_NAME", "backup-agent");

        if (builder.ExecutionContext.IsPublishMode)
        {
            agent
                .WithEnvironment("Agent__SshTunnelCommand", builder.AddParameter("backup-console-ssh"))
                // The pgBackRest repo: its own bucket and key, encrypted with the cipher pass (keep it in the password
                // manager, or the backups are unreadable).
                .WithEnvironment("PGBACKREST_REPO1_S3_ENDPOINT", builder.AddParameter("backup-s3-endpoint"))
                .WithEnvironment("PGBACKREST_REPO1_S3_REGION", builder.AddParameter("backup-s3-region"))
                .WithEnvironment("PGBACKREST_REPO1_S3_BUCKET", builder.AddParameter("backup-s3-bucket"))
                .WithEnvironment("PGBACKREST_REPO1_S3_KEY", builder.AddParameter("backup-s3-key"))
                .WithEnvironment("PGBACKREST_REPO1_S3_KEY_SECRET", builder.AddParameter("backup-s3-key-secret", secret: true))
                .WithEnvironment("PGBACKREST_REPO1_CIPHER_PASS", builder.AddParameter("backup-cipher-pass", secret: true))
                // Status, history and the migration list survive a lost VPS here.
                .WithEnvironment("OpsBucket__ServiceUrl", builder.AddParameter("backup-ops-url"))
                .WithEnvironment("OpsBucket__BucketName", builder.AddParameter("backup-ops-bucket"))
                .WithEnvironment("OpsBucket__AccessKey", builder.AddParameter("backup-ops-access-key"))
                .WithEnvironment("OpsBucket__SecretKey", builder.AddParameter("backup-ops-secret-key", secret: true))
                // elmah.io heartbeats: alert from outside the VPS when the agent goes quiet (ADR-016). Empty = skipped.
                .WithEnvironment("Heartbeats__ElmahIo__ApiKey", builder.AddParameter("elmahio-api-key", secret: true))
                .WithEnvironment("Heartbeats__ElmahIo__LogId", builder.AddParameter("elmahio-log-id"))
                .WithEnvironment("Heartbeats__Wal", builder.AddParameter("elmahio-heartbeat-wal"))
                .WithEnvironment("Heartbeats__Backup", builder.AddParameter("elmahio-heartbeat-backup"))
                .WithEnvironment("Heartbeats__Verify", builder.AddParameter("elmahio-heartbeat-verify"))
                .WithEnvironment("Heartbeats__Drill", builder.AddParameter("elmahio-heartbeat-drill"))
                .PublishAsDockerComposeService((_, service) =>
                {
                    // 127.0.0.1 only: reached over an SSH tunnel (scripts/backup-console.ps1).
                    service.Ports = ["127.0.0.1:${BACKUP_CONSOLE_PORT:-9090}:9090"];
                    service.Expose.Clear();
                    // The drill restores the whole cluster into tmpfs, which counts against the memory limit, so a
                    // drill can't starve Postgres.
                    service.Tmpfs = ["/drill:size=${DRILL_TMPFS_SIZE:-1g},uid=999,gid=999,mode=0700"];
                    service.Deploy = new Deploy { Resources = new Resources { Limits = new ResourceSpec { Memory = "${BACKUP_AGENT_MEMORY:-1536m}" } } };
                    service.StopGracePeriod = "30s";
                    service.DependsOn.Clear();
                    service.Networks = [Network];
                });
        }
        else
        {
            agent
                .WithUrlForEndpoint("console", url =>
                {
                    url.Url += $"/access?key={DevConsoleKey}";
                    url.DisplayText = "Backup console";
                })
                .WithEnvironment("Agent__ConsoleKey", DevConsoleKey)
                // Keycloak keeps its data in its own volume locally, not in the cluster.
                .WithEnvironment("Agent__KeycloakDatabase", "")
                // Fast cadence so RPO and status changes show within minutes (5 minutes in production).
                .WithEnvironment("Agent__WalSwitchInterval", "00:01:00")
                .WithEnvironment("Agent__StatusInterval", "00:01:00")
                .WithEnvironment("Agent__SlotWarnBytes", "134217728")
                .WithContainerRuntimeArgs("--tmpfs", "/drill:size=1g,uid=999,gid=999,mode=0700");
        }

        return agent;
    }

    /// <summary>
    /// `aspire run`: the pgBackRest repo and the ops bucket in the local Silo. A one-shot creates both buckets, since
    /// Silo has no init hooks. pgBackRest defaults to HTTPS; an http:// endpoint turns that off.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithLocalBuckets(
        this IResourceBuilder<ContainerResource> agent, IResourceBuilder<ContainerResource> storage,
        string accessKey, ParameterResource secretKey)
    {
        var s3 = storage.GetEndpoint("http");
        var buckets = agent.ApplicationBuilder.AddContainer("backup-buckets", "pgsty/silo", "RELEASE.2026-09-16T00-00-00Z")
            .WithEntrypoint("sh")
            .WithArgs("-c", ReferenceExpression.Create(
                $"until mcli alias set local http://{s3.Property(EndpointProperty.HostAndPort)} {accessKey} {secretKey}; do sleep 1; done && mcli mb --ignore-existing local/pgbackrest local/kupro-ops"))
            .WaitFor(storage);

        return agent
            .WithEnvironment("PGBACKREST_REPO1_S3_ENDPOINT", ReferenceExpression.Create($"http://{s3.Property(EndpointProperty.Host)}"))
            .WithEnvironment("PGBACKREST_REPO1_STORAGE_PORT", s3.Property(EndpointProperty.Port))
            .WithEnvironment("PGBACKREST_REPO1_S3_REGION", "us-east-1")
            .WithEnvironment("PGBACKREST_REPO1_S3_BUCKET", "pgbackrest")
            .WithEnvironment("PGBACKREST_REPO1_S3_KEY", accessKey)
            .WithEnvironment("PGBACKREST_REPO1_S3_KEY_SECRET", ReferenceExpression.Create($"{secretKey}"))
            .WithEnvironment("PGBACKREST_REPO1_CIPHER_PASS", "dev-cipher-pass")
            .WithEnvironment("OpsBucket__ServiceUrl", s3)
            .WithEnvironment("OpsBucket__AccessKey", accessKey)
            .WithEnvironment("OpsBucket__SecretKey", ReferenceExpression.Create($"{secretKey}"))
            .WaitForCompletion(buckets);
    }

    /// <summary>Pinned volume names, the agent's network, and Alloy on it so the agent can send telemetry.</summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithBackupNetworkAndVolumes(
        this IResourceBuilder<DockerComposeEnvironmentResource> compose) =>
        compose.ConfigureComposeFile(file =>
        {
            foreach (var name in PinnedVolumes)
            {
                file.Volumes[name].External = true;
                file.Volumes[name].Driver = null; // compose rejects "external" together with "driver"
            }

            file.Networks[Network] = new Network { Name = Network, Driver = "bridge" };
            file.Services["alloy"].Networks.Add(Network);
        });
}
