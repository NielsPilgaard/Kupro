using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kupro.Import.Snapshot;

/// <summary>
/// Writes a snapshot ZIP in one pass while a connector streams records. A ZIP takes one open entry at a time, so each
/// part is buffered up to about a megabyte and written as a numbered chunk (<c>items/000001.ndjson</c>); memory stays
/// flat however large the source. Reference data (people, sprints, versions) is collected and written last, with the
/// counts, as <c>snapshot.json</c>.
/// </summary>
public sealed class SnapshotWriter : IAsyncDisposable
{
    public const int Format = 1;
    public const string HeaderEntry = "snapshot.json";
    internal const int ChunkBytes = 1 << 20;

    private readonly ZipArchive _zip;
    private readonly SourceDescription _description;
    private readonly Dictionary<string, SourcePerson> _people = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceSprint> _sprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceVersion> _versions = new(StringComparer.Ordinal);
    private readonly Part _items = new(SnapshotParts.Items);
    private readonly Part _relations = new(SnapshotParts.Relations);
    private readonly Part _comments = new(SnapshotParts.Comments);
    private readonly Part _attachments = new(SnapshotParts.Attachments);
    private readonly Part _changes = new(SnapshotParts.Changes);
    private string? _cursor;
    private int _files;
    private bool _completed;

    public SnapshotWriter(Stream output, SourceDescription description)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        _description = description;
        foreach (var person in description.People)
        {
            AddPerson(person);
        }
    }

    /// <summary>Set when the source was read from a cursor: see <see cref="SnapshotHeader.Partial"/>.</summary>
    public bool Partial { get; init; }

    public async Task WriteAsync(ISnapshotRecord record, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        switch (record)
        {
            case SourcePerson person:
                AddPerson(person);
                break;
            case SourceSprint sprint:
                _sprints[sprint.Id] = sprint;
                break;
            case SourceVersion version:
                _versions[version.Id] = version;
                break;
            case ReadCursor cursor:
                _cursor = cursor.Cursor;
                break;
            case SourceItem item:
                await AppendAsync(_items, item, SnapshotJson.Default.SourceItem, ct);
                break;
            case SourceRelation relation:
                await AppendAsync(_relations, relation, SnapshotJson.Default.SourceRelation, ct);
                break;
            case SourceComment comment:
                await AppendAsync(_comments, comment, SnapshotJson.Default.SourceComment, ct);
                break;
            case SourceAttachment attachment:
                await AppendAsync(_attachments, attachment, SnapshotJson.Default.SourceAttachment, ct);
                break;
            case SourceChange change:
                await AppendAsync(_changes, change, SnapshotJson.Default.SourceChange, ct);
                break;
            default:
                throw new ArgumentException($"Unknown snapshot record {record.GetType().Name}.", nameof(record));
        }
    }

    /// <summary>Stores the file in the snapshot (CLI <c>--local</c>: the source token never reaches Kupro).</summary>
    public async Task WriteAttachmentAsync(SourceAttachment attachment, Stream content, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var path = $"{SnapshotParts.Files}/{++_files:D6}";
        await using (var entry = await _zip.CreateEntry(path, CompressionLevel.Fastest).OpenAsync(ct))
        {
            await content.CopyToAsync(entry, ct);
        }

        await WriteAsync(attachment with { File = path }, ct);
    }

    /// <summary>Flushes the parts and writes <c>snapshot.json</c>. The ZIP is valid once this and dispose have run.</summary>
    public async Task CompleteAsync(DateTimeOffset createdAt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        foreach (var part in new[] { _items, _relations, _comments, _attachments, _changes })
        {
            await FlushAsync(part, ct);
        }

        var header = new SnapshotHeader
        {
            Format = Format,
            Source = _description.Source,
            CreatedAt = createdAt,
            Cursor = _cursor,
            Partial = Partial,
            Projects = _description.Projects,
            People = [.. _people.Values],
            ItemTypes = _description.ItemTypes,
            Statuses = _description.Statuses,
            Fields = _description.Fields,
            Sprints = [.. _sprints.Values],
            Versions = [.. _versions.Values],
            Counts = new SnapshotCounts
            {
                Items = _items.Count,
                Relations = _relations.Count,
                Comments = _comments.Count,
                Attachments = _attachments.Count,
                Changes = _changes.Count,
            },
        };
        await using (var entry = await _zip.CreateEntry(HeaderEntry, CompressionLevel.Optimal).OpenAsync(ct))
        {
            await JsonSerializer.SerializeAsync(entry, header, SnapshotJson.Default.SnapshotHeader, ct);
        }

        _completed = true;
    }

    public ValueTask DisposeAsync()
    {
        _zip.Dispose();
        return ValueTask.CompletedTask;
    }

    private void AddPerson(SourcePerson person)
    {
        // Sources often show an email on one record and hide it on another; keep it once seen.
        _people[person.Id] = _people.TryGetValue(person.Id, out var known)
            ? known with { Email = known.Email ?? person.Email, Handle = known.Handle ?? person.Handle }
            : person;
    }

    private async Task AppendAsync<T>(Part part, T record, JsonTypeInfo<T> type, CancellationToken ct)
    {
        JsonSerializer.Serialize(part.Buffer, record, type);
        part.Buffer.WriteByte((byte)'\n');
        part.Count++;
        if (part.Buffer.Length >= ChunkBytes)
        {
            await FlushAsync(part, ct);
        }
    }

    private async Task FlushAsync(Part part, CancellationToken ct)
    {
        if (part.Buffer.Length == 0)
        {
            return;
        }

        var name = $"{part.Name}/{++part.Chunks:D6}.ndjson";
        await using (var entry = await _zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(ct))
        {
            part.Buffer.Position = 0;
            await part.Buffer.CopyToAsync(entry, ct);
        }

        part.Buffer.SetLength(0);
    }

    private sealed class Part(string name)
    {
        public string Name { get; } = name;
        public MemoryStream Buffer { get; } = new();
        public int Count { get; set; }
        public int Chunks { get; set; }
    }
}

/// <summary>Folder names inside the snapshot ZIP.</summary>
public static class SnapshotParts
{
    public const string Items = "items";
    public const string Relations = "relations";
    public const string Comments = "comments";
    public const string Attachments = "attachments";
    public const string Changes = "changes";
    public const string Files = "files";
}
