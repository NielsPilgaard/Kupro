using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.Snapshot;

// The Kupro import format, version 1 (ADR-019, docs/import-format.md). Public: the CLI, agents and customer scripts
// write it, so a change here is a format change. Adding an optional property is compatible; anything else needs a new
// format number. Every reference between records is the source's own ID as a string.
// Optional lists are settable, not init: source generation assigns init members like constructor arguments, so a
// script that leaves out "labels" would read back null instead of the empty default.

/// <summary>A record a connector streams while reading. Each kind goes into its own part of the snapshot.</summary>
public interface ISnapshotRecord;

/// <summary>Where the data came from. <see cref="Kind"/> is one of <see cref="SourceKinds"/>.</summary>
public sealed record SnapshotSource
{
    public required string Kind { get; init; }

    /// <summary>The source's base URL (Jira site, ADO collection), so links and aliases can be rewritten.</summary>
    public string? BaseUrl { get; init; }
}

/// <summary>Known values of <see cref="SnapshotSource.Kind"/>. A script may use its own; it is shown as given.</summary>
public static class SourceKinds
{
    public const string Snapshot = "snapshot";
    public const string Csv = "csv";
    public const string Kupro = "kupro";
    public const string Jira = "jira";
    public const string AzureDevOps = "azure-devops";
    public const string Linear = "linear";
    public const string Trello = "trello";
    public const string GitHub = "github";
    public const string Monday = "monday";
    public const string YouTrack = "youtrack";
}

/// <summary>What the wizard needs before the owner picks a scope and maps anything (connector Describe).</summary>
public sealed record SourceDescription
{
    public required SnapshotSource Source { get; init; }
    public IReadOnlyList<SourceProject> Projects { get; set; } = [];
    public IReadOnlyList<SourcePerson> People { get; set; } = [];
    public IReadOnlyList<SourceItemType> ItemTypes { get; set; } = [];
    public IReadOnlyList<SourceStatus> Statuses { get; set; } = [];
    public IReadOnlyList<SourceField> Fields { get; set; } = [];
}

/// <summary><c>snapshot.json</c>: the reference data and counts. Items and history are in the NDJSON parts.</summary>
public sealed record SnapshotHeader
{
    public required int Format { get; init; }
    public required SnapshotSource Source { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Opaque delta cursor for the next run (a time, a sync token). Null when the source has no delta.</summary>
    public string? Cursor { get; init; }

    public IReadOnlyList<SourceProject> Projects { get; set; } = [];
    public IReadOnlyList<SourcePerson> People { get; set; } = [];
    public IReadOnlyList<SourceItemType> ItemTypes { get; set; } = [];
    public IReadOnlyList<SourceStatus> Statuses { get; set; } = [];
    public IReadOnlyList<SourceField> Fields { get; set; } = [];
    public IReadOnlyList<SourceSprint> Sprints { get; set; } = [];
    public IReadOnlyList<SourceVersion> Versions { get; set; } = [];
    public SnapshotCounts Counts { get; set; } = new();
}

public sealed record SnapshotCounts
{
    public int Items { get; init; }
    public int Relations { get; init; }
    public int Comments { get; init; }
    public int Attachments { get; init; }
    public int Changes { get; init; }
}

/// <summary>A Jira project, ADO project, Linear team, Trello or Monday board.</summary>
public sealed record SourceProject
{
    public required string Id { get; init; }

    /// <summary>The source's project key (Jira <c>PROJ</c>), when it has one.</summary>
    public string? Key { get; init; }

    public required string Name { get; init; }
    public string? Url { get; init; }

    /// <summary>The source's process or template name (ADO "Agile", "Scrum"), for default mappings.</summary>
    public string? Process { get; init; }
}

/// <summary>A source user. Matched to a member by email, then by name, or kept as a placeholder.</summary>
public sealed record SourcePerson : ISnapshotRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Email { get; init; }
}

public sealed record SourceItemType
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Set when the type exists in one project only (ADO types are per process).</summary>
    public string? Project { get; init; }

    /// <summary>Hierarchy level as the source ranks it, higher is bigger (Jira hierarchy level, ADO backlog level).</summary>
    public int? Level { get; init; }
}

public sealed record SourceStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The source's own category, as the source names it (ADO "Proposed", Jira "indeterminate").</summary>
    public string? Category { get; init; }

    /// <summary>Set when the status belongs to one item type (ADO states are per type).</summary>
    public string? ItemType { get; init; }

    public string? Project { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FieldKind>))]
public enum FieldKind
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("date")] Date,
    [JsonStringEnumMemberName("select")] Select,
    [JsonStringEnumMemberName("multi-select")] MultiSelect,
    [JsonStringEnumMemberName("user")] User,
}

/// <summary>
/// A source field beyond the ones <see cref="SourceItem"/> has. Values in <see cref="SourceItem.Fields"/> are a
/// string (text, date as ISO 8601, select option, user ID), a number, or an array of strings (multi-select).
/// </summary>
public sealed record SourceField
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required FieldKind Kind { get; init; }
    public IReadOnlyList<string> Options { get; set; } = [];
}

/// <summary>A sprint or iteration. Without <see cref="Project"/> it can span projects; import splits it per project.</summary>
public sealed record SourceSprint : ISnapshotRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Project { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }

    /// <summary>True when the source has closed it.</summary>
    public bool Closed { get; init; }
}

/// <summary>A version or release. One with a date can become a milestone.</summary>
public sealed record SourceVersion : ISnapshotRecord
{
    public required string Id { get; init; }
    public required string Project { get; init; }
    public required string Name { get; init; }
    public DateOnly? Date { get; init; }
    public bool Released { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MarkupKind>))]
public enum MarkupKind
{
    [JsonStringEnumMemberName("plain")] Plain,
    [JsonStringEnumMemberName("markdown")] Markdown,
    [JsonStringEnumMemberName("html")] Html,

    /// <summary>Atlassian Document Format (Jira Cloud): the ADF JSON document as a string.</summary>
    [JsonStringEnumMemberName("adf")] Adf,

    [JsonStringEnumMemberName("jira-wiki")] JiraWiki,
}

/// <summary>A link from an item to something outside the source's items (commit, PR, build, web page).</summary>
public sealed record SourceLink
{
    public required string Url { get; init; }
    public string? Title { get; init; }
}

/// <summary>One work item. References (project, type, status, people, parent, sprint) are source IDs.</summary>
public sealed record SourceItem : ISnapshotRecord
{
    public required string Id { get; init; }

    /// <summary>The key as the source shows it (<c>PROJ-123</c>, <c>4567</c>). Kept where Kupro can (ADR-019 Keys).</summary>
    public string? Key { get; init; }

    public required string Project { get; init; }
    public required string Type { get; init; }
    public required string Status { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public MarkupKind DescriptionMarkup { get; init; } = MarkupKind.Plain;
    public string? Parent { get; init; }
    public string? Assignee { get; init; }
    public string? Creator { get; init; }

    /// <summary>The source's priority as it names it ("High", "2"); mapped per source.</summary>
    public string? Priority { get; init; }

    public IReadOnlyList<string> Labels { get; set; } = [];

    /// <summary>Values of <see cref="SourceField"/>s by field ID.</summary>
    public IReadOnlyDictionary<string, JsonElement> Fields { get; set; } = new Dictionary<string, JsonElement>();

    public long? EstimateSeconds { get; init; }
    public decimal? Points { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? Due { get; init; }
    public string? Sprint { get; init; }
    public IReadOnlyList<string> Versions { get; set; } = [];
    public IReadOnlyList<SourceLink> Links { get; set; } = [];
    public string? Url { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset Updated { get; init; }
    public DateTimeOffset? Resolved { get; init; }
}

/// <summary>Known values of <see cref="SourceRelation.Kind"/>; Kupro's relation kinds.</summary>
public static class RelationKinds
{
    /// <summary><see cref="SourceRelation.From"/> blocks <see cref="SourceRelation.To"/>.</summary>
    public const string Blocks = "blocks";

    public const string Relates = "relates";

    /// <summary><see cref="SourceRelation.From"/> duplicates <see cref="SourceRelation.To"/>.</summary>
    public const string Duplicates = "duplicates";
}

/// <summary>A link between two items. Parent/child is <see cref="SourceItem.Parent"/>, not a relation.</summary>
public sealed record SourceRelation : ISnapshotRecord
{
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>One of <see cref="RelationKinds"/>. A connector maps the source's link types onto them.</summary>
    public required string Kind { get; init; }

    /// <summary>The source's own link type name ("is cloned by"), kept for the details block.</summary>
    public string? Name { get; init; }
}

public sealed record SourceComment : ISnapshotRecord
{
    public required string Id { get; init; }
    public required string Item { get; init; }
    public string? Author { get; init; }
    public required string Body { get; init; }
    public MarkupKind Markup { get; init; } = MarkupKind.Plain;
    public required DateTimeOffset Created { get; init; }
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// An attachment. The bytes are either in the snapshot (<see cref="File"/>, the path inside the ZIP) or fetched from
/// the source through the connector when the import commits.
/// </summary>
public sealed record SourceAttachment : ISnapshotRecord
{
    public required string Id { get; init; }
    public required string Item { get; init; }
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    public string? Author { get; init; }
    public required DateTimeOffset Created { get; init; }

    /// <summary>The source URL. Markup that shows the file inline refers to it, so it is rewritten on import.</summary>
    public string? Url { get; init; }

    public string? File { get; init; }
}

/// <summary>
/// One field change from the source's history. <see cref="From"/>/<see cref="To"/> are IDs where the field holds a
/// reference (status, user, sprint) and raw values otherwise; the <c>Text</c> pair is what the source displays.
/// </summary>
public sealed record SourceChange : ISnapshotRecord
{
    /// <summary>Unique per item; re-runs skip changes they have seen.</summary>
    public required string Id { get; init; }

    public required string Item { get; init; }
    public string? Actor { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>A <see cref="SourceItem"/> property name ("status", "assignee") or a <see cref="SourceField"/> ID.</summary>
    public required string Field { get; init; }

    public string? From { get; init; }
    public string? To { get; init; }
    public string? FromText { get; init; }
    public string? ToText { get; init; }
}

/// <summary>The last record of a read: the cursor the next delta run starts from.</summary>
public sealed record ReadCursor(string? Cursor) : ISnapshotRecord;

/// <summary>What to read. Empty <see cref="Projects"/> reads every project the token can see.</summary>
public sealed record ReadScope
{
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>Skip items resolved before this time (the "closed items older than N months" filter).</summary>
    public DateTimeOffset? ResolvedAfter { get; init; }
}
