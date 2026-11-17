using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>An item an earlier run imported, as the read and the fit see it.</summary>
internal sealed record KnownItem(IssueType Type, string Key, string Title, string? SourceParent, int? Level);

/// <summary>A checklist item an earlier run folded into a description.</summary>
internal sealed record KnownChecklist(ChecklistLine Line, string? SourceParent, int? Level);

/// <summary>What earlier runs of an import brought across. Empty on a first run.</summary>
internal sealed record ImportTree(IReadOnlyDictionary<string, KnownItem> Items, IReadOnlyDictionary<string, KnownChecklist> Folded)
{
    public static ImportTree Empty { get; } = new(new Dictionary<string, KnownItem>(), new Dictionary<string, KnownChecklist>());
}

/// <summary>An in-scope item's references: what its key becomes, and what people call it in the source.</summary>
internal sealed record ItemRef(string Id, string ProjectKey, string? Key, string? Url);

/// <summary>
/// One pass over a snapshot through the mapping, and the hierarchy fit of the result (ADR-019 Shape). The plan counts
/// it and the commit writes it, so what the owner previews is what gets written. Holds one small record per item.
/// </summary>
internal sealed class SourceRead
{
    private SourceRead(ImportTree tree, FoldEnd fold, bool counting) => (Tree, Fold, _counting) = (tree, fold, counting);

    /// <summary>Whether to fill what only a plan reads: <see cref="Skipped"/>, <see cref="ToReview"/> and <see cref="Hours"/>.</summary>
    private readonly bool _counting;

    public ImportTree Tree { get; }
    public FoldEnd Fold { get; }

    /// <summary>What the fit runs on: this snapshot's in-scope items, plus what earlier runs imported that it doesn't list.</summary>
    public List<FitInput> Kept { get; } = [];

    /// <summary>The snapshot's in-scope items.</summary>
    public HashSet<string> KeptIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Out-of-scope items. Plan only.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>In-scope items whose status the owner should review. Plan only.</summary>
    public List<string> ToReview { get; } = [];

    /// <summary>Imported items a whole snapshot no longer has.</summary>
    public List<string> Deleted { get; } = [];

    public List<ItemRef> Refs { get; } = [];
    public HashSet<ProjectTarget> Targets { get; } = [];
    public HashSet<(string SprintId, string ProjectKey)> Sprints { get; } = [];
    public Dictionary<string, ChecklistLine> ChecklistLines { get; } = new(StringComparer.Ordinal);

    /// <summary>Hours per target project key, over the snapshot's in-scope items that get an issue. Plan only.</summary>
    public Dictionary<string, decimal> Hours { get; } = new(StringComparer.Ordinal);

    public FitResult Fit { get; private set; } = null!;

    /// <summary>Items the run would create: in the fit and not imported before.</summary>
    public IEnumerable<ItemRef> New => Refs.Where(r => Fit.Items.ContainsKey(r.Id) && !Tree.Items.ContainsKey(r.Id));

    /// <summary>Repeated IDs count once: paged source searches can return an item twice, and scripts can write anything.</summary>
    /// <param name="counting">A plan: also fill what only it reads. A commit leaves it out, to hold less per item.</param>
    public static async Task<SourceRead> ReadAsync(SnapshotReader snapshot, MappedSource source, ImportTree tree, bool counting, CancellationToken ct)
    {
        var read = new SourceRead(tree, source.Fold, counting);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A checklist item's hours count only if the fit can't fold it and it stays an item.
        var checklistHours = new List<(string Id, string ProjectKey, decimal Hours)>();
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if (!seen.Add(item.Id))
            {
                continue;
            }

            if (!source.InScope(item))
            {
                if (counting)
                {
                    read.Skipped.Add(item.Id);
                }

                continue;
            }

            var input = read.Shaped(source.Input(item));
            var target = source.Project(item.Project);
            read.Kept.Add(input);
            read.KeptIds.Add(item.Id);
            read.Refs.Add(new ItemRef(item.Id, target.Key, item.Key, item.Url));
            read.Targets.Add(target);
            if (counting && source.NeedsReview(item.Status))
            {
                read.ToReview.Add(item.Id);
            }

            if (input.Checklist)
            {
                read.ChecklistLines[item.Id] = new ChecklistLine(item.Title, source.State(item) is IssueState.Done or IssueState.Canceled);
                if (counting)
                {
                    checklistHours.Add((item.Id, target.Key, source.Effort(item) ?? 0));
                }
            }
            else if (counting)
            {
                read.AddHours(target.Key, source.Effort(item) ?? 0);
            }

            if (item.Sprint is { } sprint)
            {
                read.Sprints.Add((sprint, target.Key));
            }
        }

        foreach (var sprint in snapshot.Header.Sprints.Where(s => s.Project is not null))
        {
            // A sprint with no items yet still belongs to a project the import brings over.
            read.Sprints.Add((sprint.Id, source.Project(sprint.Project!).Key));
        }

        read.AddImportedTree(seen, snapshot.Header.Partial);
        read.Fit = HierarchyFit.Run(read.Kept, source.Fold);
        foreach (var (id, projectKey, hours) in checklistHours.Where(c => read.Fit.Items.ContainsKey(c.Id)))
        {
            read.AddHours(projectKey, hours);
        }

        return read;
    }

    private void AddHours(string projectKey, decimal hours) => Hours[projectKey] = Hours.GetValueOrDefault(projectKey) + hours;

    /// <summary>An item already imported keeps the shape it has in Kupro, whatever the mapping and the fold say now (ADR-019 Shape).</summary>
    private FitInput Shaped(FitInput input) =>
        Tree.Items.TryGetValue(input.Id, out var known) ? input with { Type = known.Type, Level = input.Level ?? known.Level, Checklist = false, Fixed = true }
        : Tree.Folded.TryGetValue(input.Id, out var folded) ? input with { Level = input.Level ?? folded.Level, Checklist = true }
        : input;

    /// <summary>What earlier runs imported and this snapshot doesn't list, so a new child finds its parent and a holder keeps its checklist.</summary>
    private void AddImportedTree(HashSet<string> seen, bool partial)
    {
        foreach (var (id, known) in Tree.Items.Where(k => !KeptIds.Contains(k.Key)))
        {
            Kept.Add(new FitInput(id, known.Type, known.SourceParent, known.Level, Fixed: true));
        }

        foreach (var (id, folded) in Tree.Folded.Where(k => !KeptIds.Contains(k.Key)))
        {
            Kept.Add(new FitInput(id, IssueType.Task, folded.SourceParent, folded.Level, Checklist: true));
            ChecklistLines[id] = folded.Line;
        }

        if (!partial)
        {
            Deleted.AddRange(Tree.Items.Keys.Where(id => !seen.Contains(id)).Order(StringComparer.Ordinal));
        }
    }
}
