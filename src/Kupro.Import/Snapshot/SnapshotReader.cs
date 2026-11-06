using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kupro.Import.Snapshot;

/// <summary>
/// Reads a snapshot ZIP part by part, one record at a time, so a 200k-item snapshot never sits in memory. Snapshots
/// come from customers and scripts, so the reader checks the format number, caps every entry's size and reports the
/// part and line of a bad record.
/// </summary>
public sealed class SnapshotReader : IAsyncDisposable
{
    /// <summary>The writer chunks at a megabyte; anything near this is not a snapshot the writer made.</summary>
    internal const long MaxEntryBytes = 64L << 20;

    private readonly ZipArchive _zip;

    private SnapshotReader(ZipArchive zip, SnapshotHeader header)
    {
        _zip = zip;
        Header = header;
    }

    public SnapshotHeader Header { get; }

    /// <summary>Opens a snapshot. The stream must be seekable (a ZIP is read from its end) and stays open.</summary>
    public static async Task<SnapshotReader> OpenAsync(Stream input, CancellationToken ct = default)
    {
        ZipArchive zip;
        try
        {
            zip = await ZipArchive.CreateAsync(input, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, ct);
        }
        catch (InvalidDataException e)
        {
            throw new SnapshotFormatException("The file is not a ZIP archive.", e);
        }

        try
        {
            var entry = zip.GetEntry(SnapshotWriter.HeaderEntry)
                ?? throw new SnapshotFormatException($"{SnapshotWriter.HeaderEntry} is missing.");
            SnapshotHeader? header;
            await using (var stream = await OpenBoundedAsync(entry, ct))
            {
                // Read the format number alone first, so a newer snapshot gets a clear message, not a field error.
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number)
                {
                    throw new SnapshotFormatException($"{SnapshotWriter.HeaderEntry} has no format number.");
                }

                if (format.GetInt32() != SnapshotWriter.Format)
                {
                    throw new SnapshotFormatException(
                        $"Snapshot format {format.GetInt32()} is not supported; this version reads format {SnapshotWriter.Format}.");
                }

                header = document.RootElement.Deserialize(SnapshotJson.Default.SnapshotHeader);
            }

            return new SnapshotReader(zip, header ?? throw new SnapshotFormatException($"{SnapshotWriter.HeaderEntry} is empty."));
        }
        catch (JsonException e)
        {
            await zip.DisposeAsync();
            throw new SnapshotFormatException($"{SnapshotWriter.HeaderEntry}: {e.Message}", e);
        }
        catch
        {
            await zip.DisposeAsync();
            throw;
        }
    }

    public IAsyncEnumerable<SourceItem> ReadItemsAsync(CancellationToken ct = default) =>
        ReadPartAsync(SnapshotParts.Items, SnapshotJson.Default.SourceItem, ct);

    public IAsyncEnumerable<SourceRelation> ReadRelationsAsync(CancellationToken ct = default) =>
        ReadPartAsync(SnapshotParts.Relations, SnapshotJson.Default.SourceRelation, ct);

    public IAsyncEnumerable<SourceComment> ReadCommentsAsync(CancellationToken ct = default) =>
        ReadPartAsync(SnapshotParts.Comments, SnapshotJson.Default.SourceComment, ct);

    public IAsyncEnumerable<SourceAttachment> ReadAttachmentsAsync(CancellationToken ct = default) =>
        ReadPartAsync(SnapshotParts.Attachments, SnapshotJson.Default.SourceAttachment, ct);

    public IAsyncEnumerable<SourceChange> ReadChangesAsync(CancellationToken ct = default) =>
        ReadPartAsync(SnapshotParts.Changes, SnapshotJson.Default.SourceChange, ct);

    /// <summary>Opens a file stored in the snapshot (<see cref="SourceAttachment.File"/>); null if it isn't there.</summary>
    public async Task<Stream?> OpenFileAsync(string path, CancellationToken ct = default)
    {
        // Only files/ entries: a record must not point the import at snapshot.json or an NDJSON part.
        if (!path.StartsWith(SnapshotParts.Files + "/", StringComparison.Ordinal) || _zip.GetEntry(path) is not { } entry)
        {
            return null;
        }

        return await entry.OpenAsync(ct);
    }

    public ValueTask DisposeAsync() => _zip.DisposeAsync();

    private async IAsyncEnumerable<T> ReadPartAsync<T>(string part, JsonTypeInfo<T> type, [EnumeratorCancellation] CancellationToken ct)
        where T : class
    {
        var prefix = part + "/";
        var chunks = _zip.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.FullName.EndsWith(".ndjson", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            await using var stream = await OpenBoundedAsync(chunk, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = 0;
            while (await reader.ReadLineAsync(ct) is { } text)
            {
                line++;
                if (text.Length == 0)
                {
                    continue;
                }

                T? record;
                try
                {
                    record = JsonSerializer.Deserialize(text, type);
                }
                catch (JsonException e)
                {
                    throw new SnapshotFormatException($"{chunk.FullName} line {line}: {e.Message}", e);
                }

                yield return record ?? throw new SnapshotFormatException($"{chunk.FullName} line {line}: null record.");
            }
        }
    }

    private static async Task<Stream> OpenBoundedAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new SnapshotFormatException($"{entry.FullName} is larger than {MaxEntryBytes >> 20} MB.");
        }

        // The declared size can lie (a ZIP bomb), so the stream enforces it too.
        return new BoundedStream(await entry.OpenAsync(ct), MaxEntryBytes, entry.FullName);
    }

    private sealed class BoundedStream(Stream inner, long limit, string name) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new SnapshotFormatException($"{name} is larger than {limit >> 20} MB.") : read;
        }
    }
}

/// <summary>The snapshot is not one this version can read. The message is for the owner, as is.</summary>
public sealed class SnapshotFormatException : Exception
{
    public SnapshotFormatException() { }

    public SnapshotFormatException(string message) : base(message) { }

    public SnapshotFormatException(string message, Exception inner) : base(message, inner) { }
}
