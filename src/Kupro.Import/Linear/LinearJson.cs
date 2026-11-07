using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.Linear;

// The parts of Linear's GraphQL responses the connector reads. Source-generated, because the connector also runs in
// the AOT CLI. Unknown properties are ignored, so new Linear fields still parse.
// Defaulted members are settable, not init: source generation assigns init members like constructor arguments, so an
// absent "nodes" or "labelIds" would arrive as null instead of the empty default. Lists Linear sends as an explicit
// null (a history entry's "addedLabelIds" when no label changed) are nullable instead.

/// <summary>A GraphQL response: <c>data</c> holds whichever root fields the query asked for.</summary>
internal sealed record LinearResponse
{
    public LinearData? Data { get; init; }
    public List<LinearError> Errors { get; set; } = [];
}

internal sealed record LinearError
{
    public string? Message { get; init; }
    public LinearErrorExtensions? Extensions { get; init; }
}

/// <summary>Linear puts its error code (<c>AUTHENTICATION_ERROR</c>, <c>FORBIDDEN</c>, <c>RATELIMITED</c>) here.</summary>
internal sealed record LinearErrorExtensions
{
    public string? Code { get; init; }
    public string? Type { get; init; }
    public string? UserPresentableMessage { get; init; }
}

/// <summary>Every root field the connector queries; each query fills one or two.</summary>
internal sealed record LinearData
{
    public LinearOrganization? Organization { get; init; }
    public LinearConnection<LinearTeam>? Teams { get; init; }
    public LinearConnection<LinearState>? WorkflowStates { get; init; }
    public LinearConnection<LinearUser>? Users { get; init; }
    public LinearConnection<LinearLabel>? IssueLabels { get; init; }
    public LinearConnection<LinearProjectStatus>? ProjectStatuses { get; init; }
    public LinearConnection<LinearCycle>? Cycles { get; init; }
    public LinearConnection<LinearProject>? Projects { get; init; }
    public LinearConnection<LinearIssue>? Issues { get; init; }
    public LinearConnection<LinearComment>? Comments { get; init; }
    public LinearIssue? Issue { get; init; }
}

/// <summary>A Relay connection: one page of nodes and where the next starts.</summary>
internal sealed record LinearConnection<T>
{
    public List<T> Nodes { get; set; } = [];
    public LinearPageInfo PageInfo { get; set; } = new();
}

internal sealed record LinearPageInfo
{
    public bool HasNextPage { get; init; }
    public string? EndCursor { get; init; }
}

/// <summary>A reference to another entity; the connector only asks for its ID.</summary>
internal sealed record LinearRef
{
    public required string Id { get; init; }
}

internal sealed record LinearNamed
{
    public required string Name { get; init; }
}

internal sealed record LinearOrganization
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The workspace's URL slug: <c>linear.app/{urlKey}</c>.</summary>
    public required string UrlKey { get; init; }
}

internal sealed record LinearTeam
{
    public required string Id { get; init; }

    /// <summary>The issue key prefix (<c>ENG</c> in <c>ENG-123</c>).</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
}

internal sealed record LinearState
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>triage, backlog, unstarted, started, completed or canceled.</summary>
    public required string Type { get; init; }

    public double Position { get; init; }
    public LinearRef? Team { get; init; }
}

internal sealed record LinearUser
{
    public required string Id { get; init; }

    /// <summary>The full name.</summary>
    public required string Name { get; init; }

    /// <summary>The handle mentions use.</summary>
    public string? DisplayName { get; init; }

    public string? Email { get; init; }
    public bool Active { get; init; }
}

internal sealed record LinearLabel
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>A label group: it holds labels and is never on an issue itself.</summary>
    public bool IsGroup { get; init; }

    public LinearRef? Parent { get; init; }

    /// <summary>Null for a workspace label.</summary>
    public LinearRef? Team { get; init; }
}

internal sealed record LinearProjectStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>backlog, planned, started, paused, completed or canceled.</summary>
    public required string Type { get; init; }

    public double Position { get; init; }
}

internal sealed record LinearCycle
{
    public required string Id { get; init; }
    public int Number { get; init; }

    /// <summary>Optional in Linear; the UI then says "Cycle {number}".</summary>
    public string? Name { get; init; }

    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

internal sealed record LinearProject
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The one-line summary.</summary>
    public string? Description { get; init; }

    /// <summary>The project's Markdown body.</summary>
    public string? Content { get; init; }

    public string? Url { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? TargetDate { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CanceledAt { get; init; }
    public int Priority { get; init; }
    public bool? Trashed { get; init; }
    public LinearRef? Status { get; init; }
    public LinearRef? Lead { get; init; }
    public LinearRef? Creator { get; init; }
    public LinearConnection<LinearRef> Teams { get; set; } = new();
    public LinearConnection<LinearNamed> Labels { get; set; } = new();
    public LinearConnection<LinearNamed> Initiatives { get; set; } = new();
    public LinearConnection<LinearMilestone> ProjectMilestones { get; set; } = new();
}

internal sealed record LinearMilestone
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateOnly? TargetDate { get; init; }

    /// <summary>unstarted, next, overdue or done.</summary>
    public string? Status { get; init; }
}

internal sealed record LinearIssue
{
    public required string Id { get; init; }

    /// <summary>The key, <c>ENG-123</c>.</summary>
    public string? Identifier { get; init; }

    /// <summary>Not required: the queries that page an issue's nested lists ask only for its ID.</summary>
    public string Title { get; set; } = "";

    public string? Description { get; init; }

    /// <summary>0 no priority, 1 urgent, 2 high, 3 medium, 4 low.</summary>
    public double Priority { get; init; }

    public decimal? Estimate { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CanceledAt { get; init; }
    public bool? Trashed { get; init; }
    public List<string> LabelIds { get; set; } = [];
    public LinearRef? Team { get; init; }
    public LinearRef? State { get; init; }
    public LinearRef? Assignee { get; init; }
    public LinearRef? Creator { get; init; }
    public LinearRef? Parent { get; init; }
    public LinearRef? Project { get; init; }
    public LinearRef? ProjectMilestone { get; init; }
    public LinearRef? Cycle { get; init; }
    public LinearConnection<LinearRelation> Relations { get; set; } = new();
    public LinearConnection<LinearAttachment> Attachments { get; set; } = new();
    public LinearConnection<LinearHistory> History { get; set; } = new();
}

internal sealed record LinearRelation
{
    public required string Id { get; init; }

    /// <summary>blocks, duplicate, related or similar, from this issue to <see cref="RelatedIssue"/>.</summary>
    public required string Type { get; init; }

    public LinearRef? RelatedIssue { get; init; }
}

/// <summary>An issue attachment: a link (pull request, Slack thread, web page) or a file on Linear's upload host.</summary>
internal sealed record LinearAttachment
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? SourceType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public LinearRef? Creator { get; init; }
}

/// <summary>One issue history entry: only the pairs of what changed are set.</summary>
internal sealed record LinearHistory
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? ActorId { get; init; }
    public string? FromStateId { get; init; }
    public string? ToStateId { get; init; }
    public string? FromAssigneeId { get; init; }
    public string? ToAssigneeId { get; init; }
    public double? FromPriority { get; init; }
    public double? ToPriority { get; init; }
    public decimal? FromEstimate { get; init; }
    public decimal? ToEstimate { get; init; }
    public string? FromCycleId { get; init; }
    public string? ToCycleId { get; init; }
    public string? FromProjectId { get; init; }
    public string? ToProjectId { get; init; }
    public string? FromParentId { get; init; }
    public string? ToParentId { get; init; }
    public string? FromTeamId { get; init; }
    public string? ToTeamId { get; init; }
    public List<string>? AddedLabelIds { get; init; }
    public List<string>? RemovedLabelIds { get; init; }
    public string? FromTitle { get; init; }
    public string? ToTitle { get; init; }
    public DateOnly? FromDueDate { get; init; }
    public DateOnly? ToDueDate { get; init; }
    public bool? UpdatedDescription { get; init; }
}

internal sealed record LinearComment
{
    public required string Id { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Set when the author edited the text; <see cref="UpdatedAt"/> also moves on reactions.</summary>
    public DateTimeOffset? EditedAt { get; init; }

    public string? IssueId { get; init; }

    /// <summary>The thread's first comment, for a reply.</summary>
    public string? ParentId { get; init; }

    /// <summary>Null for an integration or bot comment.</summary>
    public LinearRef? User { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LinearResponse))]
internal sealed partial class LinearJson : JsonSerializerContext;
