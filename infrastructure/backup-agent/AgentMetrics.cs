using System.Diagnostics.Metrics;

namespace Kupro.BackupAgent;

/// <summary>
/// The last status as OpenTelemetry gauges, exported over OTLP to Alloy and on to Prometheus (ADR-014). Grafana's
/// backup alerts (infrastructure/observability/build_dashboards.py) read these: health, and how old the status is,
/// so a stuck or dead agent alerts too. Each gauge reports nothing until the first status is built.
/// </summary>
public sealed class AgentMetrics : IDisposable
{
    public const string MeterName = "Kupro.BackupAgent";

    private readonly Meter _meter = new(MeterName);

    public AgentMetrics(StatusBuilder status)
    {
        // 0 healthy, 1 degraded, 2 unhealthy: the enum order.
        _meter.CreateObservableGauge("backup_agent.health",
            () => Observe(status, s => (double)s.Health), description: "0 healthy, 1 degraded, 2 unhealthy");
        _meter.CreateObservableGauge("backup_agent.status.age",
            () => Observe(status, s => (DateTimeOffset.UtcNow - s.GeneratedAt).TotalSeconds), unit: "s",
            description: "Time since the agent last built its status");
        _meter.CreateObservableGauge("backup_agent.data_secured.age",
            () => Observe(status, s => s.DataSecuredAt is { } at ? (DateTimeOffset.UtcNow - at).TotalSeconds : null), unit: "s",
            description: "Time since the newest commit known to be in the repo (the RPO)");
        _meter.CreateObservableGauge("backup_agent.full_backup.age",
            () => Observe(status, s => s.Backups.LastFullAt is { } at ? (DateTimeOffset.UtcNow - at).TotalSeconds : null), unit: "s",
            description: "Time since the newest full backup finished");
        _meter.CreateObservableGauge("backup_agent.issues",
            () => Observe(status, s => s.Issues.Count), description: "Open issues in the status");
    }

    private static IEnumerable<Measurement<double>> Observe(StatusBuilder status, Func<AgentStatus, double?> read)
    {
        if (status.Last is { } last && read(last) is { } value)
        {
            yield return new Measurement<double>(value);
        }
    }

    public void Dispose() => _meter.Dispose();
}
