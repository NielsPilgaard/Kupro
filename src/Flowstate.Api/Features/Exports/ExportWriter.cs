using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Attachments;
using Flowstate.Api.Features.Comments;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Meetings;
using Flowstate.Api.Features.Plan;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Sprints;
using Flowstate.Api.Features.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Features.Exports;

/// <summary>
/// Writes one workspace into a ZIP (task 016): <c>workspace.json</c> with every table as stored, <c>issues.csv</c> and
/// <c>comments.csv</c> for spreadsheets, and the attachment files. Reads through a DbContext scoped to that workspace,
/// so the tenant filter limits every query to it. Entities are written as they are, so new fields are exported without
/// touching this file; a new table needs a section here (the completeness test fails until it has one).
/// </summary>
internal static class ExportWriter
{
    public const int Format = 1;

    /// <summary>Tenant-owned tables in <c>workspace.json</c>. <see cref="IssueLabel"/> rows sit inside each issue.</summary>
    public static readonly IReadOnlySet<Type> Exported = new HashSet<Type>
    {
        typeof(Member), typeof(Project), typeof(Label), typeof(Issue), typeof(IssueLabel), typeof(IssueRelation),
        typeof(Sprint), typeof(Milestone), typeof(ProjectCapacity), typeof(Comment), typeof(ActivityEntry),
        typeof(Attachment), typeof(MeetingSession), typeof(TranscriptSegment), typeof(MeetingProposal), typeof(MeetingSettings),
    };

    /// <summary>Tenant-owned tables left out on purpose: the export records themselves.</summary>
    public static readonly IReadOnlySet<Type> NotExported = new HashSet<Type> { typeof(WorkspaceExport) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static async Task WriteAsync(Stream output, FlowstateDbContext db, ObjectStorage storage, DateTimeOffset now, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var workspace = await db.Workspaces.AsNoTracking()
            .SingleAsync(w => w.Id == db.CurrentWorkspaceId, ct);

        await WriteEntryAsync(zip, "README.txt", s => WriteReadmeAsync(s, workspace, now));
        await WriteEntryAsync(zip, "workspace.json", s => WriteJsonAsync(s, db, workspace, now, ct));
        await WriteEntryAsync(zip, "issues.csv", s => WriteIssuesCsvAsync(s, db, ct));
        await WriteEntryAsync(zip, "comments.csv", s => WriteCommentsCsvAsync(s, db, ct));

        var files = await db.Attachments.AsNoTracking().Where(a => a.Ready).OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.WorkspaceId, a.FileName, Issue = db.Issues.Where(i => i.Id == a.IssueId).Select(i => i.Key).FirstOrDefault() })
            .ToListAsync(ct);
        var missing = new List<string>();
        foreach (var file in files)
        {
            var path = $"attachments/{file.Issue ?? "unlinked"}/{file.Id}-{SafeName(file.FileName)}";
            await using var source = await storage.OpenReadAsync($"{file.WorkspaceId}/{file.Id}", ct);
            if (source is null)
            {
                // A lost file shouldn't cost the owner the rest of their data; the list says what is missing.
                missing.Add(path);
                continue;
            }

            // Images and archives are already compressed.
            await using var target = await zip.CreateEntry(path, CompressionLevel.Fastest).OpenAsync(ct);
            await source.CopyToAsync(target, ct);
        }

        if (missing.Count > 0)
        {
            await WriteEntryAsync(zip, "attachments/MISSING.txt", async s =>
            {
                await using var writer = new StreamWriter(s, new UTF8Encoding(false));
                await writer.WriteLineAsync("These attachments are recorded in workspace.json, but their files were not found in storage:");
                foreach (var path in missing)
                {
                    await writer.WriteLineAsync(path);
                }
            });
        }
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, Func<Stream, Task> write)
    {
        await using var stream = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync();
        await write(stream);
    }

    private static async Task WriteReadmeAsync(Stream stream, Workspace workspace, DateTimeOffset now)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync($"""
            Flowstate export of "{workspace.Name}" ({workspace.Slug}), {now:yyyy-MM-dd HH:mm} UTC. Format {Format}.

            workspace.json   Everything in the workspace, one array per table, ids as in the Flowstate API.
                             "users" holds the name and email of each member who is a person.
            issues.csv       One row per issue, for spreadsheets.
            comments.csv     One row per comment.
            attachments/     Uploaded files, by issue key, named <attachment id>-<file name>.
                             Markdown refers to them as attachment:<attachment id>.

            Meeting transcripts are included only if the workspace keeps them after a meeting ends.
            """);
    }

    private static async Task WriteJsonAsync(Stream stream, FlowstateDbContext db, Workspace workspace, DateTimeOffset now, CancellationToken ct)
    {
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("format", Format);
        json.WriteString("exportedAt", now);
        json.WritePropertyName("workspace");
        JsonSerializer.Serialize(json, workspace, Json);

        // Users aren't tenant-owned; only the people who are members of this workspace, and only name and email.
        await ArrayAsync(json, "users", db.Users.AsNoTracking()
            .Where(u => db.Members.Any(m => m.UserId == u.Id)).OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.DisplayName, u.Email }), ct);
        await ArrayAsync(json, "members", db.Members.AsNoTracking().OrderBy(m => m.CreatedAt), ct);
        await ArrayAsync(json, "projects", db.Projects.AsNoTracking().OrderBy(p => p.Key), ct);
        await ArrayAsync(json, "labels", db.Labels.AsNoTracking().OrderBy(l => l.Name), ct);
        await ArrayAsync(json, "issues", db.Issues.AsNoTracking().Include(i => i.Labels).OrderBy(i => i.ProjectId).ThenBy(i => i.Number), ct);
        await ArrayAsync(json, "issueRelations", db.IssueRelations.AsNoTracking().OrderBy(r => r.CreatedAt), ct);
        await ArrayAsync(json, "sprints", db.Sprints.AsNoTracking().OrderBy(s => s.StartDate), ct);
        await ArrayAsync(json, "milestones", db.Milestones.AsNoTracking().OrderBy(m => m.Date), ct);
        await ArrayAsync(json, "projectCapacity", db.ProjectCapacity.AsNoTracking().OrderBy(c => c.FromMonth), ct);
        await ArrayAsync(json, "comments", db.Comments.AsNoTracking().OrderBy(c => c.CreatedAt), ct);
        await ArrayAsync(json, "activity", db.Activity.AsNoTracking().OrderBy(a => a.CreatedAt), ct);
        await ArrayAsync(json, "attachments", db.Attachments.AsNoTracking().OrderBy(a => a.CreatedAt), ct);
        await ArrayAsync(json, "meetings", db.Meetings.AsNoTracking().OrderBy(m => m.StartedAt), ct);
        await ArrayAsync(json, "transcriptSegments", db.TranscriptSegments.AsNoTracking().OrderBy(t => t.CreatedAt), ct);
        await ArrayAsync(json, "meetingProposals", db.MeetingProposals.AsNoTracking().OrderBy(p => p.CreatedAt), ct);
        await ArrayAsync(json, "meetingSettings", db.MeetingSettings.AsNoTracking(), ct);
        json.WriteEndObject();
    }

    /// <summary>Streams rows straight from the database into the JSON, flushing as it goes, so memory stays flat.</summary>
    private static async Task ArrayAsync<T>(Utf8JsonWriter json, string name, IQueryable<T> rows, CancellationToken ct)
    {
        json.WriteStartArray(name);
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            JsonSerializer.Serialize(json, row, Json);
            if (json.BytesPending > 64 * 1024)
            {
                await json.FlushAsync(ct);
            }
        }

        json.WriteEndArray();
        await json.FlushAsync(ct);
    }

    private static async Task WriteIssuesCsvAsync(Stream stream, FlowstateDbContext db, CancellationToken ct)
    {
        var members = await db.Members.AsNoTracking().Select(MemberRefs.Projection(db)).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var sprints = await db.Sprints.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var labels = await db.Labels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var keys = await db.Issues.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Key, ct);

        await using var csv = new StreamWriter(stream, Utf8Bom) { NewLine = "\r\n" };
        await csv.WriteLineAsync("key,type,title,state,priority,assignee,sprint,labels,effort_days,start_date,due_date,parent,created_at,updated_at,completed_at,description");
        await foreach (var i in db.Issues.AsNoTracking().Include(i => i.Labels).OrderBy(i => i.ProjectId).ThenBy(i => i.Number)
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            await csv.WriteLineAsync(Row(
                i.Key, Enum(i.Type), i.Title, Enum(i.State), Enum(i.Priority),
                i.AssigneeId is { } a ? members.GetValueOrDefault(a) : null,
                i.SprintId is { } s ? sprints.GetValueOrDefault(s) : null,
                string.Join("; ", i.Labels.Select(l => labels.GetValueOrDefault(l.LabelId)).Order(StringComparer.Ordinal)),
                i.Effort?.ToString(System.Globalization.CultureInfo.InvariantCulture), i.StartDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), i.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                i.ParentId is { } p ? keys.GetValueOrDefault(p) : null,
                Time(i.CreatedAt), Time(i.UpdatedAt), i.CompletedAt is { } c ? Time(c) : null, i.Description));
        }
    }

    private static async Task WriteCommentsCsvAsync(Stream stream, FlowstateDbContext db, CancellationToken ct)
    {
        var members = await db.Members.AsNoTracking().Select(MemberRefs.Projection(db)).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        await using var csv = new StreamWriter(stream, Utf8Bom) { NewLine = "\r\n" };
        await csv.WriteLineAsync("issue,author,created_at,edited_at,body");
        await foreach (var c in db.Comments.AsNoTracking().OrderBy(c => c.CreatedAt)
                           .Select(c => new { Issue = db.Issues.Where(i => i.Id == c.IssueId).Select(i => i.Key).First(), c.AuthorId, c.CreatedAt, c.EditedAt, c.Body })
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            await csv.WriteLineAsync(Row(c.Issue, members.GetValueOrDefault(c.AuthorId), Time(c.CreatedAt), c.EditedAt is { } e ? Time(e) : null, c.Body));
        }
    }

    private static string Enum<T>(T value) where T : struct, System.Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// RFC 4180: quote fields with commas, quotes or line breaks; double the quotes inside. A field starting with
    /// = + - @ or a control character gets a leading apostrophe, so a spreadsheet shows it as text instead of running
    /// it as a formula (CSV injection: anyone in the workspace can write an issue title).
    /// </summary>
    private static string Row(params string?[] fields) => string.Join(',', fields.Select(Field));

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    private static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars().Append('/').Append('\\'))
        {
            name = name.Replace(c, '_');
        }

        return name.Length == 0 ? "file" : name;
    }
}
