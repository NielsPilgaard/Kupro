using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Csv;

/// <summary>
/// A CSV file as a source (task 023): each row an item, the columns as <see cref="CsvColumns"/> says, every other column
/// a text field for the details block. Values are taken as written: types, statuses and priorities are mapped like any
/// source's, and people are matched by name or email.
/// </summary>
public sealed class CsvConnector(CsvTable table, CsvColumns columns, string name, TimeProvider? time = null) : ISourceConnector
{
    private const string OneProject = "csv";
    private const string NoType = "Task";
    private const string NoStatus = "No status";

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "dd/MMM/yy h:mm tt", "dd/MMM/yy", "d/MMM/yy h:mm tt", "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "d.M.yyyy",
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<SourceDescription> DescribeAsync(CancellationToken ct) => Task.FromResult(new SourceDescription
    {
        Source = new SnapshotSource { Kind = SourceKinds.Csv },
        Projects = Projects(),
        People = People(),
        ItemTypes = [.. Distinct(columns.Type, NoType).Select(t => new SourceItemType { Id = t, Name = t })],
        Statuses = [.. Distinct(columns.Status, NoStatus).Select(s => new SourceStatus { Id = s, Name = s })],
        Fields = [.. Others().Select(h => new SourceField { Id = h, Name = h, Kind = FieldKind.Text })],
    });

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var person in People())
        {
            yield return person;
        }

        var ids = table.Rows.Select(IdOf).ToList();
        var byReference = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < table.Rows.Count; n++)
        {
            foreach (var reference in new[] { table.Value(table.Rows[n], columns.Key), table.Value(table.Rows[n], columns.Id) }.OfType<string>())
            {
                byReference.TryAdd(reference, ids[n]);
            }
        }

        var others = Others().ToList();
        var now = _time.GetUtcNow();
        for (var n = 0; n < table.Rows.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var row = table.Rows[n];
            var project = table.Value(row, columns.Project) ?? OneProject;
            if (scope.Projects.Count > 0 && !scope.Projects.Contains(project))
            {
                continue;
            }

            var created = Time(table.Value(row, columns.Created)) ?? now;
            var resolved = Time(table.Value(row, columns.Resolved));
            if (resolved < scope.ResolvedAfter)
            {
                continue;
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var header in others)
            {
                if (table.Value(row, header) is { } value)
                {
                    fields[header] = FieldValue.Text(value);
                }
            }

            var hours = Number(table.Value(row, columns.EstimateHours));
            var seconds = Number(table.Value(row, columns.EstimateSeconds));
            yield return new SourceItem
            {
                Id = ids[n],
                Key = table.Value(row, columns.Key) ?? (table.Value(row, columns.Id) is { } id && id.All(char.IsAsciiDigit) ? id : null),
                Project = project,
                Type = table.Value(row, columns.Type) ?? NoType,
                Status = table.Value(row, columns.Status) ?? NoStatus,
                Title = table.Value(row, columns.Title) ?? "",
                Description = table.Value(row, columns.Description),
                DescriptionMarkup = columns.DescriptionMarkup,
                Parent = table.Value(row, columns.Parent) is { } parent && byReference.TryGetValue(parent, out var parentId) && parentId != ids[n] ? parentId : null,
                Assignee = Person(table.Value(row, columns.Assignee)),
                Creator = Person(table.Value(row, columns.Creator)),
                Priority = table.Value(row, columns.Priority),
                Labels = Labels(row),
                Fields = fields,
                EstimateSeconds = seconds is { } s ? (long)s : hours is { } h ? (long)Math.Round(h * 3600) : null,
                Points = Number(table.Value(row, columns.Points)),
                Start = Day(table.Value(row, columns.Start)),
                Due = Day(table.Value(row, columns.Due)),
                Created = created,
                Updated = Time(table.Value(row, columns.Updated)) ?? created,
                Resolved = resolved,
            };
        }

        yield return new ReadCursor(null);
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        throw new NotSupportedException("A CSV file has no attachments.");

    /// <summary>The row's ID column, else its key, else its row number: a file without either can't be re-run.</summary>
    private string IdOf(string[] row, int index) =>
        table.Value(row, columns.Id) ?? table.Value(row, columns.Key) ?? $"row-{index + 1}";

    private List<SourceProject> Projects() =>
        columns.Project is null
            ? [new SourceProject { Id = OneProject, Name = name }]
            : [.. Distinct(columns.Project, OneProject).Select(p => new SourceProject { Id = p, Key = p, Name = p })];

    private List<SourcePerson> People() =>
        [.. table.Rows.SelectMany(r => new[] { table.Value(r, columns.Assignee), table.Value(r, columns.Creator) })
            .OfType<string>()
            .DistinctBy(v => Person(v), StringComparer.Ordinal)
            .Select(v => new SourcePerson { Id = Person(v)!, Name = v, Email = v.Contains('@', StringComparison.Ordinal) ? v : null })];

    /// <summary>A person is whoever the cell names; the same name or address in any case is one person.</summary>
    private static string? Person(string? value) => value?.Trim().ToLowerInvariant();

    private IEnumerable<string> Distinct(string? header, string fallback) =>
        table.Rows.Select(r => table.Value(r, header) ?? fallback).Distinct(StringComparer.Ordinal);

    private IEnumerable<string> Others()
    {
        var used = columns.Used().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return table.Headers.Where(h => h.Length > 0 && !used.Contains(h)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private List<string> Labels(string[] row)
    {
        var values = table.Values(row, columns.Labels).ToList();
        var repeated = table.Headers.Count(h => string.Equals(h, columns.Labels, StringComparison.OrdinalIgnoreCase)) > 1;
        return [.. (repeated ? values : values.SelectMany(v => v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A number as either half of Europe writes it: <c>1.5</c> or <c>1,5</c>.</summary>
    private static decimal? Number(string? text) =>
        text is null ? null
        : decimal.TryParse(text.Contains('.', StringComparison.Ordinal) ? text : text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value
        : null;

    private static DateTimeOffset? Time(string? text) =>
        text is null ? null
        : DateTimeOffset.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var exact) ? exact
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loose) ? loose
        : null;

    private static DateOnly? Day(string? text) => Time(text) is { } at ? DateOnly.FromDateTime(at.DateTime) : null;
}
