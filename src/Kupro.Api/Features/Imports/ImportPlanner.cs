using System.Globalization;
using Kupro.Api.Features.Plan;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>A real item behind a count, so the owner sees examples and not only numbers.</summary>
public sealed record PlanSample(string SourceId, string? Key, string Title);

public sealed record PlanCount(int Count, IReadOnlyList<PlanSample> Samples)
{
    public static PlanCount None { get; } = new(0, []);
}

/// <summary>
/// Hours the import brings into a project, to compare against <see cref="Capacity"/>: an existing project's, or the one the
/// source brings for a project the import creates.
/// </summary>
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

/// <summary>Records an earlier run already wrote, so a plan counts only the history a commit would still write.</summary>
/// <param name="DoneAsync">Of a record kind (<see cref="ImportedKinds"/>), the source IDs among these that are written.</param>
/// <param name="ChangeMarks">Per item, the newest change replayed (<see cref="Mark"/>).</param>
internal sealed record ImportedHistory(
    Func<string, IReadOnlyCollection<string>, Task<HashSet<string>>> DoneAsync, IReadOnlyDictionary<string, string> ChangeMarks)
{
    public static ImportedHistory None { get; } = new((_, _) => Task.FromResult(new HashSet<string>(StringComparer.Ordinal)), new Dictionary<string, string>());

    /// <summary>Sorts by time, then ID: the newest change an item has had replayed. UTC round-trip text sorts by time.</summary>
    public static string Mark(SourceChange change) => $"{change.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}|{change.Id}";

    /// <summary>An earlier run replayed this change already.</summary>
    public bool Seen(SourceChange change) => ChangeMarks.TryGetValue(change.Item, out var mark) && string.CompareOrdinal(Mark(change), mark) <= 0;
}

/// <summary>
/// Counts a <see cref="SourceRead"/> without writing anything, so re-planning after a mapping edit doesn't read the
/// source. Streams the snapshot; only one small record per item stays in memory.
/// </summary>
public static class ImportPlanner
{
    private const int MaxSamples = 5;
    private const int HistoryChunk = 2000;

    /// <summary>A first run's plan: nothing imported yet.</summary>
    public static async Task<ImportPlan> PlanAsync(SnapshotReader snapshot, ImportMapping mapping, DateTimeOffset now, CancellationToken ct)
    {
        var read = await SourceRead.ReadAsync(snapshot, new MappedSource(snapshot.Header, mapping, now), ImportTree.Empty, ct);
        return await PlanAsync(snapshot, read, ImportedHistory.None, [], ct);
    }

    /// <param name="keysTaken">Items whose source number another issue in the workspace has.</param>
    internal static async Task<ImportPlan> PlanAsync(
        SnapshotReader snapshot, SourceRead read, ImportedHistory history, IReadOnlyList<string> keysTaken, CancellationToken ct)
    {
        var fit = read.Fit;
        var created = read.New.Select(r => r.Id).ToList();
        var (comments, attachments, bytes, unknownSize, changes) = await CountHistoryAsync(snapshot, fit, history, ct);

        var samples = await SampleAsync(
            snapshot, [created, read.Skipped, fit.Reparented, fit.Promoted, [.. fit.Checklists.Select(c => c.Item)], read.ToReview, fit.Cycles, keysTaken], ct);
        PlanCount Count(IReadOnlyList<string> ids) =>
            new(ids.Count, [.. ids.Take(MaxSamples).Where(samples.ContainsKey).Select(id => samples[id])]);

        return new ImportPlan
        {
            Items = read.KeptIds.Count + read.Skipped.Count,
            Create = Count(created),

            // Which imported items change takes a re-run's comparison against the workspace; ImportCommit adds it.
            Update = PlanCount.None,
            SkippedByScope = Count(read.Skipped),
            Reparented = Count(fit.Reparented),
            Promoted = Count(fit.Promoted),
            FoldedChecklists = Count([.. fit.Checklists.Select(c => c.Item)]),
            StatusesToReview = Count(read.ToReview),
            ParentLoops = Count(fit.Cycles),
            KeysTaken = Count(keysTaken),
            Comments = comments,
            Attachments = attachments,
            AttachmentBytes = bytes,
            AttachmentsOfUnknownSize = unknownSize,
            Changes = changes,
            EffortHours = read.Hours.Values.Sum(),
            FourLevels = fit.FourLevels,
            FoldBottom = fit.FourLevels ? Fold(read.Kept, FoldEnd.Bottom) : null,
            FoldTop = fit.FourLevels ? Fold(read.Kept, FoldEnd.Top) : null,
            ShapesDiffer = Differing(read.Kept, read.Fold, fit),
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

    /// <summary>The history a commit would still write: records of items that get an issue, less what earlier runs wrote.</summary>
    private static async Task<(int Comments, int Attachments, long Bytes, int UnknownSize, int Changes)> CountHistoryAsync(
        SnapshotReader snapshot, FitResult fit, ImportedHistory history, CancellationToken ct)
    {
        var comments = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var chunk in snapshot.ReadCommentsAsync(ct).Where(c => seen.Add(c.Id) && fit.HolderOf(c.Item) is not null).Chunk(HistoryChunk).WithCancellation(ct))
        {
            var done = await history.DoneAsync(ImportedKinds.Comment, [.. chunk.Select(c => c.Id)]);
            comments += chunk.Count(c => !done.Contains(c.Id));
        }

        var (attachments, bytes, unknownSize) = (0, 0L, 0);
        seen.Clear();
        await foreach (var chunk in snapshot.ReadAttachmentsAsync(ct).Where(a => seen.Add(a.Id) && fit.HolderOf(a.Item) is not null).Chunk(HistoryChunk).WithCancellation(ct))
        {
            var done = await history.DoneAsync(ImportedKinds.Attachment, [.. chunk.Select(a => a.Id)]);
            foreach (var attachment in chunk.Where(a => !done.Contains(a.Id)))
            {
                attachments++;
                bytes += attachment.Size ?? 0;
                unknownSize += attachment.Size is null ? 1 : 0;
            }
        }

        // Folded checklist items have no issue of their own to replay changes on.
        var changes = await snapshot.ReadChangesAsync(ct).CountAsync(c => fit.Items.ContainsKey(c.Item) && !history.Seen(c), ct);
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
