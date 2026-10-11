using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

// Self-hosted observability (ADR-014): apps → otel-collector → Loki (logs), Tempo (traces), Prometheus (metrics),
// with Grafana on top. The same containers run under `aspire run` and in the published compose; their config lives
// in infrastructure/observability/. Only Grafana is reachable from outside.
internal static class Observability
{
    private const string ConfigDir = "../../infrastructure/observability";

    public static IResourceBuilder<ContainerResource> AddObservability(this IDistributedApplicationBuilder builder)
    {
        var loki = builder.AddContainer("loki", "grafana/loki", "3.7.8")
            .WithBindMount($"{ConfigDir}/loki.yaml", "/etc/loki/flowstate.yaml", isReadOnly: true)
            .WithArgs("-config.file=/etc/loki/flowstate.yaml")
            .WithVolume("flowstate-loki-data", "/loki")
            .WithHttpEndpoint(targetPort: 3100, name: "http")
            .WithHttpHealthCheck("/ready")
            .WithMemoryLimit("768m");

        var tempo = builder.AddContainer("tempo", "grafana/tempo", "3.1.0")
            .WithBindMount($"{ConfigDir}/tempo.yaml", "/etc/tempo/flowstate.yaml", isReadOnly: true)
            .WithArgs("-target=all", "-config.file=/etc/tempo/flowstate.yaml")
            .WithVolume("flowstate-tempo-data", "/var/tempo")
            .WithHttpEndpoint(targetPort: 3200, name: "http")
            .WithHttpEndpoint(targetPort: 4318, name: "otlp")
            .WithHttpHealthCheck("/ready")
            .WithMemoryLimit("768m");

        var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.15.0")
            .WithBindMount($"{ConfigDir}/prometheus.yaml", "/etc/prometheus/flowstate.yaml", isReadOnly: true)
            .WithArgs(
                "--config.file=/etc/prometheus/flowstate.yaml",
                "--storage.tsdb.path=/prometheus",
                "--storage.tsdb.retention.time=30d",
                "--web.enable-otlp-receiver")
            .WithVolume("flowstate-prometheus-data", "/prometheus")
            .WithHttpEndpoint(targetPort: 9090, name: "http")
            .WithHttpHealthCheck("/-/ready")
            .WithMemoryLimit("512m");

        var collector = builder.AddContainer("otel-collector", "otel/opentelemetry-collector-contrib", "0.162.0")
            .WithBindMount($"{ConfigDir}/otel-collector.yaml", "/etc/otelcol/flowstate.yaml", isReadOnly: true)
            .WithArgs("--config=/etc/otelcol/flowstate.yaml")
            .WithEnvironment("LOKI_URL", loki.GetEndpoint("http"))
            .WithEnvironment("PROMETHEUS_URL", prometheus.GetEndpoint("http"))
            .WithEnvironment("TEMPO_OTLP_URL", tempo.GetEndpoint("otlp"))
            .WithHttpEndpoint(targetPort: 4318, name: "otlp")
            .WithHttpEndpoint(targetPort: 13133, name: "health")
            .WithHttpHealthCheck("/", endpointName: "health")
            .WithMemoryLimit("256m");

        var grafana = builder.AddContainer("grafana", "grafana/grafana", "13.2.3")
            .WithBindMount($"{ConfigDir}/grafana/provisioning", "/etc/grafana/provisioning", isReadOnly: true)
            .WithBindMount($"{ConfigDir}/grafana/dashboards", "/etc/grafana/dashboards", isReadOnly: true)
            .WithVolume("flowstate-grafana-data", "/var/lib/grafana")
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
            grafana
                // e.g. https://grafana.flowstate.dk
                .WithEnvironment("GF_SERVER_ROOT_URL", builder.AddParameter("grafana-public-url"))
                .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", builder.AddParameter("grafana-admin-password", secret: true))
                .WithEnvironment("GF_SECURITY_COOKIE_SECURE", "true")
                // Alert emails go out through Scaleway Transactional Email (ADR-006): user = project id, password = API key.
                .WithEnvironment("GF_SMTP_ENABLED", "true")
                .WithEnvironment("GF_SMTP_HOST", "smtp.tem.scaleway.com:587")
                .WithEnvironment("GF_SMTP_USER", builder.AddParameter("grafana-smtp-user"))
                .WithEnvironment("GF_SMTP_PASSWORD", builder.AddParameter("grafana-smtp-password", secret: true))
                .WithEnvironment("GF_SMTP_FROM_ADDRESS", builder.AddParameter("grafana-smtp-from"))
                .WithEnvironment("ALERT_EMAIL", builder.AddParameter("alert-email"));
        }
        else
        {
            // Local: no login. Alerts evaluate but no email is sent.
            grafana
                .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
                .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
                .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
                .WithEnvironment("ALERT_EMAIL", "alerts@localhost");

            // Telemetry also still reaches the Aspire dashboard: the collector forwards a copy to it.
            var dashboardUrl = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];
            if (dashboardUrl is not null)
            {
                collector
                    .WithBindMount($"{ConfigDir}/otel-collector.dev.yaml", "/etc/otelcol/dev.yaml", isReadOnly: true)
                    .WithArgs("--config=/etc/otelcol/dev.yaml", "--feature-gates=confmap.enableMergeAppendOption")
                    .WithEnvironment("ASPIRE_DASHBOARD_OTLP_URL", new HostUrl(dashboardUrl))
                    .WithEnvironment("ASPIRE_DASHBOARD_OTLP_API_KEY", builder.Configuration["AppHost:OtlpApiKey"] ?? "");
            }
        }

        return collector;
    }

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

    private static IResourceBuilder<ContainerResource> WithMemoryLimit(
        this IResourceBuilder<ContainerResource> container, string limit) =>
        container
            .WithContainerRuntimeArgs($"--memory={limit}")
            .PublishAsDockerComposeService((_, service) =>
                service.Deploy = new Deploy { Resources = new Resources { Limits = new ResourceSpec { Memory = limit } } });
}
