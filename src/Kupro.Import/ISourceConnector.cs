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
    /// <returns>How many files couldn't be fetched; they are in the snapshot without their bytes, so the import counts
    /// them as missing.</returns>
    public static async Task<int> WriteSnapshotAsync(
        this ISourceConnector connector, Stream output, ReadScope scope, string? cursor, bool includeFiles,
        TimeProvider time, CancellationToken ct) =>
        await connector.WriteSnapshotAsync(output, _ => scope, cursor, includeFiles, time, ct);

    /// <summary>As above, with the scope picked from what Describe found (the CLI matches project keys to IDs).</summary>
    public static async Task<int> WriteSnapshotAsync(
        this ISourceConnector connector, Stream output, Func<SourceDescription, ReadScope> scopeFor, string? cursor,
        bool includeFiles, TimeProvider time, CancellationToken ct)
    {
        var description = await connector.DescribeAsync(ct);
        var scope = scopeFor(description);

        // Statuses of boards not picked never occur in the read, so the snapshot leaves them out.
        description = description with { Statuses = [.. description.Statuses.Where(s => s.Board is null || s.Board == scope.Board)] };
        description = Picked(description, scope);
        await using var writer = new SnapshotWriter(output, description) { Partial = cursor is not null };
        var missing = 0;
        await foreach (var record in connector.ReadAsync(scope, cursor, ct))
        {
            if (includeFiles && record is SourceAttachment attachment && attachment.File is null)
            {
                // A file deleted since the read, or one the token may not download, is one file short, not a failed read.
                Stream content;
                try
                {
                    content = await connector.FetchAttachmentAsync(attachment, ct);
                }
                catch (Exception e) when (e is SourceAccessException or HttpRequestException && !ct.IsCancellationRequested)
                {
                    missing++;
                    await writer.WriteAsync(record, ct);
                    continue;
                }

                await using (content)
                {
                    await writer.WriteAttachmentAsync(attachment, content, ct);
                }
            }
            else
            {
                await writer.WriteAsync(record, ct);
            }
        }

        await writer.CompleteAsync(time.GetUtcNow(), ct);
        return missing;
    }

    /// <summary>
    /// The description as the picked columns read it: a status column not picked gives no statuses, and a column
    /// that fills a role on every board is no field.
    /// </summary>
    internal static SourceDescription Picked(SourceDescription description, ReadScope scope)
    {
        if (description.Columns.Count == 0)
        {
            return description;
        }

        var picked = description.Columns.GroupBy(c => c.Project)
            .SelectMany(g => ColumnRoles.Picked(g, g.Key, scope.Columns).Select(r => (Project: g.Key, Role: r.Key, Column: r.Value)))
            .ToList();
        var roles = picked.Select(p => (p.Project, p.Column)).ToHashSet();
        // Only boards the read returns say what is done: the picked projects, or with none picked the active ones. A
        // sub-item board is no project, and which board's items it holds shows only in the read, so its columns stay.
        var read = scope.Projects.Count > 0
            ? scope.Projects.ToHashSet(StringComparer.Ordinal)
            : description.Projects.Where(p => !p.Archived).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var projects = description.Projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var statusColumns = picked.Where(p => p.Role == ColumnRoles.Status && (read.Contains(p.Project) || !projects.Contains(p.Project)))
            .Select(p => (p.Project, p.Column)).ToHashSet();
        var labels = description.Columns.Where(c => statusColumns.Contains((c.Project, c.Id))).SelectMany(c => c.Labels).ToHashSet(StringComparer.Ordinal);
        var done = description.Columns.Where(c => statusColumns.Contains((c.Project, c.Id))).SelectMany(c => c.Done).ToHashSet(StringComparer.Ordinal);
        var listed = description.Columns.SelectMany(c => c.Labels).ToHashSet(StringComparer.Ordinal);
        var fields = description.Columns.Where(c => c.Field is not null && !roles.Contains((c.Project, c.Id))).Select(c => c.Field!).ToHashSet(StringComparer.Ordinal);
        var columnFields = description.Columns.Select(c => c.Field).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return description with
        {
            // A label is done when a picked status column calls it done, not one left out.
            Statuses = [.. description.Statuses.Where(s => !listed.Contains(s.Id) || labels.Contains(s.Id))
                .Select(s => listed.Contains(s.Id) ? s with { Category = done.Contains(s.Id) ? "done" : null } : s)],
            Fields = [.. description.Fields.Where(f => !columnFields.Contains(f.Id) || fields.Contains(f.Id))],
            Columns = [],
        };
    }
}
