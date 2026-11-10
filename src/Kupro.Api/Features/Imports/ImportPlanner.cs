using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>A real item behind a count, so the owner sees examples and not only numbers.</summary>
public sealed record PlanSample(string SourceId, string? Key, string Title);

public sealed record PlanCount(int Count, IReadOnlyList<PlanSample> Samples);

/// <summary>What one <see cref="FoldEnd"/> would do to a four-level source; the wizard shows both side by side.</summary>
public sealed record FoldCounts(int Reparented, int Promoted, int FoldedTops);

/// <summary>The dry run's result (ADR-019 Pipeline), stored as an <see cref="ImportRun"/>'s report.</summary>
public sealed record ImportPlan
{
    public required int Items { get; init; }
    public required PlanCount Create { get; init; }
    public required PlanCount Update { get; init; }
    public required PlanCount SkippedByScope { get; init; }
    public required PlanCount Reparented { get; init; }
    public required PlanCount Promoted { get; init; }
    public required PlanCount FoldedChecklists { get; init; }
    public required PlanCount StatusesToReview { get; init; }

    /// <summary>Items in a parent loop, which become roots. Only a hand-made snapshot has these.</summary>
    public required PlanCount ParentLoops { get; init; }

    public required int Comments { get; init; }
    public required int Attachments { get; init; }
    public required long AttachmentBytes { get; init; }

    /// <summary>Attachments whose source doesn't give a size; <see cref="AttachmentBytes"/> leaves them out.</summary>
    public required int AttachmentsOfUnknownSize { get; init; }

    public required int Changes { get; init; }
    public required decimal EffortHours { get; init; }
    public required bool FourLevels { get; init; }

    /// <summary>Only for a four-level source.</summary>
    public FoldCounts? FoldBottom { get; init; }

    public FoldCounts? FoldTop { get; init; }
}

/// <summary>
/// Runs the whole mapping and hierarchy fit over a stored snapshot without writing anything, so re-planning after a
/// mapping edit doesn't read the source. Streams the snapshot; only one small record per item stays in memory.
/// </summary>
public static class ImportPlanner
{
    private const int MaxSamples = 5;

    public static async Task<ImportPlan> PlanAsync(
        SnapshotReader snapshot, ImportMapping mapping, IReadOnlySet<string> alreadyImported, DateTimeOffset now, CancellationToken ct)
    {
        var source = new MappedSource(snapshot.Header, mapping, now);
        var kept = new List<FitInput>();
        var skipped = new List<string>();
        var toReview = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var effort = 0m;
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            // Paged source searches can return an item twice when results shift, and scripts can write anything.
            if (!seen.Add(item.Id))
            {
                continue;
            }

            if (!source.InScope(item))
            {
                skipped.Add(item.Id);
                continue;
            }

            kept.Add(source.Input(item));
            effort += source.Effort(item) ?? 0;
            if (NeedsReview(item.Status, mapping))
            {
                toReview.Add(item.Id);
            }
        }

        var fit = HierarchyFit.Run(kept, mapping.Fold);
        var keptIds = kept.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var created = kept.Where(i => !alreadyImported.Contains(i.Id)).Select(i => i.Id).ToList();
        var updated = kept.Where(i => alreadyImported.Contains(i.Id)).Select(i => i.Id).ToList();

        var (comments, attachments, bytes, unknownSize, changes) = await CountHistoryAsync(snapshot, keptIds, ct);

        var samples = await SampleAsync(
            snapshot, [created, updated, skipped, fit.Reparented, fit.Promoted, [.. fit.Checklists.Select(c => c.Item)], toReview, fit.Cycles], ct);
        PlanCount Count(IReadOnlyList<string> ids) =>
            new(ids.Count, [.. ids.Take(MaxSamples).Where(samples.ContainsKey).Select(id => samples[id])]);

        return new ImportPlan
        {
            Items = kept.Count + skipped.Count,
            Create = Count(created),
            Update = Count(updated),
            SkippedByScope = Count(skipped),
            Reparented = Count(fit.Reparented),
            Promoted = Count(fit.Promoted),
            FoldedChecklists = Count([.. fit.Checklists.Select(c => c.Item)]),
            StatusesToReview = Count(toReview),
            ParentLoops = Count(fit.Cycles),
            Comments = comments,
            Attachments = attachments,
            AttachmentBytes = bytes,
            AttachmentsOfUnknownSize = unknownSize,
            Changes = changes,
            EffortHours = effort,
            FourLevels = fit.FourLevels,
            FoldBottom = fit.FourLevels ? Fold(kept, FoldEnd.Bottom) : null,
            FoldTop = fit.FourLevels ? Fold(kept, FoldEnd.Top) : null,
        };
    }

    private static FoldCounts Fold(List<FitInput> items, FoldEnd end)
    {
        var fit = HierarchyFit.Run(items, end);
        return new FoldCounts(fit.Reparented.Count, fit.Promoted.Count, fit.FoldedTops.Count);
    }

    /// <summary>A status that appeared after the mapping was saved has no state yet: Todo, and worth a look.</summary>
    private static bool NeedsReview(string status, ImportMapping mapping) =>
        !mapping.States.ContainsKey(status) || mapping.StatusesToReview.Contains(status);

    private static async Task<(int Comments, int Attachments, long Bytes, int UnknownSize, int Changes)> CountHistoryAsync(
        SnapshotReader snapshot, HashSet<string> kept, CancellationToken ct)
    {
        var comments = await snapshot.ReadCommentsAsync(ct).CountAsync(c => kept.Contains(c.Item), ct);
        var changes = await snapshot.ReadChangesAsync(ct).CountAsync(c => kept.Contains(c.Item), ct);
        var (attachments, bytes, unknownSize) = (0, 0L, 0);
        await foreach (var attachment in snapshot.ReadAttachmentsAsync(ct))
        {
            if (kept.Contains(attachment.Item))
            {
                attachments++;
                bytes += attachment.Size ?? 0;
                unknownSize += attachment.Size is null ? 1 : 0;
            }
        }

        return (comments, attachments, bytes, unknownSize, changes);
    }

    /// <summary>A second pass for titles of the sampled items only: keeping every title would hold the whole source in memory.</summary>
    private static async Task<Dictionary<string, PlanSample>> SampleAsync(
        SnapshotReader snapshot, IReadOnlyList<IReadOnlyList<string>> counts, CancellationToken ct)
    {
        var wanted = counts.SelectMany(ids => ids.Take(MaxSamples)).ToHashSet(StringComparer.Ordinal);
        var samples = new Dictionary<string, PlanSample>(wanted.Count, StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (wanted.Contains(item.Id))
            {
                samples[item.Id] = new PlanSample(item.Id, item.Key, item.Title);
            }
        }

        return samples;
    }
}
