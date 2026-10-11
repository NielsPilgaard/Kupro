// Single source of truth for the service topology (ADR-012).
// `aspire run` → local dev stack; `aspire publish` → deploy/docker-compose.yaml.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

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
    .WaitFor(database);

var api = builder.AddProject<Projects.Flowstate_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

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

    // Prod SPA = web/Dockerfile (bun build → nginx), build context = repo root (ADR-012 IMP-002).
    builder.AddDockerfile("web", "../..", "web/Dockerfile")
        .WithBuildArg("VITE_KEYCLOAK_URL", keycloakPublicUrl)
        .WithEnvironment("API_UPSTREAM", api.GetEndpoint("http"))
        .WithHttpEndpoint(targetPort: 80)
        .WithExternalHttpEndpoints()
        .WaitFor(api);
}
else
{
    // Advertised to clients (CLI device login) via GET /api/v1/status.
    api.WithEnvironment("Auth__PublicAuthority",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/flowstate"));
    // MCP resource URL = token audience for MCP clients using resource indicators (ADR-005 IMP-001).
    api.WithEnvironment("Api__PublicUrl", api.GetEndpoint("http"));

    builder.AddViteApp("web", "../../web")
        .WithBun()
        // Fixed port: the dev realm allows exactly this origin and redirect URI.
        .WithEndpoint("http", endpoint => endpoint.Port = 5180)
        .WithEnvironment("VITE_KEYCLOAK_URL", keycloak.GetEndpoint("http"))
        .WithReference(api)
        .WaitFor(api);
}

builder.Build().Run();
