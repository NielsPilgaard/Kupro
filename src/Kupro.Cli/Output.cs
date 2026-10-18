using Microsoft.Kiota.Abstractions.Serialization;

namespace Kupro.Cli;

/// <summary>Table for people at a terminal; JSON when piped or with <c>--json</c> (ADR-001 IMP-002).</summary>
public sealed class Output(TextWriter stdout, bool json)
{
    public bool Json { get; } = json;
    public TextWriter Writer { get; } = stdout;

    /// <summary>One object: JSON, or label/value lines.</summary>
    public async Task ItemAsync<T>(T item, params (string Label, string? Value)[] fields) where T : IParsable
    {
        if (Json)
        {
            await Writer.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(item));
            return;
        }

        var width = fields.Max(f => f.Label.Length);
        foreach (var (label, value) in fields.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            await Writer.WriteLineAsync($"{label.PadRight(width)}  {value}");
        }
    }

    /// <summary>A list: JSON array, or an aligned table.</summary>
    public async Task ListAsync<T>(IReadOnlyCollection<T> items, string[] headers, Func<T, string?[]> row) where T : IParsable
    {
        if (Json)
        {
            await Writer.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(items));
            return;
        }

        if (items.Count == 0)
        {
            await Writer.WriteLineAsync("Nothing found.");
            return;
        }

        var rows = items.Select(i => row(i).Select(v => v ?? "").ToArray()).ToList();
        var widths = headers.Select((h, c) => Math.Max(h.Length, rows.Max(r => r[c].Length))).ToArray();
        // The last column (usually a title) is not padded so long text doesn't push trailing spaces.
        string Line(string[] cells) => string.Join("  ", cells.Select((v, c) => c == cells.Length - 1 ? v : v.PadRight(widths[c])));
        await Writer.WriteLineAsync(Line(headers));
        foreach (var r in rows)
        {
            await Writer.WriteLineAsync(Line(r));
        }
    }

    /// <summary>A short confirmation for people; JSON callers get the object instead.</summary>
    public async Task DoneAsync<T>(T item, string message) where T : IParsable
    {
        if (Json)
        {
            await Writer.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(item));
        }
        else
        {
            await Writer.WriteLineAsync(message);
        }
    }

    public Task MessageAsync(string message) => Json ? Task.CompletedTask : Writer.WriteLineAsync(message);
}
