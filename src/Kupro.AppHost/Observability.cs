using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

// Self-hosted observability (ADR-014): apps → Alloy → Loki (logs), Tempo (traces), Prometheus (metrics),
// with Grafana on top. The same containers run under `aspire run` and in the published compose; their config lives
// in infrastructure/observability/. Only Grafana is reachable from outside.
internal static class Observability
{
    private const string ConfigDir = "../../infrastructure/observability";

    /// <returns>The collector (apps send telemetry to it) and Grafana.</returns>
    public static (IResourceBuilder<ContainerResource> Collector, IResourceBuilder<ContainerResource> Grafana) AddObservability(
        this IDistributedApplicationBuilder builder)
    {
        var loki = builder.AddContainer("loki", "grafana/loki", "3.7.8")
            .WithBindMount($"{ConfigDir}/loki.yaml", "/etc/loki/kupro.yaml", isReadOnly: true)
            .WithArgs("-config.file=/etc/loki/kupro.yaml")
            .WithVolume("kupro-loki-data", "/loki")
            .WithHttpEndpoint(targetPort: 3100, name: "http")
            .WithHttpHealthCheck("/ready")
            .WithMemoryLimit("768m");

        var tempo = builder.AddContainer("tempo", "grafana/tempo", "3.1.0")
            .WithBindMount($"{ConfigDir}/tempo.yaml", "/etc/tempo/kupro.yaml", isReadOnly: true)
            .WithArgs("-target=all", "-config.file=/etc/tempo/kupro.yaml")
            .WithVolume("kupro-tempo-data", "/var/tempo")
            .WithHttpEndpoint(targetPort: 3200, name: "http")
            .WithHttpEndpoint(targetPort: 4318, name: "otlp")
            .WithHttpHealthCheck("/ready")
            .WithMemoryLimit("768m");

        var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.15.0")
            .WithBindMount($"{ConfigDir}/prometheus.yaml", "/etc/prometheus/kupro.yaml", isReadOnly: true)
            .WithArgs(
                "--config.file=/etc/prometheus/kupro.yaml",
                "--storage.tsdb.path=/prometheus",
                "--storage.tsdb.retention.time=30d",
                "--web.enable-otlp-receiver",
                // Alloy remote-writes the scraped host, Postgres and Keycloak metrics.
                "--web.enable-remote-write-receiver")
            .WithVolume("kupro-prometheus-data", "/prometheus")
            .WithHttpEndpoint(targetPort: 9090, name: "http")
            .WithHttpHealthCheck("/-/ready")
            .WithMemoryLimit("512m");

        // Locally the collector also forwards a copy to the Aspire dashboard, so both views keep working.
        var dashboardUrl = builder.ExecutionContext.IsPublishMode ? null : builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];
        var collector = builder.AddContainer("alloy", "grafana/alloy", "v1.20.1")
            .WithBindMount($"{ConfigDir}/alloy/kupro.alloy", "/etc/alloy/kupro/kupro.alloy", isReadOnly: true)
            .WithBindMount($"{ConfigDir}/alloy/{(dashboardUrl is null ? "export.alloy" : "export.dev.alloy")}",
                "/etc/alloy/kupro/export.alloy", isReadOnly: true)
            // --disable-reporting: no usage stats to Grafana Labs (ADR-006).
            .WithArgs("run", "/etc/alloy/kupro", "--server.http.listen-addr=0.0.0.0:12345",
                "--storage.path=/var/lib/alloy/data", "--disable-reporting")
            .WithEnvironment("LOKI_URL", loki.GetEndpoint("http"))
            .WithEnvironment("PROMETHEUS_URL", prometheus.GetEndpoint("http"))
            .WithEnvironment("TEMPO_OTLP_URL", tempo.GetEndpoint("otlp"))
            .WithHttpEndpoint(targetPort: 4318, name: "otlp")
            // Alloy's UI: the live pipeline graph and each component's health.
            .WithHttpEndpoint(targetPort: 12345, name: "http")
            .WithHttpHealthCheck("/-/ready", endpointName: "http")
            .WithMemoryLimit("384m");

        if (dashboardUrl is not null)
        {
            collector
                .WithEnvironment("ASPIRE_DASHBOARD_OTLP_URL", new HostUrl(dashboardUrl))
                .WithEnvironment("ASPIRE_DASHBOARD_OTLP_API_KEY", builder.Configuration["AppHost:OtlpApiKey"] ?? "");
        }

        var grafana = builder.AddContainer("grafana", "grafana/grafana", "13.2.3")
            .WithBindMount($"{ConfigDir}/grafana/provisioning", "/etc/grafana/provisioning", isReadOnly: true)
            .WithBindMount($"{ConfigDir}/grafana/dashboards", "/etc/grafana/dashboards", isReadOnly: true)
            .WithVolume("kupro-grafana-data", "/var/lib/grafana")
            .WithEnvironment("PROMETHEUS_URL", prometheus.GetEndpoint("http"))
            .WithEnvironment("LOKI_URL", loki.GetEndpoint("http"))
            .WithEnvironment("TEMPO_URL", tempo.GetEndpoint("http"))
            // Nothing phones home to Grafana Labs (ADR-006).
            .WithEnvironment("GF_ANALYTICS_REPORTING_ENABLED", "false")
            .WithEnvironment("GF_ANALYTICS_CHECK_FOR_UPDATES", "false")
            .WithEnvironment("GF_ANALYTICS_CHECK_FOR_PLUGIN_UPDATES", "false")
            .WithEnvironment("GF_NEWS_NEWS_FEED_ENABLED", "false")
            // Locally a fixed port next to the app's 5180/8180. In production no host port: Dokploy's Traefik routes
            // the public domain to it over the Docker network, so it is only reachable through TLS.
            .WithHttpEndpoint(port: builder.ExecutionContext.IsPublishMode ? null : 3180, targetPort: 3000, name: "http")
            .WithHttpHealthCheck("/api/health")
            .WithMemoryLimit("384m")
            .WaitFor(loki)
            .WaitFor(tempo)
            .WaitFor(prometheus);

        if (builder.ExecutionContext.IsPublishMode)
        {
            var smtp = builder.AddSmtpCredentials();
            grafana
                // e.g. https://grafana.kupro.dk
                .WithEnvironment("GF_SERVER_ROOT_URL", builder.AddParameter("grafana-public-url"))
                .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", builder.AddParameter("grafana-admin-password", secret: true))
                .WithEnvironment("GF_SECURITY_COOKIE_SECURE", "true")
                // Alert emails go out through Scaleway Transactional Email (ADR-006), same credentials as Keycloak's.
                .WithEnvironment("GF_SMTP_ENABLED", "true")
                .WithEnvironment("GF_SMTP_HOST", $"{Smtp.Host}:{Smtp.Port}")
                .WithEnvironment("GF_SMTP_USER", smtp.User)
                .WithEnvironment("GF_SMTP_PASSWORD", smtp.Password)
                .WithEnvironment("GF_SMTP_FROM_ADDRESS", builder.AddParameter("grafana-smtp-from"))
                .WithEnvironment("ALERT_EMAIL", builder.AddParameter("alert-email"));
        }
        else
        {
            // Local: no login. Alert emails go to Mailpit (WithAlertMailTo).
            grafana
                .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
                .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
                .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
                .WithEnvironment("ALERT_EMAIL", "alerts@localhost");
        }

        return (collector, grafana);
    }

    /// <summary>`aspire run`: Grafana's alert emails land in the local Mailpit instead of Scaleway.</summary>
    public static IResourceBuilder<ContainerResource> WithAlertMailTo(
        this IResourceBuilder<ContainerResource> grafana, IResourceBuilder<ContainerResource> mailpit) =>
        grafana
            .WithEnvironment("GF_SMTP_ENABLED", "true")
            // An endpoint reference, not the name: Grafana and the persistent Mailpit sit on different networks.
            .WithEnvironment("GF_SMTP_HOST", mailpit.GetEndpoint("smtp").Property(EndpointProperty.HostAndPort))
            .WithEnvironment("GF_SMTP_FROM_ADDRESS", "alerts@kupro.local");

    /// <summary>Sends the service's OpenTelemetry to the collector, locally and in production.</summary>
    public static IResourceBuilder<ProjectResource> WithTelemetryTo(
        this IResourceBuilder<ProjectResource> service, IResourceBuilder<ContainerResource> collector) =>
        service
            // Replaces the endpoint Aspire points at its dashboard under `aspire run`.
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", collector.GetEndpoint("otlp"))
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            // Dashboards select on service.name (+ service.namespace from ServiceDefaults).
            .WithEnvironment("OTEL_SERVICE_NAME", service.Resource.Name)
            // So a one-shot run (migrations) doesn't export into a collector that isn't listening yet.
            .WaitFor(collector);

    /// <summary>
    /// Lets the collector scrape what doesn't send OTLP: Postgres (through Alloy's built-in exporter, as the
    /// superuser) and Keycloak's metrics on its management port. Host metrics need nothing from the AppHost.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithScrapeTargets(
        this IResourceBuilder<ContainerResource> collector,
        IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<KeycloakResource> keycloak)
    {
        keycloak.WithEnvironment("KC_METRICS_ENABLED", "true");
        var management = keycloak.GetEndpoint("management");
        var server = postgres.Resource;

        return collector
            // Alloy builds the connection URL from these (it URL-encodes the password).
            .WithEnvironment("POSTGRES_USER", server.UserNameReference)
            .WithEnvironment("POSTGRES_PASSWORD", server.PasswordParameter)
            .WithEnvironment("POSTGRES_ADDRESS", server.PrimaryEndpoint.Property(EndpointProperty.HostAndPort))
            .WithEnvironment("KEYCLOAK_MANAGEMENT_ADDRESS", management.Property(EndpointProperty.HostAndPort))
            .WithEnvironment("KEYCLOAK_MANAGEMENT_SCHEME", management.Property(EndpointProperty.Scheme));
    }

    private static IResourceBuilder<ContainerResource> WithMemoryLimit(
        this IResourceBuilder<ContainerResource> container, string limit) =>
        container
            .WithContainerRuntimeArgs($"--memory={limit}")
            .PublishAsDockerComposeService((_, service) =>
                service.Deploy = new Deploy { Resources = new Resources { Limits = new ResourceSpec { Memory = limit } } });
}
