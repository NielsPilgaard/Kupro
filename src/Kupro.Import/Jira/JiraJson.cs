using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.Jira;

// The parts of the Jira REST responses the connector reads: platform API v3 (Cloud) and v2 (Data Center and Server),
// and the Agile API for sprints. Source-generated, because the connector also runs in the AOT CLI. Unknown properties
// are ignored. Defaulted members are settable, not init: source generation assigns init members like constructor
// arguments, so an absent list would arrive as null instead of the empty default. Times stay strings: Jira writes
// offsets as +0100, which System.Text.Json doesn't read (see Jira.Time).

/// <summary>The token's user (<c>/myself</c>): JQL reads dates in its time zone.</summary>
internal sealed record JiraMyself
{
    /// <summary>An IANA zone, such as "Europe/Copenhagen".</summary>
    public string? TimeZone { get; init; }
}

internal sealed record JiraServerInfo
{
    public string? BaseUrl { get; init; }
    public string? Version { get; init; }

    /// <summary>"Cloud", or "Server" on Server and Data Center.</summary>
    public string? DeploymentType { get; init; }
}

internal sealed record JiraProject
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }

    /// <summary>"software", "business" or "service_desk".</summary>
    public string? ProjectTypeKey { get; init; }

    /// <summary>Cloud: "classic" (company-managed) or "next-gen" (team-managed).</summary>
    public string? Style { get; init; }
}

internal sealed record JiraProjectPage
{
    public IReadOnlyList<JiraProject> Values { get; set; } = [];
    public bool IsLast { get; init; } = true;
}

internal sealed record JiraIssueType
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Subtask { get; init; }

    /// <summary>Cloud only: -1 sub-task, 0 standard, 1 epic, 2 and up above epics (Premium).</summary>
    public int? HierarchyLevel { get; init; }
}

/// <summary>One issue type of a project with its workflow's statuses (<c>project/{id}/statuses</c>).</summary>
internal sealed record JiraTypeStatuses
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Subtask { get; init; }
    public IReadOnlyList<JiraStatus> Statuses { get; set; } = [];
}

internal sealed record JiraStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JiraStatusCategory? StatusCategory { get; init; }
}

internal sealed record JiraStatusCategory
{
    /// <summary>"new", "indeterminate" or "done".</summary>
    public string? Key { get; init; }
}

internal sealed record JiraField
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Custom { get; init; }
    public JiraSchema? Schema { get; init; }
}

internal sealed record JiraSchema
{
    public string? Type { get; init; }
    public string? Items { get; init; }

    /// <summary>The custom field type key, e.g. <c>com.pyxis.greenhopper.jira:gh-sprint</c>.</summary>
    public string? Custom { get; init; }

    public string? System { get; init; }
}

internal sealed record JiraResolution
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

/// <summary>Cloud's <c>resolution/search</c> page.</summary>
internal sealed record JiraResolutionPage
{
    public IReadOnlyList<JiraResolution> Values { get; set; } = [];
    public bool IsLast { get; init; } = true;
}

/// <summary>A link to something outside Jira (<c>issue/{id}/remotelink</c>): a Confluence page or a web page.</summary>
internal sealed record JiraRemoteLink
{
    public JiraRemoteObject? Object { get; init; }
}

internal sealed record JiraRemoteObject
{
    public string? Url { get; init; }
    public string? Title { get; init; }
}

internal sealed record JiraVersion
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Released { get; init; }
    public string? ReleaseDate { get; init; }
}

/// <summary>DC <c>/search</c> (startAt) and Cloud <c>/search/jql</c> (nextPageToken) in one shape.</summary>
internal sealed record JiraSearchPage
{
    public IReadOnlyList<JiraIssue> Issues { get; set; } = [];
    public int StartAt { get; init; }
    public int? Total { get; init; }
    public string? NextPageToken { get; init; }
    public bool? IsLast { get; init; }
}

internal sealed record JiraIssue
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public Dictionary<string, JsonElement> Fields { get; set; } = [];
    public JiraChangelog? Changelog { get; init; }
}

internal sealed record JiraChangelog
{
    public int StartAt { get; init; }
    public int Total { get; init; }
    public IReadOnlyList<JiraHistory> Histories { get; set; } = [];
}

/// <summary>Cloud's paged <c>issue/{id}/changelog</c>.</summary>
internal sealed record JiraChangelogPage
{
    public IReadOnlyList<JiraHistory> Values { get; set; } = [];
    public int StartAt { get; init; }
    public int Total { get; init; }
    public bool IsLast { get; init; } = true;
}

internal sealed record JiraHistory
{
    public required string Id { get; init; }
    public JsonElement Author { get; init; }
    public string? Created { get; init; }
    public IReadOnlyList<JiraHistoryItem> Items { get; set; } = [];
}

internal sealed record JiraHistoryItem
{
    public string? Field { get; init; }
    public string? FieldType { get; init; }

    /// <summary>Cloud and recent Data Center; older servers name the field only.</summary>
    public string? FieldId { get; init; }

    public string? From { get; init; }

    [JsonPropertyName("fromString")]
    public string? FromText { get; init; }

    public string? To { get; init; }

    [JsonPropertyName("toString")]
    public string? ToText { get; init; }
}

internal sealed record JiraCommentPage
{
    public IReadOnlyList<JiraComment> Comments { get; set; } = [];
    public int StartAt { get; init; }
    public int Total { get; init; }
}

internal sealed record JiraComment
{
    public required string Id { get; init; }
    public JsonElement Author { get; init; }

    /// <summary>ADF (an object) on v3, wiki markup (a string) on v2.</summary>
    public JsonElement Body { get; init; }

    public string? Created { get; init; }
    public string? Updated { get; init; }
}

internal sealed record JiraSprint
{
    public long Id { get; init; }
    public string? Name { get; init; }

    /// <summary>"future", "active" or "closed".</summary>
    public string? State { get; init; }

    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? CompleteDate { get; init; }
}

internal sealed record JiraIssueRef
{
    public required string Id { get; init; }
    public string? Key { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JiraServerInfo))]
[JsonSerializable(typeof(JiraMyself))]
[JsonSerializable(typeof(JiraProjectPage))]
[JsonSerializable(typeof(List<JiraProject>))]
[JsonSerializable(typeof(List<JiraIssueType>))]
[JsonSerializable(typeof(List<JiraTypeStatuses>))]
[JsonSerializable(typeof(List<JiraField>))]
[JsonSerializable(typeof(List<JiraVersion>))]
[JsonSerializable(typeof(List<JiraResolution>))]
[JsonSerializable(typeof(JiraResolutionPage))]
[JsonSerializable(typeof(List<JiraRemoteLink>))]
[JsonSerializable(typeof(JiraSearchPage))]
[JsonSerializable(typeof(JiraIssue))]
[JsonSerializable(typeof(JiraChangelogPage))]
[JsonSerializable(typeof(JiraCommentPage))]
[JsonSerializable(typeof(JiraComment))]
[JsonSerializable(typeof(JiraSprint))]
[JsonSerializable(typeof(JiraIssueRef))]
internal sealed partial class JiraJson : JsonSerializerContext;
