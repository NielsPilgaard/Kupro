using System.Globalization;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>What a change can point at in the workspace, by source ID. Null: the import has nothing for it.</summary>
internal sealed record ChangeTargets(Func<string, Guid?> Member, Func<string, Guid?> Issue, Func<string, Guid?> Sprint);

/// <summary>
/// Source changes as feed entries (ADR-019 Changelog replay). A field Kupro has, with both sides translatable, is
/// <c>updated</c>; anything else (a field Kupro lacks, a skipped person, a parent outside the import) is
/// <c>source_changed</c> as the source showed it, so no history is lost and none is invented. A change that maps to no
/// change at all is dropped. Pure: the workspace side comes in as <see cref="ChangeTargets"/>.
/// </summary>
internal sealed class ChangeTranslator(SnapshotHeader header, MappedSource source)
{
    public const string Updated = "updated";
    public const string SourceChanged = "source_changed";

    private readonly Dictionary<string, string> _fieldNames = header.Fields.DistinctBy(f => f.Id).ToDictionary(f => f.Id, f => f.Name, StringComparer.Ordinal);

    /// <summary>The feed entry's kind and data; a null kind means nothing changed in Kupro's terms.</summary>
    public (string? Kind, Dictionary<string, object?> Data) Translate(SourceChange change, ChangeTargets targets)
    {
        Dictionary<string, object?> Data(string field, object? from, object? to) => new() { [field] = new FieldChange(from, to) };

        (string?, Dictionary<string, object?>) Raw() => (SourceChanged, new Dictionary<string, object?>
        {
            ["field"] = _fieldNames.GetValueOrDefault(change.Field) ?? change.Field,
            ["from"] = change.FromText ?? change.From,
            ["to"] = change.ToText ?? change.To,
        });

        (string?, Dictionary<string, object?>) Mapped<T>(string field, Func<string, T?> map)
        {
            var from = change.From is { } f ? map(f) : default;
            var to = change.To is { } t ? map(t) : default;
            if ((change.From is not null && from is null) || (change.To is not null && to is null))
            {
                return Raw();
            }

            return Equals(from, to) ? (null, []) : (Updated, Data(field, from, to));
        }

        switch (change.Field)
        {
            case ChangeFields.Status:
                // The state a status maps to. An item's own state also weighs its resolution and sprint, which a change doesn't carry.
                return Mapped<IssueState?>("state", s => source.State(s));
            case ChangeFields.Type:
                return Mapped<IssueType?>("type", t => source.TypeOf(t));
            case ChangeFields.Priority:
                return Mapped<IssuePriority?>("priority", p => source.Priority(p));
            case ChangeFields.Title:
                return Mapped("title", t => t.Trim() is { Length: > 0 } title ? (title.Length > ImportDescription.MaxTitle ? title[..ImportDescription.MaxTitle].TrimEnd() : title) : null);
            case ChangeFields.Description:
                return (Updated, Data("description", null, null));
            case ChangeFields.Assignee:
                return Mapped<Guid?>("assignee", targets.Member);
            case ChangeFields.Parent:
                return Mapped<Guid?>("parent", targets.Issue);
            case ChangeFields.Sprint:
                return Mapped<Guid?>("sprint", targets.Sprint);
            case ChangeFields.Labels:
                var (fromLabels, toLabels) = (LabelList(change.FromText ?? change.From), LabelList(change.ToText ?? change.To));
                return fromLabels.SequenceEqual(toLabels) ? (null, []) : (Updated, Data("labels", fromLabels, toLabels));
            case ChangeFields.EstimateSeconds:
                return Mapped<decimal?>("effort", s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 ? MappedSource.EffortOfSeconds(seconds) : null);
            case ChangeFields.Points:
                return Mapped<decimal?>("effort", p => decimal.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out var points) ? source.EffortOfPoints(points) : null);
            case ChangeFields.Start:
                return Mapped<DateOnly?>("startDate", Day);
            case ChangeFields.Due:
                return Mapped<DateOnly?>("dueDate", Day);
            default:
                return Raw();
        }
    }

    private static DateOnly? Day(string text) =>
        DateOnly.TryParse(text.Length >= 10 ? text[..10] : text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>Labels as the source listed them: one name, or several split on <c>;</c> or <c>,</c> (Azure DevOps tags).</summary>
    private static List<string> LabelList(string? text) =>
        text is null ? [] : [.. text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).Select(LabelService.Normalize).Where(n => n.Length > 0).Order(StringComparer.Ordinal)];
}

/// <summary>The <see cref="SourceChange.Field"/> names for item properties (docs/import-format.md, Changes).</summary>
internal static class ChangeFields
{
    public const string Status = "status";
    public const string Type = "type";
    public const string Title = "title";
    public const string Description = "description";
    public const string Priority = "priority";
    public const string Assignee = "assignee";
    public const string Parent = "parent";
    public const string Sprint = "sprint";
    public const string Labels = "labels";
    public const string EstimateSeconds = "estimateSeconds";
    public const string Points = "points";
    public const string Start = "start";
    public const string Due = "due";
}
