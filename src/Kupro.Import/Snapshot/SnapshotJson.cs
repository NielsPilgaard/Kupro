using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Import.Snapshot;

/// <summary>
/// Source-generated JSON for the snapshot format (the CLI is AOT). Reads reject a missing required property or a
/// null where the model has none, so a broken customer script fails on the line, not deep in the import.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SnapshotHeader))]
[JsonSerializable(typeof(SourceDescription))]
[JsonSerializable(typeof(SourceItem))]
[JsonSerializable(typeof(SourceRelation))]
[JsonSerializable(typeof(SourceComment))]
[JsonSerializable(typeof(SourceAttachment))]
[JsonSerializable(typeof(SourceChange))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(string[]))]
public sealed partial class SnapshotJson : JsonSerializerContext;

/// <summary>Builds <see cref="SourceItem.Fields"/> values without reflection.</summary>
public static class FieldValue
{
    public static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value, SnapshotJson.Default.String);

    public static JsonElement Number(decimal value) => JsonSerializer.SerializeToElement(value, SnapshotJson.Default.Decimal);

    public static JsonElement Date(DateOnly value) => Text(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    public static JsonElement Options(params string[] values) =>
        JsonSerializer.SerializeToElement(values, SnapshotJson.Default.StringArray);
}
