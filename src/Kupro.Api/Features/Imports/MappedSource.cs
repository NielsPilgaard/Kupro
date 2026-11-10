using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// One item through the mapping: the plan and the commit both ask here, so a plan can't promise what a commit doesn't
/// write. Anything the saved mapping doesn't name (it appeared after the mapping was saved) gets the proposed default.
/// </summary>
public sealed class MappedSource
{
    private readonly ImportMapping _mapping;
    private readonly DateTimeOffset? _cutoff;
    private readonly Dictionary<string, SourceItemType> _types;
    private readonly Dictionary<string, SourceProject> _projects;

    public MappedSource(SnapshotHeader header, ImportMapping mapping, DateTimeOffset now)
    {
        _mapping = mapping;
        _cutoff = mapping.Scope.ClosedOlderThanMonths is { } months ? now.AddMonths(-months) : null;
        _types = header.ItemTypes.DistinctBy(t => t.Id).ToDictionary(t => t.Id, StringComparer.Ordinal);
        _projects = header.Projects.DistinctBy(p => p.Id).ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public bool InScope(SourceItem item) =>
        (_mapping.Scope.Projects.Count == 0 || _mapping.Scope.Projects.Contains(item.Project))
        && !(item.Resolved < _cutoff);

    public FitInput Input(SourceItem item)
    {
        var target = Type(item);
        return new FitInput(item.Id, target.Type, item.Parent, _types.GetValueOrDefault(item.Type)?.Level, target.Checklist);
    }

    public TypeTarget Type(SourceItem item) =>
        _mapping.Types.GetValueOrDefault(item.Type) ?? ImportMappingDefaults.ProposeType(_types.GetValueOrDefault(item.Type)?.Name ?? item.Type);

    public ProjectTarget Project(string sourceProject) =>
        _mapping.Projects.GetValueOrDefault(sourceProject)
        ?? ImportMappingDefaults.ProposeProject(_projects.GetValueOrDefault(sourceProject) ?? new SourceProject { Id = sourceProject, Name = sourceProject });

    /// <summary>A status with no state yet is Todo: no item is skipped for its status (ADR-019 States).</summary>
    public IssueState State(SourceItem item) => _mapping.States.TryGetValue(item.Status, out var state) ? state : IssueState.Todo;

    public IssuePriority Priority(SourceItem item) =>
        item.Priority is { } name && _mapping.Priorities.TryGetValue(name, out var priority) ? priority : ImportMappingDefaults.ProposePriority(item.Priority);

    /// <summary>Hours: the source's estimate first, else points times the owner's factor, else none (ADR-019 Effort).</summary>
    public decimal? Effort(SourceItem item)
    {
        if (item.EstimateSeconds is { } seconds)
        {
            return Math.Round(seconds / 3600m, 2);
        }

        return item.Points is { } points && _mapping.HoursPerPoint is { } factor ? points * factor : null;
    }
}
