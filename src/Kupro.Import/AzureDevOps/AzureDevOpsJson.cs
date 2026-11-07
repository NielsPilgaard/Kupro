using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.AzureDevOps;

// The parts of the Azure DevOps REST responses the connector reads (Work Item Tracking, Core, Work). Source-generated,
// because the connector also runs in the AOT CLI. Unknown properties are ignored, so newer API versions still parse.
// Defaulted members are settable, not init: source generation assigns init members like constructor arguments, so an
// absent "children" or "relations" would arrive as null instead of the empty default.

internal sealed record AdoList<T>
{
    public IReadOnlyList<T> Value { get; set; } = [];
}

internal sealed record AdoProject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? State { get; init; }
    public AdoCapabilities? Capabilities { get; init; }
}

internal sealed record AdoCapabilities
{
    public AdoProcessTemplate? ProcessTemplate { get; init; }
}

internal sealed record AdoProcessTemplate
{
    public string? TemplateName { get; init; }
    public string? TemplateTypeId { get; init; }
}

internal sealed record AdoProcess
{
    public string? TypeId { get; init; }
    public string? Name { get; init; }
    public string? ParentProcessTypeId { get; init; }
}

internal sealed record AdoWorkItemType
{
    public required string Name { get; init; }
    public string? ReferenceName { get; init; }
    public bool IsDisabled { get; init; }
    public IReadOnlyList<AdoState> States { get; set; } = [];
}

internal sealed record AdoState
{
    public required string Name { get; init; }
    public string? Category { get; init; }
}

internal sealed record AdoBacklogConfiguration
{
    public IReadOnlyList<AdoBacklog> PortfolioBacklogs { get; set; } = [];
    public AdoBacklog? RequirementBacklog { get; init; }
    public AdoBacklog? TaskBacklog { get; init; }
}

internal sealed record AdoBacklog
{
    public string? Name { get; init; }
    public int Rank { get; init; }
    public IReadOnlyList<AdoNamed> WorkItemTypes { get; set; } = [];
}

internal sealed record AdoNamed
{
    public required string Name { get; init; }
}

internal sealed record AdoField
{
    public required string Name { get; init; }
    public required string ReferenceName { get; init; }
    public string? Type { get; init; }
    public bool ReadOnly { get; init; }
    public bool IsPicklist { get; init; }
    public bool IsIdentity { get; init; }
}

internal sealed record AdoClassificationNode
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public AdoNodeAttributes? Attributes { get; init; }
    public IReadOnlyList<AdoClassificationNode> Children { get; set; } = [];
}

internal sealed record AdoNodeAttributes
{
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? FinishDate { get; init; }
}

internal sealed record AdoWiqlRequest(string Query);

internal sealed record AdoWiqlResult
{
    public IReadOnlyList<AdoWorkItemReference> WorkItems { get; set; } = [];
}

internal sealed record AdoWorkItemReference
{
    public int Id { get; init; }
}

internal sealed record AdoBatchRequest
{
    public required IReadOnlyList<int> Ids { get; init; }

    [JsonPropertyName("$expand")]
    public string Expand { get; set; } = "Relations";

    /// <summary>Omit: an item deleted between the query and the fetch comes back as null instead of failing the batch.</summary>
    public string ErrorPolicy { get; set; } = "Omit";
}

internal sealed record AdoWorkItem
{
    public int Id { get; init; }
    public int Rev { get; init; }
    public Dictionary<string, JsonElement> Fields { get; set; } = [];
    public IReadOnlyList<AdoRelation> Relations { get; set; } = [];
}

internal sealed record AdoRelation
{
    public required string Rel { get; init; }
    public required string Url { get; init; }
    public Dictionary<string, JsonElement> Attributes { get; set; } = [];
}

internal sealed record AdoUpdate
{
    public int Id { get; init; }
    public int Rev { get; init; }
    public JsonElement RevisedBy { get; init; }
    public DateTimeOffset? RevisedDate { get; init; }
    public Dictionary<string, AdoFieldUpdate> Fields { get; set; } = [];
    public AdoRelationUpdates? Relations { get; init; }
}

internal sealed record AdoFieldUpdate
{
    public JsonElement OldValue { get; init; }
    public JsonElement NewValue { get; init; }
}

internal sealed record AdoRelationUpdates
{
    public IReadOnlyList<AdoRelation> Added { get; set; } = [];
    public IReadOnlyList<AdoRelation> Removed { get; set; } = [];
}

internal sealed record AdoCommentList
{
    public IReadOnlyList<AdoComment> Comments { get; set; } = [];
    public string? ContinuationToken { get; init; }
}

internal sealed record AdoComment
{
    public int Id { get; init; }
    public string? Text { get; init; }

    /// <summary>"html" (the default) or "markdown" (Services, since 2024).</summary>
    public string? Format { get; init; }

    public JsonElement CreatedBy { get; init; }
    public DateTimeOffset CreatedDate { get; init; }
    public DateTimeOffset? ModifiedDate { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AdoList<AdoProject>))]
[JsonSerializable(typeof(AdoProject))]
[JsonSerializable(typeof(AdoProcess))]
[JsonSerializable(typeof(AdoList<AdoWorkItemType>))]
[JsonSerializable(typeof(AdoBacklogConfiguration))]
[JsonSerializable(typeof(AdoList<AdoField>))]
[JsonSerializable(typeof(AdoClassificationNode))]
[JsonSerializable(typeof(AdoWiqlRequest))]
[JsonSerializable(typeof(AdoWiqlResult))]
[JsonSerializable(typeof(AdoBatchRequest))]
[JsonSerializable(typeof(AdoList<AdoWorkItem?>))]
[JsonSerializable(typeof(AdoList<AdoUpdate>))]
[JsonSerializable(typeof(AdoCommentList))]
internal sealed partial class AzureDevOpsJson : JsonSerializerContext;
