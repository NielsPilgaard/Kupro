using System.Globalization;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// A source item as the Kupro values a first write stores and a re-run compares against (ADR-019 Shape): title,
/// description with its checklist and details block, state, priority, effort, dates and labels. Pure: the people,
/// parent and sprint it points at come in already resolved.
/// </summary>
internal sealed class ItemValues(MappedSource source, ImportDescription description)
{
    private const int MaxEffort = 1_000_000;

    /// <summary>The item's labels; names too long for a label are added to <paramref name="tooLong"/> for the details block.</summary>
    public IReadOnlyList<string> Labels(SourceItem item, string? foldedTop, List<string> tooLong) =>
        description.Labels(item, source.Type(item), foldedTop, tooLong);

    /// <returns>The values, and whether the description was cut to fit.</returns>
    public (ImportedValues Values, bool Truncated) Of(
        SourceItem item, IReadOnlyList<string> labels, IReadOnlyList<string> tooLong, Guid? assignee, Guid? parent, Guid? sprint, string? foldedTop,
        IReadOnlyList<ChecklistLine> checklist, ImportReferences references)
    {
        var effort = source.Effort(item);
        var effortFits = effort is >= 0 and < MaxEffort;
        var start = item.Start > item.Due ? null : item.Start;
        var details = description.Details(item, tooLong, item.Assignee is not null && assignee is null);
        if (effort is not null && !effortFits)
        {
            details.Add(("Effort", effort.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (item.Start is not null && start is null)
        {
            details.Add(("Start date", item.Start.Value.ToString("O", CultureInfo.InvariantCulture)));
        }

        var (text, truncated) = description.Build(item, checklist, details, references);
        var title = string.IsNullOrWhiteSpace(item.Title) ? "(no title)" : ImportDescription.Cut(item.Title.Trim(), ImportDescription.MaxTitle);
        var effortValue = effortFits ? Math.Round(effort!.Value, 2) : (decimal?)null;
        return (new ImportedValues(title, text, source.State(item), source.Priority(item), assignee, parent, sprint, effortValue, start, item.Due, labels, foldedTop), truncated);
    }
}
