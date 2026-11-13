using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kupro.Import.Snapshot;
using static Kupro.Import.Jira.JiraConnector;

namespace Kupro.Import.Jira;

/// <summary>
/// Turns Jira issues, links, comments, attachments and changelogs into snapshot records for one read. Keeps the state a
/// read needs across issues: people already reported, links already emitted (both ends carry them), keys seen (for
/// Epic Link) and the newest update time for the cursor.
/// </summary>
internal sealed partial class JiraReading(Catalog catalog, Uri site)
{
    /// <summary>Changelog fields that are bookkeeping, or history the item already carries (attachments, comments).</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rank", "timeestimate", "timespent", "WorklogId", "WorklogTimeSpent", "Attachment", "Workflow", "Comment",
        "RemoteIssueLink", "Epic Colour", "Epic Color", "Epic Status", "statuscategorychangedate",
    };

    private static readonly HashSet<string> ParentFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "parent", "IssueParentAssociation", "Parent", "Parent Issue", "Epic Link", "Epic Child",
    };

    private readonly string _site = site.AbsoluteUri.TrimEnd('/');
    private readonly Dictionary<string, bool> _people = new(StringComparer.Ordinal);
    private readonly List<SourcePerson> _pending = [];
    private readonly HashSet<string> _links = new(StringComparer.Ordinal);

    /// <summary>The newest <c>updated</c> read: the next delta starts there.</summary>
    public DateTimeOffset? Newest { get; set; }

    /// <summary>Issue IDs by key, from this read and Epic Link lookups (null: no such issue).</summary>
    public Dictionary<string, string?> IdsByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>People first seen since the last call, to stream before the records that refer to them.</summary>
    public List<SourcePerson> TakePeople()
    {
        var people = _pending.ToList();
        _pending.Clear();
        return people;
    }

    public SourceItem Item(JiraProject project, JiraIssue issue, string? epic, string? sprint)
    {
        var f = issue.Fields;
        IdsByKey[issue.Key] = issue.Id;
        var created = Jira.Time(Jira.Text(f, "created")) ?? DateTimeOffset.UnixEpoch;
        var updated = Jira.Time(Jira.Text(f, "updated")) ?? created;

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (Jira.Array(f, "components").Select(c => Jira.Prop(c, "name")).OfType<string>().ToArray() is { Length: > 0 } components)
        {
            fields[ComponentsField] = FieldValue.Options(components);
        }

        if (Jira.Prop(Jira.Field(f, "resolution"), "name") is { Length: > 0 } resolution)
        {
            fields[ResolutionField] = FieldValue.Text(resolution);
        }

        if (Jira.Number(Jira.Field(f, "timespent")) is { } spent && spent > 0)
        {
            fields[TimeLoggedField] = FieldValue.Number(Math.Round(spent / 3600m, 2));
        }

        foreach (var (id, field) in catalog.Exported)
        {
            if (Jira.Field(f, id) is { } value && Value(field, value) is { } converted)
            {
                fields[id] = converted;
            }
        }

        var attachments = Jira.Array(f, "attachment").ToList();
        return new SourceItem
        {
            Id = issue.Id,
            Key = issue.Key,
            Project = project.Id,
            Type = Jira.Prop(Jira.Field(f, "issuetype"), "id") ?? "",
            Status = Jira.Prop(Jira.Field(f, "status"), "id") ?? "",
            Title = Jira.Text(f, "summary") ?? "",
            Description = Markup(Jira.Field(f, "description"), attachments),
            DescriptionMarkup = catalog.Cloud ? MarkupKind.Adf : MarkupKind.JiraWiki,
            Parent = Jira.Prop(Jira.Field(f, "parent"), "id") ?? epic,
            Assignee = PersonId(Jira.Field(f, "assignee")),
            Creator = PersonId(Jira.Field(f, "reporter")) ?? PersonId(Jira.Field(f, "creator")),
            Priority = Jira.Prop(Jira.Field(f, "priority"), "name"),
            Labels = [.. Jira.Array(f, "labels").Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : null).OfType<string>()],
            Fields = fields,
            EstimateSeconds = Jira.Number(Jira.Field(f, "timeoriginalestimate")) is { } seconds ? (long)seconds : null,
            Points = catalog.StoryPointFields.Select(id => Jira.Number(Jira.Field(f, id))).FirstOrDefault(p => p is not null),
            Start = catalog.StartDateFields.Select(id => Jira.Day(Jira.Text(f, id))).FirstOrDefault(d => d is not null),
            Due = Jira.Day(Jira.Text(f, "duedate")),
            Sprint = sprint,
            Versions = [.. Jira.Array(f, "fixVersions").Select(v => Jira.Prop(v, "id")).OfType<string>()],
            Url = $"{_site}/browse/{issue.Key}",
            Created = created,
            Updated = updated,
            Resolved = Jira.Time(Jira.Text(f, "resolutiondate")),
        };
    }

    public IEnumerable<SourceAttachment> Attachments(JiraIssue issue, SourceItem item)
    {
        foreach (var file in Jira.Array(issue.Fields, "attachment"))
        {
            if (Jira.Prop(file, "id") is not { } id)
            {
                continue;
            }

            yield return new SourceAttachment
            {
                Id = id,
                Item = item.Id,
                FileName = Jira.Prop(file, "filename") ?? id,
                ContentType = Jira.Prop(file, "mimeType"),
                Size = Jira.Number(Jira.Field(file, "size")) is { } size ? (long)size : null,
                Author = PersonId(Jira.Field(file, "author")),
                Created = Jira.Time(Jira.Prop(file, "created")) ?? item.Created,
                Url = Jira.Prop(file, "content"),
            };
        }
    }

    /// <summary>
    /// Issue links. Each sits on both issues, so it is emitted once per link ID, always outward: <c>From</c> blocks or
    /// duplicates <c>To</c>. Blocks and Duplicate keep their meaning; Relates, Cloners and any custom type relate,
    /// keeping the source's own wording.
    /// </summary>
    public IEnumerable<SourceRelation> Relations(JiraIssue issue, SourceItem item)
    {
        foreach (var link in Jira.Array(issue.Fields, "issuelinks"))
        {
            if (Jira.Prop(link, "id") is not { } id || !_links.Add(id) || Jira.Field(link, "type") is not { } type)
            {
                continue;
            }

            var (from, to) = Jira.Prop(Jira.Field(link, "outwardIssue"), "id") is { } outward ? (item.Id, outward)
                : Jira.Prop(Jira.Field(link, "inwardIssue"), "id") is { } inward ? (inward, item.Id)
                : (null, null);
            if (from is null || to is null)
            {
                continue;
            }

            var name = Jira.Prop(type, "name") ?? "";
            yield return new SourceRelation
            {
                From = from,
                To = to,
                Kind = name.ToLowerInvariant() switch
                {
                    "blocks" => RelationKinds.Blocks,
                    "duplicate" => RelationKinds.Duplicates,
                    _ => RelationKinds.Relates,
                },
                Name = Jira.Prop(type, "outward") ?? name,
            };
        }
    }

    public SourceComment Comment(SourceItem item, JiraComment comment, IReadOnlyList<JsonElement> attachments)
    {
        var created = Jira.Time(comment.Created) ?? item.Created;
        var updated = Jira.Time(comment.Updated);
        return new SourceComment
        {
            Id = comment.Id,
            Item = item.Id,
            Author = PersonId(comment.Author),
            Body = Markup(comment.Body, attachments) ?? "",
            Markup = catalog.Cloud ? MarkupKind.Adf : MarkupKind.JiraWiki,
            Created = created,
            Updated = updated > created ? updated : null,
        };
    }

    /// <summary>
    /// The changelog as changes, one per changed field (ID <c>history:index</c>). Fields the item has map to their
    /// property; custom fields and anything else Jira tracks are source changes; bookkeeping is skipped.
    /// </summary>
    public IEnumerable<SourceChange> Changes(SourceItem item, IReadOnlyList<JiraHistory> histories)
    {
        foreach (var history in histories)
        {
            var at = Jira.Time(history.Created) ?? item.Updated;
            var actor = PersonId(history.Author);
            for (var i = 0; i < history.Items.Count; i++)
            {
                if (Map(history.Items[i]) is not { } mapped)
                {
                    continue;
                }

                yield return new SourceChange
                {
                    Id = $"{history.Id}:{i.ToString(CultureInfo.InvariantCulture)}",
                    Item = item.Id,
                    Actor = actor,
                    At = at,
                    Field = mapped.Field,
                    From = mapped.From,
                    To = mapped.To,
                    FromText = mapped.FromText,
                    ToText = mapped.ToText,
                };
            }
        }
    }

    private sealed record Mapped(string Field, string? From, string? To, string? FromText = null, string? ToText = null);

    private Mapped? Map(JiraHistoryItem change)
    {
        var name = change.Field ?? change.FieldId ?? "";
        if (Noise.Contains(name) || (change.FieldId is { } noisy && Noise.Contains(noisy)))
        {
            return null;
        }

        var id = change.FieldId ?? catalog.FieldIdsByName.GetValueOrDefault(name) ?? name;
        var (from, to, fromText, toText) = (Blank(change.From), Blank(change.To), Blank(change.FromText), Blank(change.ToText));
        if (ParentFields.Contains(name) || ParentFields.Contains(id) || id == catalog.EpicLinkField)
        {
            return Ref("parent", from, to, fromText, toText);
        }

        if (id == catalog.SprintField)
        {
            return Ref("sprint", LastId(from), LastId(to), fromText, toText);
        }

        if (catalog.StoryPointFields.Contains(id))
        {
            return Raw("points", fromText ?? from, toText ?? to);
        }

        if (catalog.StartDateFields.Contains(id))
        {
            return Raw("start", from ?? fromText, to ?? toText);
        }

        switch (id.ToLowerInvariant())
        {
            case "status":
                return Ref("status", from, to, fromText, toText);
            case "issuetype":
                return Ref("type", from, to, fromText, toText);
            case "summary":
                return Raw("title", fromText, toText);
            case "description":
                // ADR-019: description edits replay as a marker, without the text.
                return new Mapped("description", null, null);
            case "assignee":
                return Ref("assignee", Seen(from, fromText), Seen(to, toText), fromText, toText);
            case "priority":
                return Raw("priority", fromText, toText);
            case "labels":
                // Jira lists labels space-separated; a label can't hold a space.
                return Raw("labels", fromText?.Replace(' ', ','), toText?.Replace(' ', ','));
            case "duedate":
                return Raw("due", from ?? fromText, to ?? toText);
            case "timeoriginalestimate":
                return Raw("estimateSeconds", from, to);
        }

        if (catalog.Exported.TryGetValue(id, out var field))
        {
            return Jira.Kind(field) == FieldKind.User
                ? Ref(id, Seen(from, fromText), Seen(to, toText), fromText, toText)
                : Raw(id, fromText ?? from, toText ?? to);
        }

        // Anything else Jira tracks (resolution, fix versions, components, links, a moved key) stays as it showed it.
        return fromText == toText && from == to ? null : new Mapped(name, from, to, fromText ?? from, toText ?? to);

        static string? Blank(string? text) => string.IsNullOrEmpty(text) ? null : text;

        static Mapped? Raw(string field, string? from, string? to) => from == to ? null : new Mapped(field, from, to);

        static Mapped? Ref(string field, string? from, string? to, string? fromText, string? toText) =>
            from == to && fromText == toText ? null : new Mapped(field, from, to, fromText, toText);

        // A sprint change lists every sprint the issue has been in ("12, 13"); the last is the one it is in now.
        static string? LastId(string? ids) => ids?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    /// <summary>A person the changelog names by ID and display name only, reported if not seen yet.</summary>
    private string? Seen(string? id, string? name)
    {
        if (id is null)
        {
            return null;
        }

        if (!_people.ContainsKey(id))
        {
            _people[id] = false;
            _pending.Add(new SourcePerson { Id = id, Name = name ?? id });
        }

        return id;
    }

    private JsonElement? Value(JiraField field, JsonElement value) => Jira.Kind(field) switch
    {
        FieldKind.Number => Jira.Number(value) is { } number ? FieldValue.Number(number) : null,
        FieldKind.Date => Jira.Day(value.ValueKind == JsonValueKind.String ? value.GetString() : null) is { } day ? FieldValue.Date(day) : null,
        FieldKind.User => PersonId(value) is { } person ? FieldValue.Text(person) : null,
        FieldKind.MultiSelect => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Select(Jira.Display).OfType<string>().ToArray() is { Length: > 0 } options
            ? FieldValue.Options(options)
            : null,
        _ => Jira.Display(value) is { Length: > 0 } text ? FieldValue.Text(text) : null,
    };

    /// <summary>
    /// Description or comment markup, with images and file links that name an attachment pointed at its content URL,
    /// so the import re-hosts them: ADF media get <c>attrs.url</c>, wiki <c>!name!</c> and <c>[^name]</c> the URL.
    /// </summary>
    private static string? Markup(JsonElement? value, IReadOnlyList<JsonElement> attachments)
    {
        if (value is not { } markup || markup.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var files = new Dictionary<string, (string Url, string? Type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in attachments.OrderBy(a => Jira.Time(Jira.Prop(a, "created"))))
        {
            // The newest of two files with the same name is the one the text shows.
            if (Jira.Prop(file, "filename") is { } name && Jira.Prop(file, "content") is { } url)
            {
                files[name] = (url, Jira.Prop(file, "mimeType"));
            }
        }

        if (markup.ValueKind == JsonValueKind.String)
        {
            var wiki = markup.GetString()!;
            return files.Count == 0 ? wiki : WikiAttachments(wiki, files);
        }

        if (markup.ValueKind != JsonValueKind.Object)
        {
            return markup.GetRawText();
        }

        if (files.Count == 0)
        {
            return markup.GetRawText();
        }

        var document = JsonNode.Parse(markup.GetRawText())!;
        AdfAttachments(document, files);
        return document.ToJsonString();
    }

    private static void AdfAttachments(JsonNode? node, Dictionary<string, (string Url, string? Type)> files)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                AdfAttachments(child, files);
            }

            return;
        }

        if (node is not JsonObject element)
        {
            return;
        }

        if (element["type"]?.GetValueKind() == JsonValueKind.String && element["type"]!.GetValue<string>() is "media" or "mediaInline"
            && element["attrs"] is JsonObject attrs && attrs["url"] is null
            && (Str(attrs["alt"]) ?? Str(attrs["fileName"])) is { } name && files.TryGetValue(name, out var file))
        {
            attrs["url"] = file.Url;
            if (attrs["mimeType"] is null && file.Type is not null)
            {
                attrs["mimeType"] = file.Type;
            }
        }

        AdfAttachments(element["content"], files);

        static string? Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }

    private static string WikiAttachments(string wiki, Dictionary<string, (string Url, string? Type)> files) =>
        WikiFile().Replace(wiki, m =>
        {
            if (m.Groups["image"].Success)
            {
                return files.TryGetValue(m.Groups["image"].Value.Trim(), out var image) ? $"!{image.Url}{m.Groups["params"].Value}!" : m.Value;
            }

            var name = m.Groups["file"].Value.Trim();
            return files.TryGetValue(name, out var file) ? $"[{(m.Groups["label"].Success ? m.Groups["label"].Value : name)}|{file.Url}]" : m.Value;
        });

    /// <summary>The person's ID, reporting the person the first time (and again when an email turns up).</summary>
    private string? PersonId(JsonElement? value)
    {
        if (value is not { } element || Jira.Person(element) is not { } person)
        {
            return null;
        }

        var hasEmail = person.Email is not null;
        if (!_people.TryGetValue(person.Id, out var known) || (!known && hasEmail))
        {
            _people[person.Id] = hasEmail;
            _pending.Add(person);
        }

        return person.Id;
    }

    // !name! and !name|thumbnail!, not !http…!; [^name] and [label|^name].
    [GeneratedRegex(@"!(?<image>(?!https?://)[^!\s|][^!|\n]*?)(?<params>\|[^!\n]*)?!|\[(?:(?<label>[^|\]\n]*)\|)?\^(?<file>[^\]\n]+)\]", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex WikiFile();
}

/// <summary>Reading Jira JSON values: fields come as strings, numbers, named objects, user objects or arrays of them.</summary>
internal static partial class Jira
{
    public static JsonElement? Field(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public static JsonElement? Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public static string? Text(Dictionary<string, JsonElement> fields, string name) =>
        Field(fields, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? Prop(JsonElement? element, string name) =>
        element is { } e && Field(e, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static IEnumerable<JsonElement> Array(Dictionary<string, JsonElement> fields, string name) =>
        Field(fields, name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    public static decimal? Number(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.Number when value.Value.TryGetDecimal(out var number) => number,
        JsonValueKind.String when decimal.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    /// <summary>A Jira time: ISO 8601, with the offset as <c>+0100</c> as Jira writes it or <c>+01:00</c>.</summary>
    public static DateTimeOffset? Time(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var normalized = CompactOffset().Replace(text, "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }

    /// <summary>A day: a date field (<c>2025-03-20</c>) as is, a time as the day it is where it was written.</summary>
    public static DateOnly? Day(string? text) =>
        text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day
        : Time(text) is { } time ? DateOnly.FromDateTime(time.DateTime)
        : null;

    /// <summary>
    /// A user: Cloud names people by <c>accountId</c> and often hides the email; Data Center by <c>key</c> (what the
    /// changelog uses) with <c>name</c> as the handle wiki mentions use (<c>[~sofie]</c>). App accounts are bots.
    /// </summary>
    public static SourcePerson? Person(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var account = Prop(value, "accountId");
        var name = Prop(value, "name");
        var id = account ?? Prop(value, "key") ?? name;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new SourcePerson
        {
            Id = id,
            Name = Prop(value, "displayName") ?? name ?? id,
            Email = Prop(value, "emailAddress") is { Length: > 0 } email ? email : null,
            Handle = account is null ? name : null,
            Bot = string.Equals(Prop(value, "accountType"), "app", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>What a field value shows: an option's value, a named thing's name, a user's name, plain text from ADF.</summary>
    public static string? Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object when Prop(value, "type") == "doc" => AdfText(value).Trim(),
        JsonValueKind.Object when Prop(value, "value") is { } option =>
            Field(value, "child") is { } child && Prop(child, "value") is { } sub ? $"{option} / {sub}" : option,
        JsonValueKind.Object => Prop(value, "name") ?? Prop(value, "displayName"),
        _ => null,
    };

    /// <summary>The level a type sits at: Cloud says; elsewhere sub-tasks are -1, Epic 1, Initiative 2, the rest 0.</summary>
    public static int Level(JiraIssueType type) =>
        type.HierarchyLevel ?? (type.Subtask ? -1
            : type.Name.Equals("Epic", StringComparison.OrdinalIgnoreCase) ? 1
            : type.Name.Equals("Initiative", StringComparison.OrdinalIgnoreCase) ? 2
            : 0);

    /// <summary>The format's kind for a custom field; null for kinds the import has no use for.</summary>
    public static FieldKind? Kind(JiraField field) => (field.Schema?.Type, field.Schema?.Items) switch
    {
        ("number", _) => FieldKind.Number,
        ("date" or "datetime", _) => FieldKind.Date,
        ("user", _) => FieldKind.User,
        ("option" or "option-with-child" or "version" or "priority" or "project", _) => FieldKind.Select,
        ("array", "option" or "string" or "version" or "component" or "user" or "group") => FieldKind.MultiSelect,
        ("string", _) => FieldKind.Text,
        _ => null,
    };

    /// <summary>
    /// A sprint from the sprint field: an object on Cloud and recent Data Center, the old
    /// <c>com.atlassian.greenhopper.service.sprint.Sprint@1a2b[id=1,state=CLOSED,name=…,startDate=…]</c> string elsewhere.
    /// </summary>
    public static JiraSprint? Sprint(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.Deserialize(JiraJson.Default.JiraSprint);
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var open = text?.IndexOf('[', StringComparison.Ordinal) ?? -1;
        if (text is null || open < 0 || !text.EndsWith(']'))
        {
            return null;
        }

        var body = text[(open + 1)..^1];
        var keys = SprintKey().Matches(body).ToList();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var start = keys[i].Index + keys[i].Length;
            var end = i + 1 < keys.Count ? keys[i + 1].Index : body.Length;
            var raw = body[start..end];
            if (raw != "<null>" && raw.Length > 0)
            {
                values[keys[i].Groups[1].Value] = raw;
            }
        }

        return values.TryGetValue("id", out var id) && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? new JiraSprint
            {
                Id = number,
                Name = values.GetValueOrDefault("name"),
                State = values.GetValueOrDefault("state"),
                StartDate = values.GetValueOrDefault("startDate"),
                EndDate = values.GetValueOrDefault("endDate"),
                CompleteDate = values.GetValueOrDefault("completeDate"),
            }
            : null;
    }

    private static string AdfText(JsonElement node)
    {
        if (Prop(node, "type") == "text")
        {
            return Prop(node, "text") ?? "";
        }

        var parts = Field(node, "content") is { ValueKind: JsonValueKind.Array } content
            ? content.EnumerateArray().Select(AdfText)
            : [];
        var text = string.Concat(parts);
        return Prop(node, "type") is "paragraph" or "heading" or "listItem" or "codeBlock" ? text + "\n" : text;
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactOffset();

    [GeneratedRegex(@"(?:^|,)(id|rapidViewId|state|name|goal|startDate|endDate|completeDate|activatedDate|sequence|synced|autoStartStop|incompleteIssuesDestinationId)=", RegexOptions.CultureInvariant)]
    private static partial Regex SprintKey();
}
