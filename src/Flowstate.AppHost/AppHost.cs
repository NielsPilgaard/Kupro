// Single source of truth for the service topology (ADR-012).
// `aspire run` → local dev stack; `aspire publish` → deploy/docker-compose.yaml.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose")
    // Production telemetry goes to the self-hosted Grafana stack (ADR-014), not to an Aspire dashboard container.
    .WithDashboard(false)
    .WithRepoRelativeBindMounts();

var otelCollector = builder.AddObservability();

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    // Postgres 18 images keep PGDATA under /var/lib/postgresql/<major>/, so mount the parent directory.
    .WithVolume("flowstate-postgres-data", "/var/lib/postgresql")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("flowstate");

// Fixed host port so dev tokens keep a stable issuer; 8180 avoids the common 8080 clash.
var keycloak = builder.AddKeycloak("keycloak", port: 8180)
    .WithDataVolume("flowstate-keycloak-data")
    .WithRealmImport("./Realms")
    // MCP authorization needs these preview features (ADR-005 IMP-001).
    .WithEnvironment("KC_FEATURES", "resource-indicators,cimd")
    .WithLifetime(ContainerLifetime.Persistent);

// Same API image in `migrate` mode: applies EF migrations, then exits (ADR-012 IMP-003).
// No launch profile, so it does not claim the API's port.
var migrations = builder.AddProject<Projects.Flowstate_Api>("migrations", launchProfileName: null)
    .WithArgs("migrate")
    .WithReference(database)
    .WaitFor(database)
    .WithTelemetryTo(otelCollector);

var api = builder.AddProject<Projects.Flowstate_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithTelemetryTo(otelCollector);

if (builder.ExecutionContext.IsPublishMode)
{
    // Public URL users' browsers reach Keycloak on (e.g. https://auth.flowstate.dk); also baked into the SPA.
    var keycloakPublicUrl = builder.AddParameter("keycloak-public-url");
    api.WithEnvironment("Auth__PublicAuthority", ReferenceExpression.Create($"{keycloakPublicUrl}/realms/flowstate"));

    // Keycloak runs in production mode (`start`) behind the reverse proxy, which terminates TLS.
    keycloak
        .WithEnvironment("KC_HOSTNAME", keycloakPublicUrl)
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
        // Tokens carry the public issuer, but the API fetches keys over the internal network.
        .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true");

    // MCP resource URL = token audience for MCP clients using resource indicators (ADR-005 IMP-001), e.g. https://flowstate.dk.
    api.WithEnvironment("Api__PublicUrl", builder.AddParameter("api-public-url"));

    // Attachments in OVHcloud Object Storage (ADR-004). Bucket and CORS rule are set up once by hand (deploy runbook).
    api.WithEnvironment("Storage__ServiceUrl", builder.AddParameter("storage-url"))
        .WithEnvironment("Storage__Region", builder.AddParameter("storage-region"))
        .WithEnvironment("Storage__Bucket", builder.AddParameter("storage-bucket"))
        .WithEnvironment("Storage__AccessKey", builder.AddParameter("storage-access-key"))
        .WithEnvironment("Storage__SecretKey", builder.AddParameter("storage-secret-key", secret: true));

    // Docker probes /alive every 10 s. Besides restart/health status, Grafana's "API down" alert counts these
    // requests (ADR-014), so without a healthcheck the API would look down. The runtime image has bash, no curl.
    // No `ports:`: Traefik reaches services over the Docker network, so a published host port would only bypass it.
    api.PublishAsDockerComposeService((_, service) =>
    {
        service.Ports.Clear();
        service.Healthcheck = new()
        {
            Test =
            [
                "CMD", "bash", "-c",
                "exec 3<>/dev/tcp/localhost/$${HTTP_PORTS} && printf 'GET /alive HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '",
            ],
            Interval = "10s",
            Timeout = "5s",
            Retries = 6,
            StartPeriod = "30s",
        };
    });

    // Prod SPA = web/Dockerfile (bun build → nginx), build context = repo root (ADR-012 IMP-002).
    builder.AddDockerfile("web", "../..", "web/Dockerfile")
        .WithBuildArg("VITE_KEYCLOAK_URL", keycloakPublicUrl)
        .WithEnvironment("API_UPSTREAM", api.GetEndpoint("http"))
        .WithHttpEndpoint(targetPort: 80)
        .WithExternalHttpEndpoints()
        .PublishAsDockerComposeService((_, service) => service.Ports.Clear())
        .WaitFor(api);
}
else
{
    // Advertised to clients (CLI device login) via GET /api/v1/status.
    api.WithEnvironment("Auth__PublicAuthority",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/flowstate"));
    // MCP resource URL = token audience for MCP clients using resource indicators (ADR-005 IMP-001).
    api.WithEnvironment("Api__PublicUrl", api.GetEndpoint("http"));

    // Attachments (task 008): Silo (maintained MinIO fork) as the local S3 endpoint; production uses OVHcloud Object Storage (ADR-004).
    // Fixed port and CORS for the SPA origin: browsers upload straight to it with presigned URLs.
    var storageSecret = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, "storage-secret", special: false);
    var storage = builder.AddContainer("storage", "pgsty/silo", "RELEASE.2026-09-16T00-00-00Z")
        .WithArgs("server", "/data")
        .WithVolume("flowstate-storage-data", "/data")
        .WithEnvironment("MINIO_ROOT_USER", "flowstate")
        .WithEnvironment("MINIO_ROOT_PASSWORD", ReferenceExpression.Create($"{storageSecret}"))
        .WithEnvironment("MINIO_API_CORS_ALLOW_ORIGIN", "http://localhost:5180")
        .WithHttpEndpoint(port: 8190, targetPort: 9000)
        .WithHttpHealthCheck("/minio/health/live");
    api.WithEnvironment("Storage__ServiceUrl", storage.GetEndpoint("http"))
        .WithEnvironment("Storage__AccessKey", "flowstate")
        .WithEnvironment("Storage__SecretKey", ReferenceExpression.Create($"{storageSecret}"))
        .WithEnvironment("Storage__CreateBucket", "true")
        .WaitFor(storage);

    builder.AddViteApp("web", "../../web")
        .WithBun()
        // Fixed port: the dev realm allows exactly this origin and redirect URI.
        .WithEndpoint("http", endpoint => endpoint.Port = 5180)
        .WithEnvironment("VITE_KEYCLOAK_URL", keycloak.GetEndpoint("http"))
        .WithReference(api)
        .WaitFor(api);
}

builder.Build().Run();
