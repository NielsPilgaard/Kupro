using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.YouTrack;

// YouTrack REST shapes (task 027), only the members the connector asks for with `fields=`. Custom field values are
// polymorphic (an object, a list, a number), so they stay JsonElement.

internal sealed record YtRef
{
    public required string Id { get; init; }
}

internal sealed record YtUser
{
    public required string Id { get; init; }
    public string? Login { get; init; }
    public string? FullName { get; init; }
    public string? Email { get; init; }
}

internal sealed record YtProject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public bool Archived { get; init; }
}

internal sealed record YtFieldType
{
    /// <summary><c>enum[1]</c>, <c>enum[*]</c>, <c>state[1]</c>, <c>user[1]</c>, <c>date</c>, <c>integer</c>, <c>period</c>...</summary>
    public required string Id { get; init; }

    public bool IsMultiValue { get; init; }
}

internal sealed record YtField
{
    public required string Name { get; init; }
    public YtFieldType? FieldType { get; init; }
}

internal sealed record YtBundleValue
{
    public required string Name { get; init; }
    public bool? IsResolved { get; init; }
}

internal sealed record YtBundle
{
    public IReadOnlyList<YtBundleValue> Values { get; set; } = [];
}

internal sealed record YtProjectCustomField
{
    public required YtField Field { get; init; }
    public YtBundle? Bundle { get; init; }
}

internal sealed record YtIssueCustomField
{
    public required string Name { get; init; }
    public JsonElement? Value { get; init; }
}

internal sealed record YtTag
{
    public required string Name { get; init; }
}

internal sealed record YtLinkType
{
    public string? Name { get; init; }
    public bool Directed { get; init; }
    public bool Aggregation { get; init; }
    public string? SourceToTarget { get; init; }
    public string? TargetToSource { get; init; }
}

internal sealed record YtLink
{
    /// <summary>OUTWARD, INWARD or BOTH (an undirected link).</summary>
    public required string Direction { get; init; }

    public required YtLinkType LinkType { get; init; }
    public IReadOnlyList<YtRef> Issues { get; set; } = [];
}

internal sealed record YtComment
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public YtUser? Author { get; init; }
    public required long Created { get; init; }
    public long? Updated { get; init; }
    public bool Deleted { get; init; }
}

internal sealed record YtAttachment
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? MimeType { get; init; }
    public long? Size { get; init; }
    public string? Url { get; init; }
    public YtUser? Author { get; init; }
    public required long Created { get; init; }
    public bool Removed { get; init; }
}

internal sealed record YtIssue
{
    public required string Id { get; init; }
    public required string IdReadable { get; init; }
    public required string Summary { get; init; }
    public string? Description { get; init; }
    public required long Created { get; init; }
    public required long Updated { get; init; }
    public long? Resolved { get; init; }
    public YtUser? Reporter { get; init; }
    public required YtRef Project { get; init; }
    public IReadOnlyList<YtIssueCustomField> CustomFields { get; set; } = [];
    public IReadOnlyList<YtTag> Tags { get; set; } = [];
    public IReadOnlyList<YtLink> Links { get; set; } = [];
    public IReadOnlyList<YtComment> Comments { get; set; } = [];
    public IReadOnlyList<YtAttachment> Attachments { get; set; } = [];
}

internal sealed record YtActivityField
{
    public string? Presentation { get; init; }
}

internal sealed record YtCategory
{
    public required string Id { get; init; }
}

internal sealed record YtActivity
{
    public required string Id { get; init; }
    public required long Timestamp { get; init; }
    public YtUser? Author { get; init; }
    public YtActivityField? Field { get; init; }
    public YtCategory? Category { get; init; }
    public JsonElement? Added { get; init; }
    public JsonElement? Removed { get; init; }
}

internal sealed record YtActivityPage
{
    public IReadOnlyList<YtActivity> Activities { get; set; } = [];
    public bool HasAfter { get; init; }
    public string? AfterCursor { get; init; }
}

internal sealed record YtSprint
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public long? Start { get; init; }
    public long? Finish { get; init; }
    public bool Archived { get; init; }
    public IReadOnlyList<YtRef> Issues { get; set; } = [];
}

internal sealed record YtAgile
{
    public required string Id { get; init; }
    public IReadOnlyList<YtRef> Projects { get; set; } = [];
    public IReadOnlyList<YtSprint> Sprints { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<YtProject>))]
[JsonSerializable(typeof(List<YtProjectCustomField>))]
[JsonSerializable(typeof(List<YtUser>))]
[JsonSerializable(typeof(List<YtIssue>))]
[JsonSerializable(typeof(YtActivityPage))]
[JsonSerializable(typeof(List<YtAgile>))]
[JsonSerializable(typeof(YtSprint))]
internal sealed partial class YouTrackJson : JsonSerializerContext;
