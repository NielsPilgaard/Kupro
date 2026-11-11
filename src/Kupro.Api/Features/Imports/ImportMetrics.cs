using System.Diagnostics.Metrics;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Imports for Grafana (ADR-014, task 023 Ops): runs by kind and outcome, items written per second, and the bytes
/// imports add to object storage, which the storage-growth alert watches. Singleton.
/// </summary>
public sealed class ImportMetrics
{
    private readonly Counter<long> _runs;
    private readonly Counter<long> _items;
    private readonly Counter<long> _bytes;

    public ImportMetrics(IMeterFactory meters, IHostEnvironment environment)
    {
        // The API's own meter, which ServiceDefaults exports.
        var meter = meters.Create(environment.ApplicationName);
        _runs = meter.CreateCounter<long>("kupro.import.runs", "{run}", "Import runs that ended, by kind and state");
        _items = meter.CreateCounter<long>("kupro.import.items", "{item}", "Issues an import created or updated");
        _bytes = meter.CreateCounter<long>("kupro.import.bytes", "By", "Attachment bytes imports copied into object storage");
    }

    public void RunEnded(ImportRunKind kind, ImportRunState state) =>
        _runs.Add(1, new("kind", kind == ImportRunKind.DryRun ? "plan" : "commit"), new("state", state == ImportRunState.Succeeded ? "succeeded" : "failed"));

    public void Items(int count)
    {
        if (count > 0)
        {
            _items.Add(count);
        }
    }

    public void Bytes(long count) => _bytes.Add(count);
}
