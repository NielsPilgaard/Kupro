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

    /// <summary>The Kupro type a source type ID maps to, for a change in the history.</summary>
    public IssueType TypeOf(string sourceType) =>
        (_mapping.Types.GetValueOrDefault(sourceType) ?? ImportMappingDefaults.ProposeType(_types.GetValueOrDefault(sourceType)?.Name ?? sourceType)).Type;

    /// <summary>A status with no state yet is Todo: no item is skipped for its status (ADR-019 States).</summary>
    public IssueState State(SourceItem item) => State(item.Status);

    public IssueState State(string status) => _mapping.States.TryGetValue(status, out var state) ? state : IssueState.Todo;

    public IssuePriority Priority(SourceItem item) => Priority(item.Priority);

    public IssuePriority Priority(string? name) =>
        name is not null && _mapping.Priorities.TryGetValue(name, out var priority) ? priority : ImportMappingDefaults.ProposePriority(name);

    /// <summary>Hours: the source's estimate first, else points times the owner's factor, else none (ADR-019 Effort).</summary>
    public decimal? Effort(SourceItem item)
    {
        if (item.EstimateSeconds is { } seconds)
        {
            return EffortOfSeconds(seconds);
        }

        return item.Points is { } points ? EffortOfPoints(points) : null;
    }

    public static decimal EffortOfSeconds(long seconds) => Math.Round(seconds / 3600m, 2);

    /// <summary>Null without the owner's hours-per-point factor: points alone are no effort.</summary>
    public decimal? EffortOfPoints(decimal points) => _mapping.HoursPerPoint is { } factor ? points * factor : null;
}
