using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.Trello;

// The parts of the Trello REST responses (API 1) the connector reads. Source-generated, because the connector also
// runs in the AOT CLI. Unknown properties are ignored, so new Trello fields still parse.
// Defaulted members are settable, not init: source generation assigns init members like constructor arguments, so an
// absent "idMembers" or "checkItems" would arrive as null instead of the empty default.

internal sealed record TrelloMember
{
    public required string Id { get; init; }
    public string? FullName { get; init; }
    public string? Username { get; init; }

    /// <summary>Only on <c>members/me</c>, and only when the token may read the account.</summary>
    public string? Email { get; init; }
}

internal sealed record TrelloBoard
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public bool Closed { get; init; }
}

internal sealed record TrelloList
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Closed { get; init; }
    public double Pos { get; init; }
}

internal sealed record TrelloLabel
{
    public required string Id { get; init; }

    /// <summary>Empty for a colour-only label.</summary>
    public string? Name { get; init; }

    /// <summary>Trello's colour name ("green", "sky_dark"); null for a colourless label.</summary>
    public string? Color { get; init; }
}

internal sealed record TrelloCustomField
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>"text", "number", "date", "checkbox" or "list".</summary>
    public required string Type { get; init; }

    public double Pos { get; init; }
    public IReadOnlyList<TrelloCustomFieldOption> Options { get; set; } = [];
}

internal sealed record TrelloCustomFieldOption
{
    public required string Id { get; init; }
    public TrelloFieldValue? Value { get; init; }
    public double Pos { get; init; }
}

/// <summary>A custom field value: one of the four is set, always as a string ("12.5", "true").</summary>
internal sealed record TrelloFieldValue
{
    public string? Text { get; init; }
    public string? Number { get; init; }
    public string? Date { get; init; }
    public string? Checked { get; init; }
}

internal sealed record TrelloCard
{
    public required string Id { get; init; }
    public int IdShort { get; init; }
    public required string Name { get; init; }
    public string? Desc { get; init; }
    public required string IdList { get; init; }
    public IReadOnlyList<string> IdLabels { get; set; } = [];
    public IReadOnlyList<string> IdMembers { get; set; } = [];
    public bool Closed { get; init; }
    public DateTimeOffset? Due { get; init; }
    public DateTimeOffset? Start { get; init; }
    public bool DueComplete { get; init; }
    public DateTimeOffset DateLastActivity { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<TrelloMember> Members { get; set; } = [];
    public IReadOnlyList<TrelloAttachment> Attachments { get; set; } = [];
    public IReadOnlyList<TrelloChecklist> Checklists { get; set; } = [];
    public IReadOnlyList<TrelloCustomFieldItem> CustomFieldItems { get; set; } = [];
}

internal sealed record TrelloAttachment
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Url { get; init; }
    public long? Bytes { get; init; }
    public string? MimeType { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? IdMember { get; init; }

    /// <summary>True for a file uploaded to Trello; false for a link (a URL, another card, a Drive file).</summary>
    public bool IsUpload { get; init; }

    public string? FileName { get; init; }
}

internal sealed record TrelloChecklist
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public double Pos { get; init; }
    public IReadOnlyList<TrelloCheckItem> CheckItems { get; set; } = [];
}

internal sealed record TrelloCheckItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>"complete" or "incomplete".</summary>
    public required string State { get; init; }

    public double Pos { get; init; }
    public DateTimeOffset? Due { get; init; }
    public string? IdMember { get; init; }
}

internal sealed record TrelloCustomFieldItem
{
    public required string IdCustomField { get; init; }
    public TrelloFieldValue? Value { get; init; }

    /// <summary>The picked option of a list field.</summary>
    public string? IdValue { get; init; }
}

internal sealed record TrelloAction
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? IdMemberCreator { get; init; }
    public TrelloMember? MemberCreator { get; init; }
    public TrelloActionData Data { get; set; } = new();
}

/// <summary>
/// An action's <c>data</c>. <see cref="Card"/> holds the card's new values of the fields an <c>updateCard</c> changed,
/// <see cref="Old"/> the old ones, so both stay loose.
/// </summary>
internal sealed record TrelloActionData
{
    public string? Text { get; init; }
    public DateTimeOffset? DateLastEdited { get; init; }
    public Dictionary<string, JsonElement> Card { get; set; } = [];
    public Dictionary<string, JsonElement> Old { get; set; } = [];
    public TrelloNamed? ListBefore { get; init; }
    public TrelloNamed? ListAfter { get; init; }
    public TrelloNamed? Member { get; init; }
    public TrelloLabel? Label { get; init; }
    public TrelloNamed? CustomField { get; init; }
    public TrelloCustomFieldItem? CustomFieldItem { get; init; }
    public TrelloCheckItem? CheckItem { get; init; }
}

internal sealed record TrelloNamed
{
    public required string Id { get; init; }
    public string? Name { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TrelloMember))]
[JsonSerializable(typeof(TrelloBoard))]
[JsonSerializable(typeof(List<TrelloBoard>))]
[JsonSerializable(typeof(List<TrelloList>))]
[JsonSerializable(typeof(List<TrelloLabel>))]
[JsonSerializable(typeof(List<TrelloMember>))]
[JsonSerializable(typeof(List<TrelloCustomField>))]
[JsonSerializable(typeof(List<TrelloCard>))]
[JsonSerializable(typeof(List<TrelloAction>))]
internal sealed partial class TrelloJson : JsonSerializerContext;
