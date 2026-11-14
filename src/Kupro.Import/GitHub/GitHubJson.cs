using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.GitHub;

// GitHub REST v3 shapes (task 027), only the members the connector reads. GitHub writes snake_case.

internal sealed record GitHubUser
{
    public required long Id { get; init; }
    public required string Login { get; init; }

    /// <summary>"User", "Bot" or "Organization".</summary>
    public string? Type { get; init; }

    public string? Name { get; init; }
    public string? Email { get; init; }
}

internal sealed record GitHubRepo
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required GitHubUser Owner { get; init; }
    public string? HtmlUrl { get; init; }
    public bool HasIssues { get; init; } = true;
}

internal sealed record GitHubLabel
{
    public required string Name { get; init; }
}

internal sealed record GitHubMilestone
{
    public long Id { get; init; }
    public int Number { get; init; }
    public required string Title { get; init; }
    public string? State { get; init; }
    public DateTimeOffset? DueOn { get; init; }
}

/// <summary>An organisation's issue type (Bug, Feature, Task, or the org's own).</summary>
internal sealed record GitHubIssueType
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public bool? IsEnabled { get; init; }
}

internal sealed record GitHubSubIssuesSummary
{
    public int Total { get; init; }
}

internal sealed record GitHubIssue
{
    public required long Id { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }

    /// <summary>"open" or "closed".</summary>
    public required string State { get; init; }

    /// <summary>"completed", "not_planned", "duplicate" or "reopened"; null on old issues.</summary>
    public string? StateReason { get; init; }

    public GitHubUser? User { get; init; }
    public IReadOnlyList<GitHubUser> Assignees { get; set; } = [];
    public IReadOnlyList<GitHubLabel> Labels { get; set; } = [];
    public GitHubMilestone? Milestone { get; init; }
    public GitHubIssueType? Type { get; init; }
    public GitHubSubIssuesSummary? SubIssuesSummary { get; init; }

    /// <summary>Present on a pull request, which the issues list returns too.</summary>
    public JsonElement? PullRequest { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? HtmlUrl { get; init; }
}

internal sealed record GitHubComment
{
    public required long Id { get; init; }

    /// <summary>The API URL of the issue, ending in its number.</summary>
    public required string IssueUrl { get; init; }

    public GitHubUser? User { get; init; }
    public string? Body { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record GitHubRename
{
    public string? From { get; init; }
    public string? To { get; init; }
}

internal sealed record GitHubEventIssue
{
    public required long Id { get; init; }
    public required int Number { get; init; }
}

/// <summary>A repository issue event (<c>/issues/events</c>): labeled, assigned, closed, reopened, milestoned, renamed...</summary>
internal sealed record GitHubEvent
{
    public required long Id { get; init; }
    public required string Event { get; init; }
    public GitHubUser? Actor { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public GitHubLabel? Label { get; init; }
    public GitHubUser? Assignee { get; init; }
    public GitHubMilestone? Milestone { get; init; }
    public GitHubRename? Rename { get; init; }
    public string? StateReason { get; init; }
    public GitHubEventIssue? Issue { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubUser))]
[JsonSerializable(typeof(List<GitHubUser>))]
[JsonSerializable(typeof(List<GitHubRepo>))]
[JsonSerializable(typeof(List<GitHubIssueType>))]
[JsonSerializable(typeof(List<GitHubIssue>))]
[JsonSerializable(typeof(List<GitHubMilestone>))]
[JsonSerializable(typeof(List<GitHubComment>))]
[JsonSerializable(typeof(List<GitHubEvent>))]
internal sealed partial class GitHubJson : JsonSerializerContext;
