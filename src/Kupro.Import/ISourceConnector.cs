using Kupro.Import.Snapshot;

namespace Kupro.Import;

/// <summary>
/// Reads one source (ADR-019 IMP-003). Runs in the API, or in the CLI with <c>--local</c> for servers behind a VPN, so
/// implementations stay AOT-safe and keep the token to themselves. Tested against recorded fixtures only.
/// </summary>
public interface ISourceConnector
{
    /// <summary>Projects, people, types, statuses and fields, for the wizard's scope and mapping steps.</summary>
    Task<SourceDescription> DescribeAsync(CancellationToken ct);

    /// <summary>
    /// Streams the items in scope with their history and whatever reference data they use. With a cursor, only what
    /// changed since then (the caller already subtracted the overlap). Ends with a <see cref="ReadCursor"/>.
    /// </summary>
    IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, CancellationToken ct);

    /// <summary>The bytes of an attachment from <see cref="ReadAsync"/>.</summary>
    Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct);
}

public static class SourceConnectorExtensions
{
    /// <summary>
    /// Reads a connector into a snapshot ZIP: the CLI's <c>--local</c> mode, and how tests turn a fake source into an
    /// upload. With <paramref name="includeFiles"/> attachment bytes go into the ZIP, so the import never calls back.
    /// </summary>
    public static async Task WriteSnapshotAsync(
        this ISourceConnector connector, Stream output, ReadScope scope, string? cursor, bool includeFiles,
        TimeProvider time, CancellationToken ct)
    {
        var description = await connector.DescribeAsync(ct);
        await using var writer = new SnapshotWriter(output, description);
        await foreach (var record in connector.ReadAsync(scope, cursor, ct))
        {
            if (includeFiles && record is SourceAttachment attachment && attachment.File is null)
            {
                await using var content = await connector.FetchAttachmentAsync(attachment, ct);
                await writer.WriteAttachmentAsync(attachment, content, ct);
            }
            else
            {
                await writer.WriteAsync(record, ct);
            }
        }

        await writer.CompleteAsync(time.GetUtcNow(), ct);
    }
}
