using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Comments;
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
    private async Task<ImportReferences> ReferencesAsync(SnapshotReader snapshot, SourceRead read, Dictionary<string, int> numbers, CancellationToken ct)
    {
        var files = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);
        var missing = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var attachment in snapshot.ReadAttachmentsAsync(ct))
        {
            if (!seen.Add(attachment.Id) || read.Fit.HolderOf(attachment.Item) is null)
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
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> known;
        List<(string Url, string Key)> earlierLinks;
        List<(Guid Id, string Json)> earlierFiles;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            known = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item)
                .Join(db.Issues, i => i.EntityId, x => x.Id, (i, x) => new { i.SourceId, x.Key })
                .ToDictionaryAsync(r => r.SourceId, r => r.Key, StringComparer.Ordinal, ct);
            _candidates ??= await CandidatesAsync(db, ct);
            earlierLinks = await db.IssueAliases.AsNoTracking().Where(a => a.ImportId == _importId && (a.Alias.Contains("://") || EF.Functions.Like(a.Alias, "%#%")))
                .Join(db.Issues, a => a.IssueId, x => x.Id, (a, x) => new ValueTuple<string, string>(a.Alias, x.Key)).ToListAsync(ct);
            earlierFiles = await db.ImportedItems.AsNoTracking().Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Attachment && i.LastValues != null)
                .Select(i => new ValueTuple<Guid, string>(i.EntityId, i.LastValues!)).ToListAsync(ct);
        }

        foreach (var item in read.Refs)
        {
            var key = known.GetValueOrDefault(item.Id) ?? (numbers.TryGetValue(item.Id, out var number) ? $"{item.ProjectKey}-{number}" : null);
            if (key is null)
            {
                continue;
            }

            if (item.Url is { Length: > 0 } url)
            {
                issues.TryAdd(url.Trim(), key);
            }

            // A source that numbers its items (Azure DevOps) refers to them as #4567 in text; GitHub as owner/repo#89,
            // or #89 within the same repository.
            var sourceKey = item.Key?.Trim();
            if (sourceKey is { Length: > 0 } && sourceKey.All(char.IsAsciiDigit))
            {
                issues.TryAdd($"#{sourceKey}", key);
            }
            else if (sourceKey?.LastIndexOf('#') is > 0 and var hash && sourceKey.Contains('/', StringComparison.Ordinal))
            {
                issues.TryAdd(sourceKey, key);
                scopes[item.Id] = sourceKey[..hash];
            }
        }

        // What a delta doesn't list is still linked to: earlier runs left its URL as an alias, and its files' as a row.
        foreach (var (url, key) in earlierLinks)
        {
            issues.TryAdd(url, key);
        }

        foreach (var (entityId, json) in earlierFiles)
        {
            var file = JsonSerializer.Deserialize<StoredFile>(json, Json)!;
            files.TryAdd(file.Url, new AttachmentRef(entityId, file.FileName, file.Image));
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

        return new ImportReferences(names, handles, files, issues, scopes);
    }

    private Guid? IssueOf(string sourceItem) => _fit.HolderOf(sourceItem) is { } holder && _items.TryGetValue(holder, out var id) ? id : null;

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
                metrics.Bytes(size);
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
                var row = Record(db, ImportedKinds.Attachment, source.Id, ImportedEntityKind.Attachment, id);
                if (source.Url is { Length: > 0 } url)
                {
                    row.LastValues = JsonSerializer.Serialize(new StoredFile(url.Trim(), FileName(source.FileName), IsImage(contentType)), Json);
                }

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
        var body = references.Rewrite(MarkupConverters.ToMarkdown(comment.Body, comment.Markup), comment.Item) ?? EmptyComment;
        const string Note = "\n\n*Cut at import: the original is too long for Kupro.*";
        return body.Length <= CommentRequest.MaxBody ? body : string.Concat(body.AsSpan(0, CommentRequest.MaxBody - Note.Length), Note);
    }

    // ---------------------------------------------------------------- worklogs

    /// <summary>
    /// With time registration on, worklogs become time entries for the person who logged them, as the source recorded
    /// them (ADR-021 Import). A worklog by someone skipped, or by nobody, stays in the item's summed field: one member
    /// must not hold several people's time.
    /// </summary>
    private async Task WriteWorklogsAsync(SnapshotReader snapshot, CancellationToken ct)
    {
        var batch = new List<SourceWorklog>(BatchSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var worklog in snapshot.ReadWorklogsAsync(ct))
        {
            if (!seen.Add(worklog.Id) || worklog.Author is null || IssueOf(worklog.Item) is null)
            {
                continue;
            }

            batch.Add(worklog);
            if (batch.Count == BatchSize)
            {
                await WriteWorklogBatchAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteWorklogBatchAsync(batch, ct);
        }
    }

    /// <summary>
    /// Per item, the minutes of the worklogs that become entries: by someone the owner didn't skip, as
    /// <see cref="WriteWorklogsAsync"/> writes them. Known before the items are written, so their summed field keeps
    /// only the rest.
    /// </summary>
    private async Task<Dictionary<string, int>> HeldMinutesAsync(SnapshotReader snapshot, CancellationToken ct)
    {
        var holds = Holds();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var held = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var worklog in snapshot.ReadWorklogsAsync(ct))
        {
            if (seen.Add(worklog.Id) && holds(worklog))
            {
                held[worklog.Item] = held.GetValueOrDefault(worklog.Item) + worklog.Minutes;
            }
        }

        return held;
    }

    /// <summary>
    /// Whether a worklog becomes an entry: someone logged it and the owner didn't skip them, by the mapping or, for
    /// someone it doesn't name, the default it would propose (bots skipped).
    /// </summary>
    private Func<SourceWorklog, bool> Holds()
    {
        var people = _header.People.DistinctBy(p => p.Id).ToDictionary(p => p.Id, StringComparer.Ordinal);
        return worklog => worklog.Author is { } author
            && (_mapping.People.TryGetValue(author, out var target)
                ? target.Action != PersonAction.Skip
                : people.GetValueOrDefault(author) is { Bot: false });
    }

    private async Task<bool> TimeRegistrationAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().Where(w => w.Id == workspace.RequireWorkspaceId()).Select(w => w.TimeRegistration).SingleAsync(ct);
    }

    private async Task WriteWorklogBatchAsync(List<SourceWorklog> batch, CancellationToken ct)
    {
        var done = await DoneAsync(ImportedKinds.Worklog, batch.Select(w => w.Id), ct);
        var todo = batch.Where(w => !done.Contains(w.Id)).ToList();
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
            var actor = await ActorsAsync(db, todo.Select(w => w.Author), newPeople, delta, ct);
            foreach (var source in todo)
            {
                if (actor(source.Author) is not (var member, null))
                {
                    continue;
                }

                var id = Guid.CreateVersion7();
                time.AddImported(db, id, member, IssueOf(source.Item)!.Value, source.Date, source.Minutes, source.Note,
                    (source.Created ?? clock.GetUtcNow()).ToUniversalTime(), _ownerId);
                Record(db, ImportedKinds.Worklog, source.Id, ImportedEntityKind.TimeEntry, id);
                delta.TimeEntries++;
            }

            await SaveCountsAsync(db, delta, ct);
        }, ct);

        Merge(newPeople);
        _counts.Add(delta);
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
        var marks = await ChangeMarksAsync(ct);
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

            if (ImportedHistory.Seen(marks, change))
            {
                continue;
            }

            var mark = ImportedHistory.Mark(change);
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

    /// <summary>Per item, the newest change an earlier run replayed.</summary>
    private async Task<Dictionary<string, string>> ChangeMarksAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ImportedItems.AsNoTracking()
            .Where(i => i.ImportId == _importId && i.SourceKind == ImportedKinds.Item && i.SeenChangeMark != null)
            .ToDictionaryAsync(i => i.SourceId, i => i.SeenChangeMark!, StringComparer.Ordinal, ct);
    }

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
            var targets = new ChangeTargets(
                p => Member(p, newPeople),
                i => _items.TryGetValue(i, out var id) ? id : null,
                (item, s) => projectOf.GetValueOrDefault(item) is { } projectKey && _sprints.TryGetValue(SprintKey(s, projectKey), out var id) ? id : null);
            foreach (var change in batch)
            {
                var (actorId, sourceActor) = actor(change.Actor);
                var (kind, data) = _changes.Translate(change, targets);
                if (kind is null)
                {
                    continue;
                }

                if (sourceActor is not null)
                {
                    data["sourceActor"] = sourceActor;
                }

                activity.RecordImported(db, _items[change.Item], kind, actorId, change.At.ToUniversalTime(), data);
                if (kind == ChangeTranslator.SourceChanged)
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

    /// <summary>A copied file, as text imported later needs to find it.</summary>
    private sealed record StoredFile(string Url, string FileName, bool Image);
}
