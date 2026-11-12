using System.Buffers.Binary;
using System.Text.Json;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Kupro;

/// <summary>
/// The tables of an export's <c>workspace.json</c>, streamed once and spooled to disk, one file per table the import
/// reads. The connector needs them in another order than the file has them, and memory holds about one row at a time,
/// so a workspace of any size moves. Tables it doesn't read (meetings, transcripts) are skipped row by row.
/// </summary>
internal sealed class ExportSpool : IDisposable
{
    /// <summary>One row may need the read buffer to grow this far. Kupro's own limits keep rows far smaller.</summary>
    internal const int MaxRowBytes = 64 << 20;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kupro-export-{Guid.NewGuid():N}");
    private readonly HashSet<string> _wanted;
    private readonly Dictionary<string, FileStream> _writers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private byte[] _row = new byte[4096];

    private ExportSpool(IEnumerable<string> tables) => _wanted = new HashSet<string>(tables, StringComparer.Ordinal);

    /// <summary>The <c>format</c> number at the top of the file, if it has one.</summary>
    public int? Format { get; private set; }

    /// <summary>
    /// Streams <paramref name="json"/> into per-table files. More than <paramref name="maxBytes"/> fails: a ZIP bomb
    /// must not fill the disk.
    /// </summary>
    public static async Task<ExportSpool> SplitAsync(Stream json, IEnumerable<string> tables, long maxBytes, CancellationToken ct)
    {
        var spool = new ExportSpool(tables);
        try
        {
            Directory.CreateDirectory(spool._directory);
            var splitter = new Splitter(spool._wanted);
            var state = new JsonReaderState();
            var buffer = new byte[1 << 16];
            var (filled, total, final) = (0, 0L, false);
            var rows = new List<(string Table, int Start, int Length)>();
            while (true)
            {
                if (filled == buffer.Length)
                {
                    if (buffer.Length >= MaxRowBytes)
                    {
                        throw new SnapshotFormatException($"A row in workspace.json is larger than {MaxRowBytes >> 20} MB.");
                    }

                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                // A full buffer per round: a row cut at the end is parsed again, so small reads mustn't each cost a parse.
                while (filled < buffer.Length)
                {
                    var read = await json.ReadAsync(buffer.AsMemory(filled), ct);
                    if (read == 0)
                    {
                        final = true;
                        break;
                    }

                    filled += read;
                    total += read;
                    if (total > maxBytes)
                    {
                        throw TooLarge(maxBytes);
                    }
                }

                rows.Clear();
                var consumed = splitter.Process(buffer.AsSpan(0, filled), final, ref state, rows);
                foreach (var (table, start, length) in rows)
                {
                    await spool.WriteAsync(table, buffer.AsMemory(start, length), ct);
                }

                Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                filled -= consumed;
                if (splitter.Done)
                {
                    break;
                }

                if (final)
                {
                    throw new SnapshotFormatException("workspace.json ends before its last table.");
                }
            }

            spool.Format = splitter.Format;
            foreach (var writer in spool._writers.Values)
            {
                await writer.DisposeAsync();
            }

            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    public static SnapshotFormatException TooLarge(long maxBytes) =>
        new($"This export's workspace.json is larger than {maxBytes >> 20} MB, the most this Kupro reads (Imports:MaxExportJsonMegabytes).");

    /// <summary>A table's rows in file order, read lazily from disk.</summary>
    public IEnumerable<JsonElement> Rows(string table) => RowsWithOffsets(table).Select(r => r.Row);

    /// <summary>Each row with where it starts, for <see cref="RowsAt"/>.</summary>
    public IEnumerable<(long Offset, JsonElement Row)> RowsWithOffsets(string table)
    {
        if (Open(table) is not { } file)
        {
            yield break;
        }

        using (file)
        {
            while (file.Position < file.Length)
            {
                var offset = file.Position;
                yield return (offset, ReadRow(file));
            }
        }
    }

    /// <summary>The rows that start at <paramref name="offsets"/>, in that order.</summary>
    public IEnumerable<JsonElement> RowsAt(string table, IEnumerable<long> offsets)
    {
        if (Open(table) is not { } file)
        {
            yield break;
        }

        using (file)
        {
            foreach (var offset in offsets)
            {
                file.Position = offset;
                yield return ReadRow(file);
            }
        }
    }

    public void Dispose()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task WriteAsync(string table, ReadOnlyMemory<byte> row, CancellationToken ct)
    {
        if (!_writers.TryGetValue(table, out var writer))
        {
            var path = Path.Combine(_directory, $"{_writers.Count}.rows");
            writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            _writers[table] = writer;
            _paths[table] = path;
        }

        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, row.Length);
        await writer.WriteAsync(length, ct);
        await writer.WriteAsync(row, ct);
    }

    private FileStream? Open(string table) =>
        _paths.TryGetValue(table, out var path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16) : null;

    private JsonElement ReadRow(FileStream file)
    {
        Span<byte> prefix = stackalloc byte[4];
        file.ReadExactly(prefix);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (_row.Length < length)
        {
            _row = new byte[length];
        }

        file.ReadExactly(_row, 0, length);
        return Parse(_row.AsSpan(0, length));
    }

    /// <summary>The element owns a copy, so the buffer can be reused.</summary>
    private static JsonElement Parse(ReadOnlySpan<byte> row)
    {
        var reader = new Utf8JsonReader(row);
        return JsonElement.ParseValue(ref reader);
    }

    /// <summary>
    /// Walks the root object a buffer at a time: each array element at depth 2 is one row. A value that doesn't fit the
    /// buffer yet is left for the next round, from the reader state before it.
    /// </summary>
    private sealed class Splitter(HashSet<string> wanted)
    {
        private string? _name;
        private string? _table;

        public bool Done { get; private set; }

        public int? Format { get; private set; }

        public int Process(ReadOnlySpan<byte> data, bool final, ref JsonReaderState state, List<(string Table, int Start, int Length)> rows)
        {
            var reader = new Utf8JsonReader(data, final, state);
            while (!Done)
            {
                var before = reader.CurrentState;
                var at = (int)reader.BytesConsumed;
                if (!reader.Read())
                {
                    break;
                }

                switch (reader.CurrentDepth, reader.TokenType)
                {
                    case (0, JsonTokenType.EndObject):
                        Done = true;
                        break;
                    case (0, _):
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            throw new SnapshotFormatException("workspace.json is not a JSON object.");
                        }

                        break;
                    case (1, JsonTokenType.PropertyName):
                        _name = reader.GetString();
                        break;
                    case (1, JsonTokenType.StartArray):
                        _table = _name is not null && wanted.Contains(_name) ? _name : null;
                        break;
                    case (1, JsonTokenType.EndArray):
                        _table = null;
                        break;
                    case (1, JsonTokenType.StartObject) or (2, JsonTokenType.StartObject) or (2, JsonTokenType.StartArray):
                        var start = (int)reader.TokenStartIndex;
                        var row = reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.StartObject;
                        if (!reader.TrySkip())
                        {
                            state = before;
                            return at;
                        }

                        if (row && _table is not null)
                        {
                            rows.Add((_table, start, (int)reader.BytesConsumed - start));
                        }

                        break;
                    case (1, JsonTokenType.Number) when _name == "format":
                        Format = reader.TryGetInt32(out var format) ? format : -1;
                        break;
                }
            }

            state = reader.CurrentState;
            return (int)reader.BytesConsumed;
        }
    }
}
