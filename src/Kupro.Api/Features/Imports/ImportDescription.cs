using System.Globalization;
using System.Text;
using System.Text.Json;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>A folded checklist item, as a line in its parent's description. <see cref="Position"/> keeps the lines in order across runs.</summary>
public sealed record ChecklistLine(string Title, bool Done, int Position = 0);

/// <summary>
/// What an imported issue's text and labels are made of: the converted description, folded checklist items, and a
/// collapsed "Imported from" block for everything Kupro has no field for, so nothing is dropped (ADR-019 Other fields).
/// </summary>
public sealed class ImportDescription
{
    /// <summary>The issue column's limit.</summary>
    public const int MaxLength = 100_000;

    public const int MaxTitle = 500;
    private const int MaxLabel = 50;
    private const int MaxValue = 1000;
    private const int MaxBlock = 50_000;

    private readonly string _sourceName;
    private readonly ImportMapping _mapping;
    private readonly Dictionary<string, SourceField> _fields;
    private readonly Dictionary<string, string> _statuses;
    private readonly Dictionary<string, string> _types;
    private readonly Dictionary<string, string> _people;
    private readonly Dictionary<string, string> _versions;
    private readonly Dictionary<string, string> _sprints;

    public ImportDescription(SnapshotHeader header, ImportMapping mapping)
    {
        _sourceName = SourceName(header.Source.Kind);
        _mapping = mapping;
        _fields = header.Fields.DistinctBy(f => f.Id).ToDictionary(f => f.Id, StringComparer.Ordinal);
        _statuses = header.Statuses.DistinctBy(s => s.Id).ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        _types = header.ItemTypes.DistinctBy(t => t.Id).ToDictionary(t => t.Id, t => t.Name, StringComparer.Ordinal);
        _people = header.People.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        _versions = header.Versions.DistinctBy(v => v.Id).ToDictionary(v => v.Id, v => v.Name, StringComparer.Ordinal);
        _sprints = header.Sprints.DistinctBy(s => s.Id).ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
    }

    public string SourceDisplayName => _sourceName;

    public static string SourceName(string kind) => kind switch
    {
        SourceKinds.Jira => "Jira",
        SourceKinds.AzureDevOps => "Azure DevOps",
        SourceKinds.Linear => "Linear",
        SourceKinds.Trello => "Trello",
        SourceKinds.GitHub => "GitHub",
        SourceKinds.Monday => "Monday",
        SourceKinds.YouTrack => "YouTrack",
        SourceKinds.Csv => "CSV",
        SourceKinds.Kupro => "Kupro",
        SourceKinds.Snapshot => "a snapshot",
        _ => kind,
    };

    /// <summary>
    /// The item's labels: its own, its type's (Bug is a Task labelled <c>bug</c>), the fields the owner ticked and, under a
    /// folded top-level item, that item's name. A label over Kupro's length goes to <paramref name="tooLong"/> rather than being cut.
    /// </summary>
    public IReadOnlyList<string> Labels(SourceItem item, TypeTarget type, string? foldedTop, List<string> tooLong)
    {
        var names = new List<string>(item.Labels);
        if (type.Label is { } typeLabel)
        {
            names.Add(typeLabel);
        }

        foreach (var field in _mapping.LabelFields)
        {
            if (item.Fields.TryGetValue(field, out var value))
            {
                names.AddRange(Strings(value));
            }
        }

        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names.Select(LabelService.Normalize).Where(n => n.Length > 0))
        {
            if (name.Length > MaxLabel)
            {
                tooLong.Add(name);
            }
            else
            {
                labels.Add(name);
            }
        }

        // A grouping label, so a shortened name still groups.
        if (foldedTop is not null && LabelService.Normalize(foldedTop) is { Length: > 0 } top)
        {
            labels.Add(top.Length > MaxLabel ? top[..MaxLabel].TrimEnd() : top);
        }

        return [.. labels.Order(StringComparer.Ordinal)];
    }

    /// <summary>The rows of the block: what the source said that Kupro has no field for.</summary>
    public List<(string Field, string Value)> Details(SourceItem item, IEnumerable<string> tooLongLabels, bool assigneeSkipped)
    {
        var rows = new List<(string, string)>();
        void Add(string field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add((field, value));
            }
        }

        Add("Key", item.Key);
        Add("Type", _types.GetValueOrDefault(item.Type, item.Type));
        Add("Status", _statuses.GetValueOrDefault(item.Status, item.Status));
        Add("Priority", item.Priority);
        Add("Points", item.Points?.ToString(CultureInfo.InvariantCulture));
        if (item.Title.Length > MaxTitle)
        {
            Add("Title", item.Title);
        }

        if (assigneeSkipped && item.Assignee is { } assignee)
        {
            Add("Assignee", _people.GetValueOrDefault(assignee, assignee));
        }

        if (item.Sprint is { } sprint && !_sprints.ContainsKey(sprint))
        {
            Add("Sprint", sprint);
        }

        Add("Versions", string.Join(", ", item.Versions.Select(v => _versions.GetValueOrDefault(v, v))));
        Add("Labels", string.Join(", ", tooLongLabels));
        foreach (var (id, value) in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!_mapping.LabelFields.Contains(id))
            {
                var field = _fields.GetValueOrDefault(id);
                Add(field?.Name ?? id, Display(value, field));
            }
        }

        return rows;
    }

    /// <summary>The whole description. Too long: the source text is cut, and the block still gets through with its link to the original.</summary>
    public (string? Text, bool Truncated) Build(
        SourceItem item, IReadOnlyList<ChecklistLine> checklist, IReadOnlyList<(string Field, string Value)> details, ImportReferences references)
    {
        var body = references.Rewrite(MarkupConverters.ToMarkdown(item.Description, item.DescriptionMarkup), item.Id);
        if (checklist.Count > 0)
        {
            var lines = string.Join("\n", checklist.Select(c =>
                $"- [{(c.Done ? 'x' : ' ')}] {MarkupConverters.EscapeText(Whitespace(c.Title))}"));
            body = body is null ? lines : $"{body}\n\n{lines}";
        }

        var block = Block(item, details);
        var room = MaxLength - block.Length - 2;
        var truncated = body is not null && body.Length > room;
        if (truncated)
        {
            const string Note = "\n\n*Cut at import: the original is too long for Kupro.*";
            body = string.Concat(body.AsSpan(0, Math.Max(0, room - Note.Length)), Note);
        }

        var text = body is null ? block : block.Length == 0 ? body : $"{body}\n\n{block}";
        return (text.Length == 0 ? null : text, truncated);
    }

    private string Block(SourceItem item, IReadOnlyList<(string Field, string Value)> details)
    {
        if (details.Count == 0 && item.Url is null && item.Links.Count == 0)
        {
            return "";
        }

        var block = new StringBuilder();
        block.Append("<details>\n<summary>Imported from ").Append(Whitespace(_sourceName).Replace("<", "", StringComparison.Ordinal)).Append("</summary>\n\n");
        if (details.Count > 0)
        {
            block.Append("| Field | Value |\n| --- | --- |\n");
            foreach (var (field, value) in details)
            {
                if (block.Length > MaxBlock)
                {
                    block.Append("| … | … |\n");
                    break;
                }

                block.Append("| ").Append(Cell(field)).Append(" | ").Append(Cell(value)).Append(" |\n");
            }

            block.Append('\n');
        }

        if (item.Url is { } url)
        {
            block.Append("[Open the original](").Append(Destination(url)).Append(")\n\n");
        }

        foreach (var link in item.Links.Take(100))
        {
            var title = MarkupConverters.EscapeText(Whitespace(link.Title ?? link.Url));
            block.Append("- [").Append(title).Append("](").Append(Destination(link.Url)).Append(")\n");
        }

        block.Append(item.Links.Count > 0 ? "\n" : "").Append("</details>");
        return block.ToString();
    }

    private string? Display(JsonElement value, SourceField? field) => value.ValueKind switch
    {
        JsonValueKind.String when field?.Kind == FieldKind.User && value.GetString() is { } id => _people.GetValueOrDefault(id, id),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => string.Join(", ", Strings(value)),
    };

    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        JsonValueKind.String => [value.GetString() ?? ""],
        JsonValueKind.Null or JsonValueKind.Undefined => [],
        _ => [value.ToString()],
    };

    private static string Cell(string value)
    {
        var line = Whitespace(value);
        var cut = line.Length > MaxValue ? line[..MaxValue] + "…" : line;
        return MarkupConverters.EscapeText(cut).Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string Whitespace(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Destination(string url)
    {
        var clean = url.Trim().Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
        return clean.Any(c => char.IsWhiteSpace(c) || c is '(' or ')')
            ? $"<{clean.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)}>"
            : clean;
    }
}
