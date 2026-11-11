using System.Text;

namespace Kupro.Import.Csv;

/// <summary>
/// A CSV file read whole: the delimiter and encoding it uses, its header row and its rows. CSV exports are bounded by
/// what a tracker lets you download, so the file is read into memory. Handles quoted fields with delimiters, quotes and
/// line breaks (RFC 4180), UTF-8 with or without a BOM, and Windows-1252 from older Excel and Jira exports.
/// </summary>
public sealed class CsvTable
{
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];
    private const int SampleLines = 20;

    private CsvTable(char delimiter, string encoding, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows) =>
        (Delimiter, Encoding, Headers, Rows) = (delimiter, encoding, headers, rows);

    public char Delimiter { get; }

    /// <summary><c>utf-8</c> or <c>windows-1252</c>.</summary>
    public string Encoding { get; }

    /// <summary>Header names as written, trimmed. A name can repeat: Jira writes one <c>Labels</c> column per label.</summary>
    public IReadOnlyList<string> Headers { get; }

    /// <summary>Data rows, each as long as <see cref="Headers"/>.</summary>
    public IReadOnlyList<string[]> Rows { get; }

    public static async Task<CsvTable> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return Parse(buffer.ToArray());
    }

    public static CsvTable Parse(byte[] bytes)
    {
        var (text, encoding) = Decode(bytes);
        var delimiter = DetectDelimiter(text);
        var records = Split(text, delimiter).Where(r => r.Length > 1 || r[0].Length > 0).ToList();
        if (records.Count == 0)
        {
            throw new FormatException("The file is empty. It needs a header row naming its columns.");
        }

        var headers = records[0].Select(h => h.Trim()).ToList();
        var rows = records.Skip(1).Select(r => r.Length == headers.Count ? r : [.. r, .. Enumerable.Repeat("", Math.Max(0, headers.Count - r.Length))])
            .Select(r => r.Length > headers.Count ? r[..headers.Count] : r)
            .ToList();
        return new CsvTable(delimiter, encoding, headers, rows);
    }

    /// <summary>The value in the first column of that name that has one, trimmed; null if none does.</summary>
    public string? Value(string[] row, string? header)
    {
        if (header is null)
        {
            return null;
        }

        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i], header, StringComparison.OrdinalIgnoreCase) && row[i].Trim() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Every non-empty value in columns of that name.</summary>
    public IEnumerable<string> Values(string[] row, string? header)
    {
        for (var i = 0; header is not null && i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i], header, StringComparison.OrdinalIgnoreCase) && row[i].Trim() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    /// <summary>UTF-8 when the bytes are valid UTF-8 (BOM or not); anything else is taken as Windows-1252, which keeps æøå.</summary>
    private static (string Text, string Encoding) Decode(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes), "windows-1252");
        }
    }

    /// <summary>The delimiter that splits the first lines into the same number of fields, the most of them.</summary>
    private static char DetectDelimiter(string text)
    {
        var sample = string.Join('\n', text.Split('\n').Take(SampleLines));
        return Delimiters
            .Select(d => (Delimiter: d, Counts: Split(sample, d).Take(SampleLines - 1).Select(r => r.Length).ToList()))
            .Where(c => c.Counts.Count > 0 && c.Counts[0] > 1)
            .OrderByDescending(c => c.Counts.Count(n => n == c.Counts[0]))
            .ThenByDescending(c => c.Counts[0])
            .Select(c => c.Delimiter)
            .DefaultIfEmpty(',')
            .First();
    }

    private static IEnumerable<string[]> Split(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                yield return [.. fields];
                fields.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return [.. fields];
        }
    }
}
