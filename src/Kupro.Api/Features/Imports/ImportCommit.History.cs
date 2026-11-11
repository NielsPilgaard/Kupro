using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Realtime;
using Kupro.Import.Snapshot;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// The history half of a commit (ADR-019 History): attachments copied into storage, comments, and the changelog
/// replayed into the issue feed, each with the source's people and times. Every pass is resumable like the items.
/// </summary>
public sealed partial class ImportCommit
{
    private const int AttachmentBatch = 50;
    private const int ChangeBatch = 2000;
    private const int MaxFileName = 200;
    private const string EmptyComment = "*(empty comment)*";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // ---------------------------------------------------------------- references in text

    /// <summary>
    /// What imported text can point at, known before any issue is written: numbers are reserved, and an attachment's ID
    /// is derived from its source ID, so an inline image can name its copy before the copy exists.
    /// </summary>
    private async Task<ImportReferences> ReferencesAsync(SnapshotReader snapshot, SourceRead read, FitResult fit, Dictionary<string, int> numbers, CancellationToken ct)
    {
        var files = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);
        var missing = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var attachment in snapshot.ReadAttachmentsAsync(ct))
        {
            if (!seen.Add(attachment.Id) || HolderOf(attachment.Item, fit) is null)
            {
                continue;
            }

            if (attachment.File is null)
            {
                missing++;
            }
            else if (attachment.Url is { Length: > 0 } url)
            {
                files.TryAdd(url.Trim(), new AttachmentRef(StableId(ImportedKinds.Attachment, attachment.Id), FileName(attachment.FileName), IsImage(ContentType(attachment))));
            }
        }

        // Absolute, not added: a resumed run counts the same attachments again.
        _counts.AttachmentsMissing = missing;

        var issues = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> known;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            known = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item)
                .Join(db.Issues, i => i.EntityId, x => x.Id, (i, x) => new { i.SourceId, x.Key })
                .ToDictionaryAsync(r => r.SourceId, r => r.Key, StringComparer.Ordinal, ct);
            _candidates ??= await CandidatesAsync(db, ct);
        }

        foreach (var item in read.Refs.Where(r => r.Url is { Length: > 0 }))
        {
            var key = known.GetValueOrDefault(item.Id) ?? (numbers.TryGetValue(item.Id, out var number) ? $"{item.ProjectKey}-{number}" : null);
            if (key is not null)
            {
                issues.TryAdd(item.Url!.Trim(), key);
            }
        }

        // A mention reads as the Kupro name of whoever the person became, else the source's name for them.
        var members = _candidates.ToDictionary(c => c.Id, c => c.Name);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var handles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in _header.People.DistinctBy(p => p.Id))
        {
            var name = _mapping.People.TryGetValue(person.Id, out var target) && target is { Action: PersonAction.Member, MemberId: { } id }
                && members.TryGetValue(id, out var member) ? member : person.Name;
            names[person.Id] = name;
            if (person.Handle is { Length: > 0 } handle)
            {
                handles.TryAdd(handle.TrimStart('@'), name);
            }
        }

        return new ImportReferences(names, handles, files, issues);
    }

    /// <summary>The source item whose issue a record belongs to: the item itself, or for a folded checklist item, its holder.</summary>
    private string? HolderOf(string sourceItem, FitResult? fit = null) =>
        _holders.TryGetValue(sourceItem, out var holder) ? holder
        : _items.ContainsKey(sourceItem) || fit?.Items.ContainsKey(sourceItem) == true ? sourceItem
        : null;

    private Guid? IssueOf(string sourceItem) => HolderOf(sourceItem) is { } holder && _items.TryGetValue(holder, out var id) ? id : null;

    /// <summary>The same ID for the same source record in every run of this import, so a retried upload overwrites itself.</summary>
    private Guid StableId(string kind, string sourceId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{_importId:N}/{kind}/{sourceId}"), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8: application-defined
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    // ---------------------------------------------------------------- attachments

    private async Task WriteAttachmentsAsync(SnapshotReader snapshot, CancellationToken ct)
    {
        var batch = new List<SourceAttachment>(AttachmentBatch);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var attachment in snapshot.ReadAttachmentsAsync(ct))
        {
            if (!seen.Add(attachment.Id) || attachment.File is null || IssueOf(attachment.Item) is null)
            {
                continue;
            }

            batch.Add(attachment);
            if (batch.Count == AttachmentBatch)
            {
                await WriteAttachmentBatchAsync(snapshot, batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteAttachmentBatchAsync(snapshot, batch, ct);
        }
    }

    private async Task WriteAttachmentBatchAsync(SnapshotReader snapshot, List<SourceAttachment> batch, CancellationToken ct)
    {
        var done = await DoneAsync(ImportedKinds.Attachment, batch.Select(a => a.Id), ct);
        var stored = new List<(SourceAttachment Source, Guid Id, string ContentType, long Size)>();
        foreach (var attachment in batch.Where(a => !done.Contains(a.Id)))
        {
            // Uploaded before the rows are written. A crash in between leaves an object the next run overwrites.
            var id = StableId(ImportedKinds.Attachment, attachment.Id);
            var contentType = ContentType(attachment);
            var path = Path.GetTempFileName();
            try
            {
                long size;
                await using (var content = await snapshot.OpenFileAsync(attachment.File!, ct))
                {
                    if (content is null)
                    {
                        continue;
                    }

                    await using var file = File.Create(path);
                    await content.CopyToAsync(file, ct);
                    size = file.Length;
                }

                await attachments.StoreImportedAsync(id, path, contentType, ct);
                stored.Add((attachment, id, contentType, size));
            }
            finally
            {
                File.Delete(path);
            }
        }

        if (stored.Count == 0)
        {
            return;
        }

        var newPeople = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            newPeople.Clear();
            delta = new ImportRunCounts();
            var actor = await ActorsAsync(db, stored.Select(s => s.Source.Author), newPeople, delta, ct);
            foreach (var (source, id, contentType, size) in stored)
            {
                attachments.AddImported(db, id, IssueOf(source.Item)!.Value, actor(source.Author).Id, FileName(source.FileName), contentType, size, source.Created.ToUniversalTime());
                Record(db, ImportedKinds.Attachment, source.Id, ImportedEntityKind.Attachment, id);
                delta.Attachments++;
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        Merge(newPeople);
        _counts.Add(delta);
        await realtime.ChangedAsync(ChangeTopic.Issue);
    }

    private static string FileName(string name)
    {
        var file = Path.GetFileName(name.Trim().Replace('\\', '/'));
        file = file.Length == 0 ? "attachment" : file;
        return file.Length > MaxFileName ? file[^MaxFileName..] : file;
    }

    /// <summary>What the source said, if it is a plain MIME type; else what the file name suggests.</summary>
    private static string ContentType(SourceAttachment attachment) =>
        attachment.ContentType?.Trim().ToLowerInvariant() is { Length: <= 100 } type && MimeType().IsMatch(type) ? type
        : ContentTypes.TryGetContentType(attachment.FileName, out var guessed) ? guessed
        : "application/octet-stream";

    private static bool IsImage(string contentType) => contentType.StartsWith("image/", StringComparison.Ordinal);

    // ---------------------------------------------------------------- comments

    private async Task WriteCommentsAsync(SnapshotReader snapshot, ImportReferences references, CancellationToken ct)
    {
        var batch = new List<SourceComment>(BatchSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var comment in snapshot.ReadCommentsAsync(ct))
        {
            if (!seen.Add(comment.Id) || IssueOf(comment.Item) is null)
            {
                continue;
            }

            batch.Add(comment);
            if (batch.Count == BatchSize)
            {
                await WriteCommentBatchAsync(batch, references, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteCommentBatchAsync(batch, references, ct);
        }
    }

    private async Task WriteCommentBatchAsync(List<SourceComment> batch, ImportReferences references, CancellationToken ct)
    {
        var done = await DoneAsync(ImportedKinds.Comment, batch.Select(c => c.Id), ct);
        var todo = batch.Where(c => !done.Contains(c.Id)).ToList();
        if (todo.Count == 0)
        {
            return;
        }

        var newPeople = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            newPeople.Clear();
            delta = new ImportRunCounts();
            var actor = await ActorsAsync(db, todo.Select(c => c.Author), newPeople, delta, ct);
            foreach (var source in todo)
            {
                var (author, sourceAuthor) = actor(source.Author);
                var id = Guid.CreateVersion7();
                comments.AddImported(db, id, IssueOf(source.Item)!.Value, author, CommentBody(source, references),
                    source.Created.ToUniversalTime(), source.Updated?.ToUniversalTime(), sourceAuthor);
                Record(db, ImportedKinds.Comment, source.Id, ImportedEntityKind.Comment, id);
                delta.Comments++;
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        Merge(newPeople);
        _counts.Add(delta);
        await realtime.ChangedAsync(ChangeTopic.Issue);
    }

    private static string CommentBody(SourceComment comment, ImportReferences references)
    {
        var body = references.Rewrite(MarkupConverters.ToMarkdown(comment.Body, comment.Markup)) ?? EmptyComment;
        const string Note = "\n\n*Cut at import: the original is too long for Kupro.*";
        return body.Length <= CommentRequest.MaxBody ? body : string.Concat(body.AsSpan(0, CommentRequest.MaxBody - Note.Length), Note);
    }

    // ---------------------------------------------------------------- changelog

    /// <summary>
    /// Source changes become feed entries (ADR-019 Changelog replay): <c>updated</c> for fields Kupro has, translated
    /// through the mapping, and <c>source_changed</c> with the source's own names for the rest. A change this import
    /// replayed before is skipped by its item's mark; a resumed run skips the changes its batches already wrote.
    /// Returns the newest change per item, for <see cref="MarkChangesSeenAsync"/>.
    /// </summary>
    private async Task<Dictionary<string, string>> ReplayChangesAsync(SnapshotReader snapshot, Dictionary<string, string> projectOf, CancellationToken ct)
    {
        Dictionary<string, string> prior;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            prior = await db.ImportedItems.AsNoTracking()
                .Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item && i.SeenChangeMark != null)
                .ToDictionaryAsync(i => i.SourceId, i => i.SeenChangeMark!, StringComparer.Ordinal, ct);
        }

        var newest = new Dictionary<string, string>(StringComparer.Ordinal);
        var batch = new List<SourceChange>(ChangeBatch);
        var skip = _counts.ChangesRead;
        var read = 0;
        await foreach (var change in snapshot.ReadChangesAsync(ct))
        {
            read++;
            if (!_items.ContainsKey(change.Item))
            {
                // Folded checklist items have no issue of their own; out-of-scope items have none at all.
                continue;
            }

            var mark = Mark(change);
            if (prior.TryGetValue(change.Item, out var seen) && string.CompareOrdinal(mark, seen) <= 0)
            {
                continue;
            }

            if (!newest.TryGetValue(change.Item, out var top) || string.CompareOrdinal(mark, top) > 0)
            {
                newest[change.Item] = mark;
            }

            if (read <= skip)
            {
                continue;
            }

            batch.Add(change);
            if (batch.Count == ChangeBatch)
            {
                await WriteChangeBatchAsync(batch, read, projectOf, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteChangeBatchAsync(batch, read, projectOf, ct);
        }

        return newest;
    }

    /// <summary>Sorts by time, then ID: the newest change an item has had replayed. UTC round-trip text sorts by time.</summary>
    private static string Mark(SourceChange change) => $"{change.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}|{change.Id}";

    private async Task WriteChangeBatchAsync(List<SourceChange> batch, int readTo, Dictionary<string, string> projectOf, CancellationToken ct)
    {
        var newPeople = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var delta = new ImportRunCounts();
        await WriteAsync(async db =>
        {
            newPeople.Clear();
            delta = new ImportRunCounts { ChangesRead = readTo - _counts.ChangesRead };
            var assignees = batch.Where(c => c.Field == ChangeFields.Assignee).SelectMany(c => new[] { c.From, c.To });
            await ResolvePeopleAsync(db, assignees.OfType<string>().Distinct(), newPeople, delta, ct);
            var actor = await ActorsAsync(db, batch.Select(c => c.Actor), newPeople, delta, ct);
            foreach (var change in batch)
            {
                var (actorId, sourceActor) = actor(change.Actor);
                var (kind, data) = Translate(change, projectOf.GetValueOrDefault(change.Item), newPeople);
                if (kind is null)
                {
                    continue;
                }

                if (sourceActor is not null)
                {
                    data["sourceActor"] = sourceActor;
                }

                activity.RecordImported(db, _items[change.Item], kind, actorId, change.At.ToUniversalTime(), data);
                if (kind == SourceChangedKind)
                {
                    delta.SourceChanges++;
                }
                else
                {
                    delta.Changes++;
                }
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        Merge(newPeople);
        _counts.Add(delta);
        await realtime.ChangedAsync(ChangeTopic.Issue);
    }

    internal const string SourceChangedKind = "source_changed";

    /// <summary>
    /// One source change as a feed entry. A field Kupro has, with both sides translatable, is <c>updated</c>; anything
    /// else (a field Kupro lacks, a skipped person, a parent outside the import) is <c>source_changed</c> as the source
    /// showed it, so no history is lost and none is invented. A change that maps to no change at all is dropped.
    /// </summary>
    private (string? Kind, Dictionary<string, object?> Data) Translate(SourceChange change, string? projectKey, Dictionary<string, Guid?> newPeople)
    {
        Dictionary<string, object?> Updated(string field, object? from, object? to) => new() { [field] = new FieldChange(from, to) };

        (string?, Dictionary<string, object?>) Raw()
        {
            var name = _header.Fields.FirstOrDefault(f => f.Id == change.Field)?.Name ?? change.Field;
            return (SourceChangedKind, new Dictionary<string, object?>
            {
                ["field"] = name,
                ["from"] = change.FromText ?? change.From,
                ["to"] = change.ToText ?? change.To,
            });
        }

        (string?, Dictionary<string, object?>) Mapped<T>(string field, Func<string, T?> map, bool ifUnknownRaw = true)
        {
            var from = change.From is { } f ? map(f) : default;
            var to = change.To is { } t ? map(t) : default;
            if (ifUnknownRaw && ((change.From is not null && from is null) || (change.To is not null && to is null)))
            {
                return Raw();
            }

            return Equals(from, to) ? (null, []) : ("updated", Updated(field, from, to));
        }

        switch (change.Field)
        {
            case ChangeFields.Status:
                return Mapped<IssueState?>("state", s => _source.State(s));
            case ChangeFields.Type:
                return Mapped<IssueType?>("type", t => _source.TypeOf(t));
            case ChangeFields.Priority:
                return Mapped<IssuePriority?>("priority", p => _source.Priority(p));
            case ChangeFields.Title:
                return Mapped("title", t => t.Trim() is { Length: > 0 } title ? Cut(title, ImportDescription.MaxTitle) : null);
            case ChangeFields.Description:
                return ("updated", Updated("description", null, null));
            case ChangeFields.Assignee:
                return Mapped<Guid?>("assignee", p => Member(p, newPeople));
            case ChangeFields.Parent:
                return Mapped<Guid?>("parent", p => _items.TryGetValue(p, out var id) ? id : null);
            case ChangeFields.Sprint:
                return Mapped<Guid?>("sprint", s => projectKey is not null && _sprints.TryGetValue(SprintKey(s, projectKey), out var id) ? id : null);
            case ChangeFields.Labels:
                var (fromLabels, toLabels) = (LabelList(change.FromText ?? change.From), LabelList(change.ToText ?? change.To));
                return fromLabels.SequenceEqual(toLabels) ? (null, []) : ("updated", Updated("labels", fromLabels, toLabels));
            case ChangeFields.EstimateSeconds:
                return Mapped<decimal?>("effort", s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 ? MappedSource.EffortOfSeconds(seconds) : null);
            case ChangeFields.Points:
                return Mapped<decimal?>("effort", p => decimal.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out var points) ? _source.EffortOfPoints(points) : null);
            case ChangeFields.Start:
                return Mapped<DateOnly?>("startDate", Day);
            case ChangeFields.Due:
                return Mapped<DateOnly?>("dueDate", Day);
            default:
                return Raw();
        }
    }

    private static DateOnly? Day(string text) =>
        DateOnly.TryParse(text.Length >= 10 ? text[..10] : text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>Labels as the source listed them: one name, or several split on <c>;</c> or <c>,</c> (Azure DevOps tags).</summary>
    private static List<string> LabelList(string? text) =>
        text is null ? [] : [.. text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).Select(LabelService.Normalize).Where(n => n.Length > 0).Order(StringComparer.Ordinal)];

    /// <summary>Remembers the newest change replayed per item, after the replay, so a resumed replay isn't cut short by its own marks.</summary>
    private async Task MarkChangesSeenAsync(Dictionary<string, string> newest, CancellationToken ct)
    {
        foreach (var chunk in newest.Chunk(BatchSize))
        {
            var marks = chunk.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
            await WriteAsync(async db =>
            {
                var ids = marks.Keys.ToList();
                var rows = await db.ImportedItems.Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item && ids.Contains(i.SourceId)).ToListAsync(ct);
                var now = clock.GetUtcNow();
                foreach (var row in rows.Where(r => r.SeenChangeMark is null || string.CompareOrdinal(marks[r.SourceId], r.SeenChangeMark) > 0))
                {
                    row.SeenChangeMark = marks[row.SourceId];
                    row.UpdatedAt = now;
                }
            }, ct);
        }
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Source records of one kind this import already wrote, among <paramref name="sourceIds"/>.</summary>
    private async Task<HashSet<string>> DoneAsync(string kind, IEnumerable<string> sourceIds, CancellationToken ct)
    {
        var ids = sourceIds.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == kind && ids.Contains(i.SourceId))
            .Select(i => i.SourceId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Who acts for each source person: their member, whoever ran the import when the source names nobody, or the shared
    /// placeholder for someone skipped, with the source's name for them kept alongside (ADR-019 People).
    /// </summary>
    private async Task<Func<string?, (Guid Id, string? SourceName)>> ActorsAsync(
        KuproDbContext db, IEnumerable<string?> sourceIds, Dictionary<string, Guid?> added, ImportRunCounts delta, CancellationToken ct)
    {
        var ids = sourceIds.OfType<string>().Distinct().ToList();
        await ResolvePeopleAsync(db, ids, added, delta, ct);
        if (ids.Any(id => Member(id, added) is null))
        {
            EnsureSkippedPlaceholder(db, added, delta);
        }

        return id => Actor(id, added) is { } member ? (member, null) : (SkippedId(added), PersonName(id!));
    }

    private void Merge(Dictionary<string, Guid?> newPeople)
    {
        foreach (var (id, member) in newPeople)
        {
            _people[id] = member;
        }
    }

    [GeneratedRegex(@"^[\w.+-]+/[\w.+-]+$")]
    private static partial Regex MimeType();
}

/// <summary>The <see cref="SourceChange.Field"/> names for item properties (docs/import-format.md, Changes).</summary>
internal static class ChangeFields
{
    public const string Status = "status";
    public const string Type = "type";
    public const string Title = "title";
    public const string Description = "description";
    public const string Priority = "priority";
    public const string Assignee = "assignee";
    public const string Parent = "parent";
    public const string Sprint = "sprint";
    public const string Labels = "labels";
    public const string EstimateSeconds = "estimateSeconds";
    public const string Points = "points";
    public const string Start = "start";
    public const string Due = "due";
}
