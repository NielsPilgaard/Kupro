using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Kupro;

/// <summary>
/// A Kupro export ZIP (task 016, format 1) as a source: moves a workspace, or a self-hosted one, into another (task
/// 023). Keys, people, sprints, milestones, comments, attachments and field history come across through the same
/// pipeline as any source. Reads <c>workspace.json</c> whole, so a very large workspace needs memory to match.
/// </summary>
public sealed class KuproExportConnector : ISourceConnector, IDisposable
{
    private const int Format = 1;

    /// <summary>Kupro's states, with categories that map each back onto itself.</summary>
    private static readonly SourceStatus[] States =
    [
        new() { Id = "backlog", Name = "Backlog", Category = "backlog" },
        new() { Id = "todo", Name = "Todo", Category = "todo" },
        new() { Id = "inProgress", Name = "In Progress", Category = "inprogress" },
        new() { Id = "inReview", Name = "In Review", Category = "inprogress" },
        new() { Id = "done", Name = "Done", Category = "done" },
        new() { Id = "canceled", Name = "Canceled", Category = "done" },
    ];

    private readonly ZipArchive _zip;
    private readonly JsonDocument _json;
    private readonly JsonElement _root;

    private KuproExportConnector(ZipArchive zip, JsonDocument json) => (_zip, _json, _root) = (zip, json, json.RootElement);

    /// <summary>Opens the export. The stream must be seekable and stay open while the connector is used.</summary>
    public static async Task<KuproExportConnector> OpenAsync(Stream zip, CancellationToken ct)
    {
        var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            var entry = archive.GetEntry("workspace.json") ?? throw new SnapshotFormatException("This is not a Kupro export: it has no workspace.json.");
            JsonDocument json;
            await using (var stream = await entry.OpenAsync(ct))
            {
                json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }

            if (!json.RootElement.TryGetProperty("format", out var format) || format.GetInt32() != Format)
            {
                json.Dispose();
                throw new SnapshotFormatException($"Kupro can read export format {Format}; this export has another.");
            }

            return new KuproExportConnector(archive, json);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _json.Dispose();
        _zip.Dispose();
    }

    public Task<SourceDescription> DescribeAsync(CancellationToken ct) => Task.FromResult(new SourceDescription
    {
        Source = new SnapshotSource { Kind = SourceKinds.Kupro },
        Projects = [.. Rows("projects").Select(p => new SourceProject
        {
            Id = Str(p, "id")!,
            Key = Str(p, "key"),
            Name = Str(p, "name") ?? Str(p, "key")!,
            Capacity = Capacity(Str(p, "id")!),
        })],
        People = People(),
        ItemTypes =
        [
            new SourceItemType { Id = "epic", Name = "Epic", Level = 2 },
            new SourceItemType { Id = "feature", Name = "Feature", Level = 1 },
            new SourceItemType { Id = "task", Name = "Task", Level = 0 },
        ],
        Statuses = States,
    });

    /// <summary>Every milestone with a date: in a move between Kupro workspaces they are all milestones again.</summary>
    public IReadOnlyList<string> Milestones() => [.. Rows("milestones").Where(m => Str(m, "date") is not null).Select(m => Str(m, "id")!)];

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var projects = scope.Projects.Count == 0 ? null : new HashSet<string>(scope.Projects, StringComparer.Ordinal);
        foreach (var person in People())
        {
            yield return person;
        }

        foreach (var sprint in Rows("sprints").Where(s => projects is null || projects.Contains(Str(s, "projectId")!)))
        {
            yield return new SourceSprint
            {
                Id = Str(sprint, "id")!,
                Name = Str(sprint, "name")!,
                Project = Str(sprint, "projectId"),
                Start = Day(Str(sprint, "startDate")),
                End = Day(Str(sprint, "endDate")),
                Closed = Str(sprint, "state") == "closed",
            };
        }

        foreach (var milestone in Rows("milestones").Where(m => projects is null || projects.Contains(Str(m, "projectId")!)))
        {
            yield return new SourceVersion { Id = Str(milestone, "id")!, Project = Str(milestone, "projectId")!, Name = Str(milestone, "name")!, Date = Day(Str(milestone, "date")) };
        }

        var labels = Rows("labels").ToDictionary(l => Str(l, "id")!, l => Str(l, "name")!, StringComparer.Ordinal);
        var items = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in Rows("issues").Where(i => projects is null || projects.Contains(Str(i, "projectId")!)))
        {
            ct.ThrowIfCancellationRequested();
            var id = Str(issue, "id")!;
            items.Add(id);
            var created = Time(Str(issue, "createdAt"))!.Value;
            yield return new SourceItem
            {
                Id = id,
                Key = Str(issue, "key"),
                Project = Str(issue, "projectId")!,
                Type = Str(issue, "type") ?? "task",
                Status = Str(issue, "state") ?? "todo",
                Title = Str(issue, "title") ?? "",
                Description = Str(issue, "description"),
                DescriptionMarkup = MarkupKind.Markdown,
                Parent = Str(issue, "parentId"),
                Assignee = Str(issue, "assigneeId"),
                Creator = Str(issue, "createdById"),
                Priority = Str(issue, "priority"),
                Labels = issue.TryGetProperty("labels", out var own) && own.ValueKind == JsonValueKind.Array
                    ? [.. own.EnumerateArray().Select(l => labels.GetValueOrDefault(Str(l, "labelId")!)).OfType<string>()]
                    : [],
                EstimateSeconds = Num(issue, "effort") is { } hours ? (long)Math.Round(hours * 3600) : null,
                Start = Day(Str(issue, "startDate")),
                Due = Day(Str(issue, "dueDate")),
                Sprint = Str(issue, "sprintId"),
                Created = created,
                Updated = Time(Str(issue, "updatedAt")) ?? created,
                Resolved = Time(Str(issue, "completedAt")),
            };
        }

        foreach (var relation in Rows("issueRelations").Where(r => items.Contains(Str(r, "sourceId")!) && items.Contains(Str(r, "targetId")!)))
        {
            yield return new SourceRelation
            {
                From = Str(relation, "sourceId")!,
                To = Str(relation, "targetId")!,
                Kind = Str(relation, "type") switch
                {
                    "blocks" => RelationKinds.Blocks,
                    "duplicates" => RelationKinds.Duplicates,
                    _ => RelationKinds.Relates,
                },
            };
        }

        foreach (var comment in Rows("comments").Where(c => items.Contains(Str(c, "issueId")!)))
        {
            yield return new SourceComment
            {
                Id = Str(comment, "id")!,
                Item = Str(comment, "issueId")!,
                Author = Str(comment, "authorId"),
                Body = Str(comment, "body") ?? "",
                Markup = MarkupKind.Markdown,
                Created = Time(Str(comment, "createdAt"))!.Value,
                Updated = Time(Str(comment, "editedAt")),
            };
        }

        foreach (var attachment in Rows("attachments").Where(a => Str(a, "issueId") is { } issue && items.Contains(issue)))
        {
            var id = Str(attachment, "id")!;
            yield return new SourceAttachment
            {
                Id = id,
                Item = Str(attachment, "issueId")!,
                FileName = Str(attachment, "fileName") ?? "attachment",
                ContentType = Str(attachment, "contentType"),
                Size = Num(attachment, "size") is { } size ? (long)size : null,
                Author = Str(attachment, "uploadedById"),
                Created = Time(Str(attachment, "createdAt"))!.Value,

                // Markdown in the export points at files as attachment:ID; the import points that at the copy.
                Url = $"attachment:{id}",
            };
        }

        foreach (var entry in Rows("activity").Where(a => Str(a, "kind") == "updated" && items.Contains(Str(a, "issueId")!)))
        {
            foreach (var change in Changes(entry))
            {
                yield return change;
            }
        }

        yield return new ReadCursor(null);
    }

    /// <summary>The file of an export's attachment: <c>attachments/&lt;issue&gt;/&lt;id&gt;-&lt;name&gt;</c>.</summary>
    public async Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct)
    {
        var entry = _zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("attachments/", StringComparison.Ordinal)
                && e.Name.StartsWith(attachment.Id + "-", StringComparison.Ordinal))
            ?? throw new SourceAccessException($"The export has no file for attachment {attachment.FileName}.");
        var copy = new MemoryStream();
        await using (var stream = await entry.OpenAsync(ct))
        {
            await stream.CopyToAsync(copy, ct);
        }

        copy.Position = 0;
        return copy;
    }

    /// <summary>Members as people: a person by their name and email, a placeholder or an agent by the name it shows.</summary>
    private List<SourcePerson> People()
    {
        var users = Rows("users").ToDictionary(u => Str(u, "id")!, StringComparer.Ordinal);
        return
        [
            .. Rows("members").Select(m =>
            {
                var user = Str(m, "userId") is { } userId && users.TryGetValue(userId, out var found) ? found : (JsonElement?)null;
                var name = Str(m, "displayName") ?? (user is { } u ? Str(u, "displayName") : null) ?? Str(m, "agentName") ?? "Unknown";
                var email = (user is { } v ? Str(v, "email") : null) ?? Str(m, "placeholderEmail") ?? Str(m, "inviteEmail");
                return new SourcePerson { Id = Str(m, "id")!, Name = name, Email = email };
            }),
        ];
    }

    /// <summary>One <c>updated</c> entry's field changes, in the names a snapshot uses.</summary>
    private static IEnumerable<SourceChange> Changes(JsonElement entry)
    {
        if (Str(entry, "data") is not { } data)
        {
            yield break;
        }

        using var doc = JsonDocument.Parse(data);
        var actor = Str(entry, "actorId");
        var at = Time(Str(entry, "createdAt"))!.Value;
        var id = Str(entry, "id")!;
        foreach (var field in doc.RootElement.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.Object))
        {
            var name = field.Name switch
            {
                "state" => "status",
                "startDate" => "start",
                "dueDate" => "due",
                "effort" => "estimateSeconds",
                _ => field.Name,
            };
            string? Side(string side) =>
                field.Value.TryGetProperty(side, out var v) && v.ValueKind != JsonValueKind.Null
                    ? field.Name == "effort" && v.ValueKind == JsonValueKind.Number ? ((long)Math.Round(v.GetDecimal() * 3600)).ToString(CultureInfo.InvariantCulture)
                    : v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray().Select(x => x.ToString()))
                    : v.ToString()
                    : null;
            yield return new SourceChange { Id = $"{id}/{field.Name}", Item = Str(entry, "issueId")!, Actor = actor, At = at, Field = name, From = Side("from"), To = Side("to") };
        }
    }

    /// <summary>A project's headcount steps for the delivery plan, or none.</summary>
    private List<SourceCapacity>? Capacity(string projectId)
    {
        List<SourceCapacity> steps =
        [
            .. Rows("projectCapacity")
                .Where(c => Str(c, "projectId") == projectId && Str(c, "fromMonth") is not null && Num(c, "people") is not null)
                .Select(c => new SourceCapacity { From = DateOnly.Parse(Str(c, "fromMonth")!, CultureInfo.InvariantCulture), People = Num(c, "people")!.Value }),
        ];
        return steps.Count == 0 ? null : steps;
    }

    private List<JsonElement> Rows(string table) =>
        _root.TryGetProperty(table, out var rows) && rows.ValueKind == JsonValueKind.Array ? [.. rows.EnumerateArray()] : [];

    private static string? Str(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static decimal? Num(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static DateTimeOffset? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static DateOnly? Day(string? text) =>
        text is not null && DateOnly.TryParse(text.Length >= 10 ? text[..10] : text, CultureInfo.InvariantCulture, out var day) ? day : null;
}
