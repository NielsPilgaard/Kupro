using System.ComponentModel.DataAnnotations;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Issues;

/// <param name="Project">Project key, e.g. <c>KUP</c>.</param>
/// <param name="Title">One-line title.</param>
/// <param name="Type">Epic, feature or task (default).</param>
/// <param name="Description">Markdown.</param>
/// <param name="State">Workflow state; default backlog.</param>
/// <param name="Priority">Default none.</param>
/// <param name="Assignee">Member id, or <c>me</c>.</param>
/// <param name="Parent">Parent issue key: a feature or epic (tasks), or an epic (features). In a bulk create, <c>#i</c>
/// points to the i-th issue (0-based) earlier in the same request, so an epic and its children go in one call.</param>
/// <param name="Effort">Estimate in hours.</param>
/// <param name="StartDate">Planned start (drives the delivery plan).</param>
/// <param name="DueDate">Planned delivery date.</param>
/// <param name="Labels">Label names; unknown names are created.</param>
/// <param name="Sprint">Sprint id, or <c>active</c> for the project's active sprint.</param>
public sealed record CreateIssueRequest(
    [Required, StringLength(10)] string Project,
    [Required, StringLength(500, MinimumLength = 1)] string Title,
    IssueType Type = IssueType.Task,
    [StringLength(100_000)] string? Description = null,
    IssueState State = IssueState.Backlog,
    IssuePriority Priority = IssuePriority.None,
    [StringLength(40)] string? Assignee = null,
    [StringLength(24)] string? Parent = null,
    [Range(0, 100_000)] decimal? Effort = null,
    DateOnly? StartDate = null,
    DateOnly? DueDate = null,
    [MaxLength(20)] IReadOnlyList<string>? Labels = null,
    [StringLength(40)] string? Sprint = null);

/// <param name="Issues">Up to 100 issues, created all-or-nothing.</param>
public sealed record CreateIssuesRequest([Required, MinLength(1), MaxLength(100)] IReadOnlyList<CreateIssueRequest> Issues);

/// <summary>Fields that can be cleared with <c>clear</c> in an update.</summary>
public enum IssueField
{
    Description,
    Assignee,
    Parent,
    Effort,
    StartDate,
    DueDate,
    Sprint,
}

/// <summary>Only the fields you set change. To empty a field, list it in <c>clear</c>.</summary>
/// <param name="Title">New title.</param>
/// <param name="Type">New type; must stay valid for the parent and children.</param>
/// <param name="Description">New Markdown description.</param>
/// <param name="State">New workflow state.</param>
/// <param name="Priority">New priority.</param>
/// <param name="Assignee">Member id, or <c>me</c>.</param>
/// <param name="Parent">New parent issue key.</param>
/// <param name="Effort">New estimate in hours.</param>
/// <param name="StartDate">New start date.</param>
/// <param name="DueDate">New due date.</param>
/// <param name="Labels">Replace all labels with these names.</param>
/// <param name="AddLabels">Label names to add.</param>
/// <param name="RemoveLabels">Label names to remove.</param>
/// <param name="Sprint">Sprint id, or <c>active</c> for the project's active sprint. Clear with <c>clear: [sprint]</c>.</param>
/// <param name="Clear">Fields to empty.</param>
public sealed record UpdateIssueRequest(
    [StringLength(500, MinimumLength = 1)] string? Title = null,
    IssueType? Type = null,
    [StringLength(100_000)] string? Description = null,
    IssueState? State = null,
    IssuePriority? Priority = null,
    [StringLength(40)] string? Assignee = null,
    [StringLength(24)] string? Parent = null,
    [Range(0, 100_000)] decimal? Effort = null,
    DateOnly? StartDate = null,
    DateOnly? DueDate = null,
    [MaxLength(20)] IReadOnlyList<string>? Labels = null,
    [MaxLength(20)] IReadOnlyList<string>? AddLabels = null,
    [MaxLength(20)] IReadOnlyList<string>? RemoveLabels = null,
    IReadOnlyList<IssueField>? Clear = null,
    [StringLength(40)] string? Sprint = null);

/// <param name="Key">Issue to update.</param>
/// <param name="Changes">Changes for that issue.</param>
public sealed record IssueUpdate([Required, StringLength(24)] string Key, [Required] UpdateIssueRequest Changes);

/// <param name="Items">Up to 100 updates, applied all-or-nothing.</param>
public sealed record UpdateIssuesRequest([Required, MinLength(1), MaxLength(100)] IReadOnlyList<IssueUpdate> Items);

/// <summary>Board move: optional new state and a position between two neighbours in the target column.</summary>
/// <param name="State">Target column; default the current state.</param>
/// <param name="After">Key of the issue that should end up directly above.</param>
/// <param name="Before">Key of the issue that should end up directly below.</param>
public sealed record MoveIssueRequest(
    IssueState? State = null, [StringLength(24)] string? After = null, [StringLength(24)] string? Before = null);

/// <summary>Relation as seen from one issue.</summary>
public enum RelationKind
{
    Blocks,
    BlockedBy,
    Relates,
    Duplicates,
    DuplicatedBy,
}

/// <param name="Type">How this issue relates to the target.</param>
/// <param name="Target">Key of the other issue.</param>
public sealed record AddRelationRequest(RelationKind Type, [Required, StringLength(24)] string Target);

public enum IssueSort
{
    /// <summary>Most recently updated first.</summary>
    Updated,

    /// <summary>Newest first.</summary>
    Created,

    /// <summary>Most urgent first.</summary>
    Priority,

    /// <summary>Manual board order.</summary>
    Rank,

    /// <summary>Best match for the search text first; the default when <c>q</c> has free text.</summary>
    Relevance,
}

/// <summary>Filters combine with AND; repeated values of one filter combine with OR.</summary>
public sealed class IssueQuery
{
    /// <summary>
    /// Search: free text (typo tolerant, English and Danish stemming) plus filters such as
    /// <c>type:epic state:todo,inProgress assignee:me sprint:current label:bug project:KUP parent:KUP-1</c>.
    /// Combines with the other parameters by AND.
    /// </summary>
    [FromQuery(Name = "q"), StringLength(500)]
    public string? Q { get; init; }

    /// <summary>Project keys.</summary>
    [FromQuery(Name = "project")]
    public string[]? Project { get; init; }

    [FromQuery(Name = "state")]
    public IssueState[]? State { get; init; }

    [FromQuery(Name = "type")]
    public IssueType[]? Type { get; init; }

    /// <summary>Member id, <c>me</c>, or <c>none</c> for unassigned.</summary>
    [FromQuery(Name = "assignee")]
    public string? Assignee { get; init; }

    /// <summary>Label name.</summary>
    [FromQuery(Name = "label")]
    public string? Label { get; init; }

    /// <summary>Sprint id, <c>active</c> (each project's active sprint) or <c>none</c>.</summary>
    [FromQuery(Name = "sprint")]
    public string? Sprint { get; init; }

    /// <summary>Parent issue key: its direct children.</summary>
    [FromQuery(Name = "parent")]
    public string? Parent { get; init; }

    /// <summary>Default: <c>relevance</c> when <c>q</c> has free text, otherwise <c>updated</c>.</summary>
    [FromQuery(Name = "sort")]
    public IssueSort? Sort { get; init; }

    /// <summary>Page size, 1-200 (default 50).</summary>
    [FromQuery(Name = "limit"), Range(1, 200)]
    public int Limit { get; init; } = 50;

    /// <summary><c>nextCursor</c> from the previous page.</summary>
    [FromQuery(Name = "cursor")]
    public string? Cursor { get; init; }
}

/// <param name="Id">Member id.</param>
/// <param name="Name">Display name, or the agent name.</param>
/// <param name="Kind">Human or agent.</param>
public sealed record MemberRef(Guid Id, string Name, MemberKind Kind);

/// <param name="Key">Issue key.</param>
/// <param name="Title">Title.</param>
/// <param name="Type">Type.</param>
/// <param name="State">State.</param>
public sealed record IssueRef(string Key, string Title, IssueType Type, IssueState State);

public sealed record IssueSummary(
    Guid Id,
    string Key,
    IssueType Type,
    string Title,
    IssueState State,
    IssuePriority Priority,
    MemberRef? Assignee,
    string? ParentKey,
    decimal? Effort,
    DateOnly? StartDate,
    DateOnly? DueDate,
    IReadOnlyList<string> Labels,
    double Rank,
    Guid? SprintId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Items">Issues on this page.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record IssuePage(IReadOnlyList<IssueSummary> Items, string? NextCursor);

/// <param name="Id">Relation id, used to remove it.</param>
/// <param name="Type">Relation from this issue's point of view.</param>
/// <param name="Issue">The other issue.</param>
public sealed record IssueRelationResponse(Guid Id, RelationKind Type, IssueRef Issue);

/// <param name="ChildEffort">Sum of the children's effort, shown beside the issue's own estimate.</param>
public sealed record IssueDetail(
    Guid Id,
    string Key,
    string ProjectKey,
    IssueType Type,
    string Title,
    string? Description,
    IssueState State,
    IssuePriority Priority,
    MemberRef? Assignee,
    IssueRef? Parent,
    decimal? Effort,
    decimal? ChildEffort,
    DateOnly? StartDate,
    DateOnly? DueDate,
    IReadOnlyList<string> Labels,
    IReadOnlyList<IssueRef> Children,
    IReadOnlyList<IssueRelationResponse> Relations,
    double Rank,
    Guid? SprintId,
    MemberRef CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <param name="Name">Label name (lowercase).</param>
/// <param name="Color">Display colour.</param>
/// <param name="IssueCount">Issues carrying the label.</param>
public sealed record LabelResponse(Guid Id, string Name, ProjectColor Color, int IssueCount);

/// <param name="Name">New name.</param>
/// <param name="Color">New colour.</param>
public sealed record UpdateLabelRequest([StringLength(50, MinimumLength = 1)] string? Name = null, ProjectColor? Color = null);

/// <summary>A failed issue operation: a stable code (Problem Details <c>code</c>) and a human-readable detail.</summary>
public sealed record IssueError(string Code, string Detail)
{
    public static IssueError NotFound(string key) => new("issue_not_found", $"No issue {key}.");
}

/// <summary>Value or error, for service methods with several failure cases.</summary>
public readonly record struct IssueResult<T>(T? Value, IssueError? Error)
{
    public static implicit operator IssueResult<T>(T value) => new(value, null);
    public static implicit operator IssueResult<T>(IssueError error) => new(default, error);
}
