using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>A sampled item as the source shows it. <see cref="Type"/> is the source type's name.</summary>
public sealed record SampleSourceNode(string SourceId, string? Key, string Title, string Type, int Depth);

/// <summary>
/// Where a sampled item lands under one fold. <see cref="Key"/> is the Kupro key it would get, if its number is free.
/// <see cref="OldParent"/> is the source parent it moves away from (kept as a <c>relates</c> relation);
/// <see cref="LabelOf"/> is the folded top-level item whose name becomes its label.
/// </summary>
public sealed record SampleKuproNode(
    string SourceId, string? Key, string Title, IssueType Type, IssueState State, int Depth, string? OldParent, bool Promoted, string? LabelOf);

/// <summary>Real branches of a four-level source and their shape under each fold: the Hierarchy step's trees (prototype 07).</summary>
public sealed record HierarchySample(IReadOnlyList<SampleSourceNode> Source, IReadOnlyList<SampleKuproNode> Bottom, IReadOnlyList<SampleKuproNode> Top);

/// <summary>Picks the sample and lays it out. Pure; holds one small record per item while picking.</summary>
internal static class HierarchySampler
{
    private const int MaxNodes = 12;
    private const int MaxRoots = 2;
    private const int MaxChildren = 3;

    /// <summary>A second branch starts only with this much room left, so it shows more than its top item.</summary>
    private const int MinBranch = 3;

    /// <summary>Pre-order IDs of up to two branches, deepest first, so the sample shows every level the source has.</summary>
    /// <param name="listed">Items the snapshot lists; earlier-imported items it doesn't can't be shown, so they take no slot.</param>
    public static List<string> Pick(IReadOnlyList<FitInput> items, FitResult fit, IReadOnlySet<string> listed)
    {
        var all = items.Where(i => listed.Contains(i.Id) && fit.Items.ContainsKey(i.Id)).DistinctBy(i => i.Id).ToList();
        var ids = all.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var children = all.Where(i => i.Parent is not null && ids.Contains(i.Parent)).ToLookup(i => i.Parent!, i => i.Id, StringComparer.Ordinal);
        var roots = all.Where(i => i.Parent is null || !ids.Contains(i.Parent)).Select(i => i.Id).ToList();

        // Breadth-first from the roots, then heights from the leaves up. Items in a parent loop aren't reached.
        var order = new List<string>(all.Count);
        var queue = new Queue<string>(roots);
        while (queue.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var child in children[id])
            {
                queue.Enqueue(child);
            }
        }

        var height = new Dictionary<string, int>(order.Count, StringComparer.Ordinal);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            height[order[i]] = 1 + children[order[i]].Select(c => height[c]).DefaultIfEmpty(0).Max();
        }

        var picked = new List<string>();
        foreach (var root in roots.Where(r => height[r] > 1).OrderByDescending(r => height[r]).Take(MaxRoots))
        {
            if (picked.Count > MaxNodes - MinBranch)
            {
                break;
            }

            var stack = new Stack<string>([root]);
            while (picked.Count < MaxNodes && stack.TryPop(out var id))
            {
                picked.Add(id);
                foreach (var child in children[id].OrderByDescending(c => height[c]).Take(MaxChildren).Reverse())
                {
                    stack.Push(child);
                }
            }
        }

        return picked;
    }

    /// <param name="picked">From <see cref="Pick"/>.</param>
    /// <param name="items">The picked items, read from the snapshot.</param>
    /// <param name="keys">The Kupro key each picked item would get, where known.</param>
    public static HierarchySample Build(
        List<string> picked, IReadOnlyList<FitInput> inputs, IReadOnlyDictionary<string, SourceItem> items, IReadOnlyDictionary<string, string> types,
        Func<SourceItem, IssueState> state, IReadOnlyDictionary<string, string> keys, FitResult bottom, FitResult top)
    {
        var shown = picked.Where(items.ContainsKey).ToList();
        var set = shown.ToHashSet(StringComparer.Ordinal);
        var parents = inputs.Where(i => set.Contains(i.Id)).DistinctBy(i => i.Id).ToDictionary(i => i.Id, i => i.Parent, StringComparer.Ordinal);

        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var source = new List<SampleSourceNode>(shown.Count);
        foreach (var id in shown)
        {
            var item = items[id];
            depth[id] = parents[id] is { } p && depth.TryGetValue(p, out var d) ? d + 1 : 0;
            source.Add(new SampleSourceNode(id, item.Key, item.Title, types.GetValueOrDefault(item.Type) ?? item.Type, depth[id]));
        }

        List<SampleKuproNode> Land(FitResult fit)
        {
            var kids = shown.Where(id => fit.Items[id].Parent is { } p && set.Contains(p)).ToLookup(id => fit.Items[id].Parent!, StringComparer.Ordinal);
            var oldParents = fit.OldParents.Where(o => set.Contains(o.Item)).DistinctBy(o => o.Item).ToDictionary(o => o.Item, o => o.Parent, StringComparer.Ordinal);
            var labels = fit.FoldedTops.Where(t => set.Contains(t.Item)).ToDictionary(t => t.Item, t => t.Top, StringComparer.Ordinal);
            var promoted = fit.Promoted.Where(set.Contains).ToHashSet(StringComparer.Ordinal);

            var nodes = new List<SampleKuproNode>(shown.Count);
            var stack = new Stack<(string Id, int Depth)>(shown.Where(id => fit.Items[id].Parent is not { } p || !set.Contains(p)).Reverse().Select(id => (id, 0)));
            while (stack.TryPop(out var at))
            {
                var item = items[at.Id];
                nodes.Add(new SampleKuproNode(
                    at.Id, keys.GetValueOrDefault(at.Id), item.Title, fit.Items[at.Id].Type, state(item), at.Depth,
                    oldParents.GetValueOrDefault(at.Id), promoted.Contains(at.Id), labels.GetValueOrDefault(at.Id)));
                foreach (var child in kids[at.Id].Reverse())
                {
                    stack.Push((child, at.Depth + 1));
                }
            }

            return nodes;
        }

        return new HierarchySample(source, Land(bottom), Land(top));
    }
}
