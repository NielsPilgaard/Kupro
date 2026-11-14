using Kupro.Api.Features.Issues;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// An item as the fit sees it: the mapped type and the source parent. <see cref="Level"/> is the source type's.
/// <see cref="Fixed"/> means it is already imported: it keeps its Kupro type, and only the source can still move it.
/// </summary>
public sealed record FitInput(string Id, IssueType Type, string? Parent, int? Level = null, bool Checklist = false, bool Fixed = false);

/// <summary>An item's place in Kupro.</summary>
public sealed record FittedItem(string Id, IssueType Type, string? Parent);

/// <summary>A source parent the item can't keep; becomes a <c>relates</c> relation so the grouping isn't lost.</summary>
public sealed record OldParent(string Item, string Parent);

/// <summary>A checklist item folded into its parent's description.</summary>
public sealed record FoldedChecklist(string Item, string Parent);

/// <summary>An item under a folded top-level item, labelled with that item's name.</summary>
public sealed record FoldedTop(string Item, string Top);

public sealed record FitResult(
    IReadOnlyDictionary<string, FittedItem> Items,
    IReadOnlyList<OldParent> OldParents,
    IReadOnlyList<FoldedChecklist> Checklists,
    IReadOnlyList<FoldedTop> FoldedTops,
    IReadOnlyList<string> Promoted,
    IReadOnlyList<string> Cycles,
    bool FourLevels)
{
    /// <summary>Items that ended up under a different parent, or none. A folded top's children are counted in <see cref="FoldedTops"/>.</summary>
    public IReadOnlyList<string> Reparented => [.. OldParents.Select(p => p.Item).Except(FoldedTops.Select(t => t.Item)).Distinct()];
}

/// <summary>
/// Squeezes a source's tree into ADR-008's shape, driven only by the mapping (ADR-019 Shape). The plan and the commit
/// both run this, so what the owner previews is what gets written. Pure, and holds one small record per item.
/// </summary>
public static class HierarchyFit
{
    private const int FourLevelHeight = 4;

    public static FitResult Run(IEnumerable<FitInput> input, FoldEnd fold)
    {
        var all = input.ToDictionary(i => i.Id, StringComparer.Ordinal);

        // A checklist item needs a real item to fold into. Without one it would vanish, so it stays an item.
        var checklists = all.Values
            .Where(i => i.Checklist)
            .Select(i => new FoldedChecklist(i.Id, Holder(i, all)!))
            .Where(c => c.Parent is not null)
            .ToList();
        foreach (var folded in checklists)
        {
            all.Remove(folded.Item);
        }

        var (order, depth, cycles) = Walk(all);
        var height = depth.Count == 0 ? 0 : depth.Values.Max() + 1;
        var levels = all.Values.Where(i => i.Level is not null).Select(i => i.Level!.Value).Distinct().OrderDescending().ToList();
        var fourLevels = height >= FourLevelHeight || levels.Count >= FourLevelHeight;

        var types = all.Values.ToDictionary(i => i.Id, i => i.Type, StringComparer.Ordinal);
        var parents = all.Values.ToDictionary(i => i.Id, i => cycles.Contains(i.Id) ? null : Known(i.Parent, all), StringComparer.Ordinal);
        var oldParents = new List<OldParent>();
        var foldedTops = new List<FoldedTop>();
        if (fold == FoldEnd.Top && fourLevels)
        {
            FoldTop(all, levels, depth, types, parents, oldParents, foldedTops);
        }

        return Fit(all, order, types, parents, oldParents, foldedTops, checklists, cycles, fourLevels);
    }

    /// <summary>Roots first. An item in a parent loop (only a hand-made snapshot has one) becomes a root, and is reported.</summary>
    private static (List<string> Order, Dictionary<string, int> Depth, HashSet<string> Cycles) Walk(Dictionary<string, FitInput> all)
    {
        var children = all.Values.Where(i => Known(i.Parent, all) is not null).ToLookup(i => i.Parent!, i => i.Id, StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>(all.Count);
        var cycles = new HashSet<string>(StringComparer.Ordinal);

        void Visit(IEnumerable<string> roots)
        {
            var queue = new Queue<string>(roots);
            foreach (var root in queue)
            {
                depth[root] = 0;
            }

            while (queue.TryDequeue(out var id))
            {
                order.Add(id);
                foreach (var child in children[id].Where(c => !depth.ContainsKey(c)))
                {
                    depth[child] = depth[id] + 1;
                    queue.Enqueue(child);
                }
            }
        }

        Visit(all.Values.Where(i => Known(i.Parent, all) is null).Select(i => i.Id));
        foreach (var item in all.Keys.Order(StringComparer.Ordinal))
        {
            if (!depth.ContainsKey(item))
            {
                cycles.Add(item);
                Visit([item]);
            }
        }

        return (order, depth, cycles);
    }

    /// <summary>
    /// Ranks by the source type's level when every item has one, else by depth in the tree. Rank 0 becomes a childless
    /// Epic; the ranks below move up one Kupro level.
    /// </summary>
    private static void FoldTop(
        Dictionary<string, FitInput> all, List<int> levels, Dictionary<string, int> depth, Dictionary<string, IssueType> types,
        Dictionary<string, string?> parents, List<OldParent> oldParents, List<FoldedTop> foldedTops)
    {
        var byLevel = all.Values.All(i => i.Level is not null);
        int Rank(FitInput item) => byLevel ? levels.IndexOf(item.Level!.Value) : depth[item.Id];

        foreach (var item in all.Values.Where(i => !i.Fixed))
        {
            types[item.Id] = Rank(item) switch
            {
                0 or 1 => IssueType.Epic,
                2 => IssueType.Feature,
                _ => IssueType.Task,
            };
        }

        foreach (var item in all.Values.Where(i => !i.Fixed && Rank(i) > 0 && parents[i.Id] is { } parent && Rank(all[parent]) == 0))
        {
            var top = parents[item.Id]!;
            oldParents.Add(new OldParent(item.Id, top));
            foldedTops.Add(new FoldedTop(item.Id, top));
            parents[item.Id] = null;
        }
    }

    private static FitResult Fit(
        Dictionary<string, FitInput> all, List<string> order, Dictionary<string, IssueType> types, Dictionary<string, string?> parents,
        List<OldParent> oldParents, List<FoldedTop> foldedTops, List<FoldedChecklist> checklists, HashSet<string> cycles, bool fourLevels)
    {
        var hasChildren = parents.Values.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var fitted = new Dictionary<string, FittedItem>(all.Count, StringComparer.Ordinal);
        var promoted = new List<string>();

        // Roots first, so a parent's final type is known when its children look at it.
        foreach (var id in order)
        {
            var type = types[id];
            var parent = parents[id];
            var fittedParent = parent is null ? null : fitted[parent];
            if (!all[id].Fixed && type == IssueType.Task && hasChildren.Contains(id) && (fittedParent is null || fittedParent.Type == IssueType.Epic))
            {
                type = IssueType.Feature;
                promoted.Add(id);
            }

            var place = fittedParent;
            while (place is not null && !IssueRules.CanBeParent(type, place.Type))
            {
                place = place.Parent is null ? null : fitted[place.Parent];
            }

            if (parent is not null && place?.Id != parent)
            {
                oldParents.Add(new OldParent(id, parent));
            }

            fitted[id] = new FittedItem(id, type, place?.Id);
        }

        return new FitResult(fitted, oldParents, checklists, foldedTops, promoted, [.. cycles], fourLevels);
    }

    /// <summary>The nearest ancestor that isn't a checklist item itself; null if the chain ends or loops first.</summary>
    private static string? Holder(FitInput item, Dictionary<string, FitInput> all)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { item.Id };
        var parent = Known(item.Parent, all);
        while (parent is not null && all[parent].Checklist)
        {
            parent = seen.Add(parent) ? Known(all[parent].Parent, all) : null;
        }

        return parent;
    }

    /// <summary>A parent the import doesn't have (out of scope) counts as none here.</summary>
    private static string? Known(string? parent, Dictionary<string, FitInput> all) =>
        parent is not null && all.ContainsKey(parent) ? parent : null;
}
