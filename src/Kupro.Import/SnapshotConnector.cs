using System.Runtime.CompilerServices;
using Kupro.Import.Snapshot;

namespace Kupro.Import;

/// <summary>
/// The "snapshot upload" source: a snapshot from the CLI's <c>--local</c> mode, an agent or a customer script, read
/// as if it were a live source. A snapshot is a fixed picture, so there is no delta: every run reads all of it, and
/// re-runs match by source ID as usual.
/// </summary>
public sealed class SnapshotConnector(SnapshotReader snapshot) : ISourceConnector
{
    public Task<SourceDescription> DescribeAsync(CancellationToken ct)
    {
        var header = snapshot.Header;
        return Task.FromResult(new SourceDescription
        {
            Source = header.Source,
            Projects = header.Projects,
            People = header.People,
            ItemTypes = header.ItemTypes,
            Statuses = header.Statuses,
            Fields = header.Fields,
        });
    }

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        var header = snapshot.Header;
        var projects = scope.Projects.Count == 0 ? null : new HashSet<string>(scope.Projects, StringComparer.Ordinal);
        foreach (var sprint in header.Sprints.Where(s => s.Project is null || projects is null || projects.Contains(s.Project)))
        {
            yield return sprint;
        }

        foreach (var version in header.Versions.Where(v => projects is null || projects.Contains(v.Project)))
        {
            yield return version;
        }

        // Items first, remembering which are in scope, so their history can be filtered without a second pass.
        var inScope = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in snapshot.ReadItemsAsync(ct))
        {
            if ((projects is null || projects.Contains(item.Project))
                && (scope.ResolvedAfter is null || item.Resolved is null || item.Resolved >= scope.ResolvedAfter))
            {
                inScope.Add(item.Id);
                yield return item;
            }
        }

        await foreach (var relation in snapshot.ReadRelationsAsync(ct))
        {
            if (inScope.Contains(relation.From) && inScope.Contains(relation.To))
            {
                yield return relation;
            }
        }

        await foreach (var comment in snapshot.ReadCommentsAsync(ct))
        {
            if (inScope.Contains(comment.Item))
            {
                yield return comment;
            }
        }

        await foreach (var attachment in snapshot.ReadAttachmentsAsync(ct))
        {
            if (inScope.Contains(attachment.Item))
            {
                yield return attachment;
            }
        }

        await foreach (var change in snapshot.ReadChangesAsync(ct))
        {
            if (inScope.Contains(change.Item))
            {
                yield return change;
            }
        }

        yield return new ReadCursor(header.Cursor);
    }

    public async Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        attachment.File is { } path && await snapshot.OpenFileAsync(path, ct) is { } stream
            ? stream
            : throw new SnapshotFormatException($"Attachment {attachment.Id} ({attachment.FileName}) has no file in the snapshot.");
}
