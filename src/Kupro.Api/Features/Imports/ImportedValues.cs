using System.Text.Json;
using Kupro.Api.Features.Issues;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// The Kupro values an item last imported as, stored on <see cref="ImportedItem.LastValues"/>. A re-run updates a field
/// only while Kupro still holds this value. <see cref="FoldedTop"/> is the name of the folded top-level item whose
/// label the issue carries: an imported item keeps its shape, whatever the fold is set to now.
/// </summary>
public sealed record ImportedValues(
    string Title,
    string? Description,
    IssueState State,
    IssuePriority Priority,
    Guid? Assignee,
    Guid? Parent,
    Guid? Sprint,
    decimal? Effort,
    DateOnly? Start,
    DateOnly? Due,
    IReadOnlyList<string> Labels,
    string? FoldedTop = null);

/// <summary>What a re-run does with one field (ADR-019 Re-runs and conflicts).</summary>
public enum ImportFieldDecision
{
    /// <summary>The source didn't change it.</summary>
    Unchanged,

    /// <summary>Both sides reached the same value; only the memory of the last import moves.</summary>
    Agreed,

    /// <summary>The source changed it and Kupro didn't.</summary>
    Apply,

    /// <summary>Both changed it, differently. Kupro wins until the owner says otherwise.</summary>
    Conflict,
}

/// <summary>One field of <see cref="ImportedValues"/>, named as in the activity log.</summary>
public sealed class ImportedField
{
    public const string Title = "title";
    public const string Description = "description";
    public const string State = "state";
    public const string Priority = "priority";
    public const string Assignee = "assignee";
    public const string Parent = "parent";
    public const string Sprint = "sprint";
    public const string Effort = "effort";
    public const string Start = "startDate";
    public const string Due = "dueDate";
    public const string Labels = "labels";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyList<ImportedField> All =
    [
        Of(Title, v => v.Title, (v, x) => v with { Title = x }),
        Of(Description, v => v.Description, (v, x) => v with { Description = x }),
        Of(State, v => v.State, (v, x) => v with { State = x }),
        Of(Priority, v => v.Priority, (v, x) => v with { Priority = x }),
        Of(Assignee, v => v.Assignee, (v, x) => v with { Assignee = x }),
        Of(Parent, v => v.Parent, (v, x) => v with { Parent = x }),
        Of(Sprint, v => v.Sprint, (v, x) => v with { Sprint = x }),
        Of(Effort, v => v.Effort, (v, x) => v with { Effort = x }),
        Of(Start, v => v.Start, (v, x) => v with { Start = x }),
        Of(Due, v => v.Due, (v, x) => v with { Due = x }),
        Of(Labels, v => v.Labels, (v, x) => v with { Labels = x }),
    ];

    private readonly Func<ImportedValues, object?> _get;
    private readonly Func<ImportedValues, ImportedValues, ImportedValues> _copy;
    private readonly Func<ImportedValues, string, ImportedValues> _parse;

    private ImportedField(
        string name, Func<ImportedValues, object?> get, Func<ImportedValues, ImportedValues, ImportedValues> copy, Func<ImportedValues, string, ImportedValues> parse) =>
        (Name, _get, _copy, _parse) = (name, get, copy, parse);

    public string Name { get; }

    private static ImportedField Of<T>(string name, Func<ImportedValues, T> get, Func<ImportedValues, T, ImportedValues> set) =>
        new(name, v => get(v), (to, from) => set(to, get(from)), (to, json) => set(to, JsonSerializer.Deserialize<T>(json, Json)!));

    public static ImportedField Named(string name) => All.Single(f => f.Name == name);

    public static string Serialize(ImportedValues values) => JsonSerializer.Serialize(values, Json);

    public static ImportedValues Deserialize(string json) => JsonSerializer.Deserialize<ImportedValues>(json, Json)!;

    /// <summary>The field from <paramref name="from"/>, everything else from <paramref name="to"/>.</summary>
    public ImportedValues Copy(ImportedValues to, ImportedValues from) => _copy(to, from);

    public string ToJson(ImportedValues values) => JsonSerializer.Serialize(_get(values), Json);

    public ImportedValues WithJson(ImportedValues to, string json) => _parse(to, json);

    public ImportFieldDecision Decide(ImportedValues next, ImportedValues last, ImportedValues current)
    {
        var (source, remembered, kupro) = (_get(next), _get(last), _get(current));
        return Same(source, remembered) ? ImportFieldDecision.Unchanged
            : Same(source, kupro) ? ImportFieldDecision.Agreed
            : Same(kupro, remembered) ? ImportFieldDecision.Apply
            : ImportFieldDecision.Conflict;
    }

    private static bool Same(object? a, object? b) =>
        a is IReadOnlyList<string> x && b is IReadOnlyList<string> y ? x.SequenceEqual(y, StringComparer.Ordinal) : Equals(a, b);
}
