using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Users;
using Kupro.Api.Features.Workspaces;

namespace Kupro.Api.Mcp;

/// <summary>Issue fields a row leaves out unless asked for.</summary>
public enum IssueRowField
{
    Id,
    AssigneeId,
    StartDate,
    DueDate,
    SprintId,
    Rank,
    CreatedAt,
    UpdatedAt,
}

/// <summary>An issue as agents read it in lists: a third of <see cref="IssueSummary"/>'s size. Optional fields stay null, and so out of the JSON, unless asked for.</summary>
public sealed record IssueRow(
    string Key,
    IssueType Type,
    string Title,
    IssueState State,
    IssuePriority Priority,
    string? Assignee,
    decimal? Effort,
    string? ParentKey,
    IReadOnlyList<string> Labels)
{
    public Guid? Id { get; init; }
    public Guid? AssigneeId { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public Guid? SprintId { get; init; }
    public double? Rank { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record IssueRowPage(IReadOnlyList<IssueRow> Items, string? NextCursor);

public enum ContextPart
{
    Projects,
    Members,
    Labels,
    Sprints,
    MyIssues,
}

public sealed record ContextProject(string Key, string Name, string? Description, int IssueCount);

public sealed record ContextMember(Guid Id, string Name, MemberKind Kind, WorkspaceRole Role);

public sealed record ContextSprint(Guid Id, string ProjectKey, string Name, DateOnly StartDate, DateOnly EndDate);

/// <param name="Workspace">The resolved workspace; null when you are in several and named none, as are the parts below it.</param>
/// <param name="MembersTotal">Set when <c>Members</c> is capped: how many there are in all.</param>
/// <param name="LabelsTotal">Set when <c>Labels</c> is capped: how many there are in all.</param>
/// <param name="MyIssuesCapped">More open issues are assigned to you than listed; search_issues has the rest.</param>
public sealed record ContextResponse(
    MeResponse Me,
    IReadOnlyList<WorkspaceSummary> Workspaces,
    WorkspaceResponse? Workspace = null,
    IReadOnlyList<ContextProject>? Projects = null,
    IReadOnlyList<ContextMember>? Members = null,
    int? MembersTotal = null,
    IReadOnlyList<string>? Labels = null,
    int? LabelsTotal = null,
    IReadOnlyList<ContextSprint>? Sprints = null,
    IReadOnlyList<IssueRow>? MyIssues = null,
    bool? MyIssuesCapped = null);

public enum IssueInclude
{
    Activity,
}

public sealed record IssueLookupError(string Key, string Code, string Detail);

/// <param name="Issues">The issues found, in the order asked.</param>
/// <param name="Activity">History and comments by issue key, oldest first, when asked for.</param>
/// <param name="Errors">One entry per key that could not be read.</param>
public sealed record IssueLookup(
    IReadOnlyList<IssueDetail> Issues,
    IReadOnlyDictionary<string, IReadOnlyList<FeedItem>>? Activity,
    IReadOnlyList<IssueLookupError>? Errors);

/// <summary>The one mapping from feature DTOs to what MCP tools return. View logic only: the feature services stay unaware of it.</summary>
public static class McpViews
{
    /// <summary>Most members and labels get_context lists, so a large workspace stays small.</summary>
    public const int ContextCap = 50;

    /// <summary>Most issues get_context lists under myIssues.</summary>
    public const int MyIssuesCap = 25;

    public static IssueRow Row(IssueSummary i, IReadOnlyCollection<IssueRowField>? fields = null)
    {
        var row = new IssueRow(i.Key, i.Type, i.Title, i.State, i.Priority, i.Assignee?.Name, i.Effort, i.ParentKey, i.Labels);
        return fields is not { Count: > 0 }
            ? row
            : row with
            {
                Id = fields.Contains(IssueRowField.Id) ? i.Id : null,
                AssigneeId = fields.Contains(IssueRowField.AssigneeId) ? i.Assignee?.Id : null,
                StartDate = fields.Contains(IssueRowField.StartDate) ? i.StartDate : null,
                DueDate = fields.Contains(IssueRowField.DueDate) ? i.DueDate : null,
                SprintId = fields.Contains(IssueRowField.SprintId) ? i.SprintId : null,
                Rank = fields.Contains(IssueRowField.Rank) ? i.Rank : null,
                CreatedAt = fields.Contains(IssueRowField.CreatedAt) ? i.CreatedAt : null,
                UpdatedAt = fields.Contains(IssueRowField.UpdatedAt) ? i.UpdatedAt : null,
            };
    }

    public static IReadOnlyList<IssueRow> Rows(IEnumerable<IssueSummary> issues) => issues.Select(i => Row(i)).ToList();

    public static ContextProject Project(ProjectResponse p) => new(p.Key, p.Name, p.Description, p.IssueCount);

    /// <summary>People you can assign: active ones, humans before agents so a cap can't push them out.</summary>
    public static (IReadOnlyList<ContextMember> Members, int? Total) Members(IReadOnlyList<MemberResponse> members)
    {
        var active = members.Where(m => m.Status == MemberStatus.Active).OrderBy(m => m.Kind).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        return (active.Take(ContextCap).Select(m => new ContextMember(m.Id, m.Name, m.Kind, m.Role)).ToList(),
            active.Count > ContextCap ? active.Count : null);
    }

    /// <summary>Label names, most used first.</summary>
    public static (IReadOnlyList<string> Labels, int? Total) Labels(IReadOnlyList<LabelResponse> labels) =>
        (labels.OrderByDescending(l => l.IssueCount).ThenBy(l => l.Name, StringComparer.Ordinal).Take(ContextCap).Select(l => l.Name).ToList(),
            labels.Count > ContextCap ? labels.Count : null);

    public static ContextSprint Sprint(SprintResponse s) => new(s.Id, s.ProjectKey, s.Name, s.StartDate, s.EndDate);
}
