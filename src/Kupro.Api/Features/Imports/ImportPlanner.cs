using Kupro.Api.Features.Plan;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>A real item behind a count, so the owner sees examples and not only numbers.</summary>
public sealed record PlanSample(string SourceId, string? Key, string Title);

public sealed record PlanCount(int Count, IReadOnlyList<PlanSample> Samples)
{
    public static PlanCount None { get; } = new(0, []);
}

/// <summary>Hours the import brings into a project. <see cref="Capacity"/> is set for an existing project that has one, to compare against.</summary>
public sealed record PlanProjectEffort(string Project, decimal Hours, IReadOnlyList<CapacityEntry> Capacity);

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

    /// <summary>Fields both sides changed, carried over or new. The source's <see cref="PlanSample.SourceId"/> is the field here.</summary>
    public PlanCount Conflicts { get; init; } = PlanCount.None;

    /// <summary>Items whose source number is taken: they get the next free one and keep their key as an alias.</summary>
    public PlanCount KeysTaken { get; init; } = PlanCount.None;

    /// <summary>Imported items a whole snapshot no longer has. They are left alone.</summary>
    public PlanCount DeletedInSource { get; init; } = PlanCount.None;

    /// <summary>Project keys another project uses. The run would fail until the mapping picks another key or that project.</summary>
    public IReadOnlyList<string> ProjectKeysTaken { get; init; } = [];

    /// <summary>Existing projects the mapping targets that aren't there. The run would fail on these too.</summary>
    public IReadOnlyList<string> ProjectsMissing { get; init; } = [];

    public int LabelsToCreate { get; init; }
    public int SprintsToCreate { get; init; }
    public int MilestonesToCreate { get; init; }
    public IReadOnlyList<PlanProjectEffort> EffortByProject { get; init; } = [];

    /// <summary>Imported items whose shape would differ under the current fold and mapping. They keep the shape they have.</summary>
    public int ShapesDiffer { get; init; }
}

/// <summary>
/// Runs the whole mapping and hierarchy fit over a stored snapshot without writing anything, so re-planning after a
/// mapping edit doesn't read the source. Streams the snapshot; only one small record per item stays in memory.
/// </summary>
public static class ImportPlanner
{
    private const int MaxSamples = 5;

    public static Task<ImportPlan> PlanAsync(
        SnapshotReader snapshot, ImportMapping mapping, IReadOnlySet<string> alreadyImported, DateTimeOffset now, CancellationToken ct) =>
        PlanAsync(snapshot, mapping, alreadyImported, new Dictionary<string, FitInput>(StringComparer.Ordinal), now, ct);

    /// <param name="tree">What earlier runs imported, as the fit must see it: a delta lists only part of the tree.</param>
    internal static async Task<ImportPlan> PlanAsync(
        SnapshotReader snapshot, ImportMapping mapping, IReadOnlySet<string> alreadyImported, IReadOnlyDictionary<string, FitInput> tree, DateTimeOffset now, CancellationToken ct)
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

            var input = source.Input(item);
            kept.Add(tree.TryGetValue(item.Id, out var known) ? known with { Parent = input.Parent, Level = input.Level ?? known.Level } : input);
            effort += source.Effort(item) ?? 0;
            if (NeedsReview(item.Status, mapping))
            {
                toReview.Add(item.Id);
            }
        }

        var listed = kept.ToList();
        var keptIds = listed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        kept.AddRange(tree.Values.Where(t => !keptIds.Contains(t.Id)));
        var fit = HierarchyFit.Run(kept, mapping.Fold);
        var created = listed.Where(i => !alreadyImported.Contains(i.Id) && !i.Checklist).Select(i => i.Id).ToList();
        var updated = listed.Where(i => alreadyImported.Contains(i.Id)).Select(i => i.Id).ToList();

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
            ShapesDiffer = Differing(kept, mapping.Fold, fit),
        };
    }

    /// <summary>Imported items that would land elsewhere if they were imported today.</summary>
    private static int Differing(List<FitInput> items, FoldEnd end, FitResult fit)
    {
        if (!items.Any(i => i.Fixed))
        {
            return 0;
        }

        var free = HierarchyFit.Run(items.Select(i => i with { Fixed = false }), end);
        return items.Count(i => i.Fixed && free.Items.TryGetValue(i.Id, out var would) && fit.Items.TryGetValue(i.Id, out var has)
            && (would.Type != has.Type || would.Parent != has.Parent));
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
    internal static async Task<Dictionary<string, PlanSample>> SampleAsync(
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
