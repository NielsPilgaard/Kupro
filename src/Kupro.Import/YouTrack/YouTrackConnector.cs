using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kupro.Import.Snapshot;

namespace Kupro.Import.YouTrack;

/// <summary>
/// YouTrack Cloud and self-hosted (task 027, ADR-019): projects with their short names as keys, issues with their
/// per-project custom fields (Type, State, Priority, Assignee, Estimation, Due Date, the rest as fields), subtask
/// parents, links, tags, comments, attachments, agile sprints, and activities as changes. This class only reads.
/// </summary>
public sealed class YouTrackConnector : ISourceConnector
{
    /// <summary>Item type of an issue in a project without a Type field.</summary>
    public const string PlainIssueType = "Issue";

    /// <summary>Hours spent, for the details block only (time tracking is out of v1 scope).</summary>
    public const string SpentTimeField = "spentTime";

    private const string TypeField = "Type";
    private const string StateField = "State";
    private const string PriorityField = "Priority";
    private const string AssigneeField = "Assignee";
    private const string EstimationField = "Estimation";
    private const string SpentTimeName = "Spent time";
    private const string DueDateField = "Due Date";

    private const string UserFields = "id,login,fullName,email";
    private const string ValueFields = "id,name,login,fullName,email,minutes,text,isResolved";

    private const string IssueFields =
        "id,idReadable,summary,description,created,updated,resolved,reporter(" + UserFields + "),project(id),"
        + "customFields(name,value(" + ValueFields + ")),tags(name),"
        + "links(direction,linkType(name,directed,aggregation,sourceToTarget,targetToSource),issues(id)),"
        + "comments(id,text,author(" + UserFields + "),created,updated,deleted),"
        + "attachments(id,name,mimeType,size,url,author(" + UserFields + "),created,removed)";

    private const string ActivityFields =
        "activities(id,timestamp,author(" + UserFields + "),field(presentation),category(id),added(" + ValueFields + "),removed(" + ValueFields + ")),hasAfter,afterCursor";

    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        TypeField, StateField, PriorityField, AssigneeField, EstimationField, SpentTimeName, DueDateField,
    };

    private readonly YouTrackClient _client;
    private readonly TimeProvider _time;
    private Catalog? _catalog;

    /// <param name="url">The address people open: <c>https://acme.youtrack.cloud</c> or <c>https://yt.acme.example/youtrack</c>.</param>
    /// <param name="token">A permanent token (Profile → Account Security).</param>
    public YouTrackConnector(HttpClient http, string url, string token, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _client = new YouTrackClient(http, Site(url), token, _time);
    }

    internal YouTrackClient Client => _client;

    /// <summary>The site without a trailing <c>/api</c> or slash.</summary>
    internal static Uri Site(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var given) || given.Scheme is not ("https" or "http"))
        {
            throw new SourceAccessException($"'{url}' is not a YouTrack address, like https://acme.youtrack.cloud.");
        }

        var path = given.AbsolutePath.TrimEnd('/');
        path = path.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? path[..^"/api".Length] : path;
        return new Uri($"{given.Scheme}://{given.Authority}{path}");
    }

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        // The next delta starts when this read did: anything changed while it ran is read again, nothing is missed.
        var started = _time.GetUtcNow();
        var catalog = await CatalogAsync(ct);
        DateTimeOffset? since = cursor is null
            ? null
            : DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{cursor}' is not a YouTrack cursor (a UTC time).", nameof(cursor));
        var projects = scope.Projects.Count == 0
            ? catalog.Projects.Where(p => !p.Archived).ToList()
            : [.. scope.Projects.Select(id => catalog.Projects.FirstOrDefault(p => p.Id == id)
                ?? throw new SourceAccessException($"YouTrack has no project {id} that this token can see.", HttpStatusCode.NotFound))];
        var inScope = projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var people = new HashSet<string>(StringComparer.Ordinal);
        var sprintOf = new Dictionary<string, (string Id, long Start)>(StringComparer.Ordinal);
        await foreach (var agile in _client.PagedAsync($"agiles?fields=id,projects(id),sprints(id,name,start,finish,archived)", YouTrackJson.Default.ListYtAgile, ct, missingIsEmpty: true))
        {
            if (!agile.Projects.Any(p => inScope.Contains(p.Id)))
            {
                continue;
            }

            foreach (var listed in agile.Sprints)
            {
                var sprint = await _client.GetAsync($"agiles/{agile.Id}/sprints/{listed.Id}?fields=id,name,start,finish,archived,issues(id)", YouTrackJson.Default.YtSprint, ct);
                var finish = Day(sprint.Finish);
                yield return new SourceSprint
                {
                    Id = sprint.Id,
                    Name = sprint.Name,
                    Start = Day(sprint.Start),
                    End = finish,
                    Closed = sprint.Archived || (finish is { } end && end < DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)),
                };
                foreach (var issue in sprint.Issues)
                {
                    // An issue carried over sits in several sprints: the latest one is where it is now.
                    if (!sprintOf.TryGetValue(issue.Id, out var known) || known.Start <= (sprint.Start ?? 0))
                    {
                        sprintOf[issue.Id] = (sprint.Id, sprint.Start ?? 0);
                    }
                }
            }
        }

        foreach (var project in projects)
        {
            // Days, not times: the query reads dates in the token user's time zone, so a day early and then filtered here.
            var query = $"project: {{{project.ShortName}}}";
            if (since is { } from)
            {
                query += $" updated: {from.UtcDateTime.AddDays(-1):yyyy-MM-dd} .. Today";
            }

            query += " sort by: created asc";
            await foreach (var issue in _client.PagedAsync($"issues?query={Uri.EscapeDataString(query)}&fields={IssueFields}", YouTrackJson.Default.ListYtIssue, ct))
            {
                var updated = Time(issue.Updated);
                if (since is not null && updated < since)
                {
                    continue;
                }

                var resolved = issue.Resolved is { } r ? Time(r) : (DateTimeOffset?)null;
                if (scope.ResolvedAfter is { } after && resolved < after)
                {
                    continue;
                }

                var users = new List<YtUser?> { issue.Reporter };
                users.AddRange(issue.CustomFields.Select(f => User(f.Value)));
                users.AddRange(issue.Comments.Select(c => c.Author));
                users.AddRange(issue.Attachments.Select(a => a.Author));
                var activities = await ActivitiesAsync(issue.Id, since, ct);
                users.AddRange(activities.Select(a => a.Author));
                foreach (var user in users.OfType<YtUser>().Where(u => people.Add(u.Id)))
                {
                    yield return Person(user);
                }

                var files = issue.Attachments.Where(a => !a.Removed && a.Url is not null).ToList();
                yield return Item(catalog, issue, sprintOf.TryGetValue(issue.Id, out var sprint) ? sprint.Id : null, files);
                foreach (var file in files)
                {
                    yield return new SourceAttachment
                    {
                        Id = file.Id,
                        Item = issue.Id,
                        FileName = file.Name,
                        ContentType = file.MimeType,
                        Size = file.Size,
                        Author = file.Author?.Id,
                        Created = Time(file.Created),
                        Url = Absolute(file.Url!),
                    };
                }

                foreach (var relation in Relations(issue))
                {
                    yield return relation;
                }

                foreach (var comment in issue.Comments.Where(c => !c.Deleted && !string.IsNullOrWhiteSpace(c.Text)))
                {
                    yield return new SourceComment
                    {
                        Id = comment.Id,
                        Item = issue.Id,
                        Author = comment.Author?.Id,
                        Body = PointAtFiles(comment.Text!, files),
                        Markup = MarkupKind.Markdown,
                        Created = Time(comment.Created),
                        Updated = comment.Updated is { } u && u > comment.Created ? Time(u) : null,
                    };
                }

                foreach (var change in Changes(issue.Id, activities))
                {
                    yield return change;
                }
            }
        }

        yield return new ReadCursor(started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        attachment.Url is { } url
            ? _client.DownloadAsync(url, ct)
            : throw new SourceAccessException($"Attachment {attachment.Id} ({attachment.FileName}) has no URL.");

    private SourceItem Item(Catalog catalog, YtIssue issue, string? sprint, IReadOnlyList<YtAttachment> files)
    {
        YtIssueCustomField? Field(string name) => issue.CustomFields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in issue.CustomFields.Where(f => !Handled.Contains(f.Name)))
        {
            if (catalog.Fields.TryGetValue(field.Name, out var kind) && Value(field.Value, kind) is { } value)
            {
                fields[field.Name] = value;
            }
        }

        if (Minutes(Field(SpentTimeName)?.Value) is { } spent and > 0)
        {
            fields[SpentTimeField] = FieldValue.Number(Math.Round(spent / 60m, 2));
        }

        return new SourceItem
        {
            Id = issue.Id,
            Key = issue.IdReadable,
            Project = issue.Project.Id,
            Type = Name(Field(TypeField)?.Value) ?? PlainIssueType,
            Status = Name(Field(StateField)?.Value) ?? "",
            Title = issue.Summary,
            Description = string.IsNullOrWhiteSpace(issue.Description) ? null : PointAtFiles(issue.Description, files),
            DescriptionMarkup = MarkupKind.Markdown,
            Parent = issue.Links.Where(l => l.LinkType.Aggregation && l.Direction == "INWARD").SelectMany(l => l.Issues).FirstOrDefault()?.Id,
            Assignee = User(Field(AssigneeField)?.Value)?.Id,
            Creator = issue.Reporter?.Id,
            Priority = Name(Field(PriorityField)?.Value),
            Labels = [.. issue.Tags.Select(t => t.Name)],
            Fields = fields,
            EstimateSeconds = Minutes(Field(EstimationField)?.Value) * 60,
            Due = Day(Millis(Field(DueDateField)?.Value)),
            Sprint = sprint,
            Url = $"{_client.Site.AbsoluteUri.TrimEnd('/')}/issue/{issue.IdReadable}",
            Created = Time(issue.Created),
            Updated = Time(issue.Updated),
            Resolved = issue.Resolved is { } resolved ? Time(resolved) : null,
        };
    }

    /// <summary>
    /// Links as relations, each once: a directed link from its outward side, which YouTrack names "is required for"
    /// (X blocks Y) and "is duplicated by" (Y duplicates X), an undirected one from the side with the smaller ID. Subtask links are the hierarchy, not relations.
    /// </summary>
    internal static IEnumerable<SourceRelation> Relations(YtIssue issue)
    {
        foreach (var link in issue.Links.Where(l => !l.LinkType.Aggregation))
        {
            var name = link.LinkType.Name ?? "";
            foreach (var other in link.Issues)
            {
                if (link.Direction == "BOTH" || !link.LinkType.Directed)
                {
                    if (string.CompareOrdinal(issue.Id, other.Id) < 0)
                    {
                        yield return new SourceRelation { From = issue.Id, To = other.Id, Kind = RelationKinds.Relates, Name = link.LinkType.SourceToTarget ?? name };
                    }
                }
                else if (link.Direction == "OUTWARD")
                {
                    var label = link.LinkType.SourceToTarget ?? name;
                    yield return name.StartsWith("Depend", StringComparison.OrdinalIgnoreCase)
                        ? new SourceRelation { From = issue.Id, To = other.Id, Kind = RelationKinds.Blocks, Name = label }
                        : name.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase)
                            ? new SourceRelation { From = other.Id, To = issue.Id, Kind = RelationKinds.Duplicates, Name = label }
                            : new SourceRelation { From = issue.Id, To = other.Id, Kind = RelationKinds.Relates, Name = label };
                }
            }
        }
    }

    private async Task<List<YtActivity>> ActivitiesAsync(string issue, DateTimeOffset? since, CancellationToken ct)
    {
        var activities = new List<YtActivity>();
        string? after = null;
        do
        {
            // A delta starts the history at the cursor (start, in ms), so an old issue's whole history isn't read again.
            var path = $"issues/{issue}/activitiesPage?categories=CustomFieldCategory,SummaryCategory&fields={ActivityFields}"
                + (since is { } from ? $"&start={from.ToUnixTimeMilliseconds()}" : "")
                + (after is null ? "" : $"&cursor={Uri.EscapeDataString(after)}");
            var page = await _client.GetAsync(path, YouTrackJson.Default.YtActivityPage, ct);
            activities.AddRange(page.Activities.Where(a => since is null || Time(a.Timestamp) >= since));
            after = page.HasAfter && page.Activities.Count > 0 ? page.AfterCursor : null;
        }
        while (after is not null);

        return activities;
    }

    /// <summary>Activities as changes: the item properties Kupro replays, and every other custom field by its name.</summary>
    internal static IEnumerable<SourceChange> Changes(string item, IEnumerable<YtActivity> activities)
    {
        foreach (var a in activities)
        {
            SourceChange Change(string field, string? from, string? to, string? fromText = null, string? toText = null) => new()
            {
                Id = a.Id,
                Item = item,
                Actor = a.Author?.Id,
                At = Time(a.Timestamp),
                Field = field,
                From = from,
                To = to,
                FromText = fromText,
                ToText = toText,
            };

            if (a.Category?.Id == "SummaryCategory")
            {
                yield return Change("title", Text(a.Removed), Text(a.Added));
                continue;
            }

            switch (a.Field?.Presentation)
            {
                case null:
                    break;
                case StateField:
                    yield return Change("status", Name(a.Removed), Name(a.Added));
                    break;
                case TypeField:
                    yield return Change("type", Name(a.Removed), Name(a.Added));
                    break;
                case PriorityField:
                    yield return Change("priority", Name(a.Removed), Name(a.Added));
                    break;
                case AssigneeField:
                    yield return Change("assignee", User(a.Removed)?.Id, User(a.Added)?.Id, User(a.Removed)?.Login, User(a.Added)?.Login);
                    break;
                case EstimationField:
                    yield return Change("estimateSeconds", Seconds(a.Removed), Seconds(a.Added));
                    break;
                case DueDateField:
                    yield return Change("due", DayText(a.Removed), DayText(a.Added));
                    break;
                case var other:
                    yield return Change(other, null, null, Text(a.Removed), Text(a.Added));
                    break;
            }
        }
    }

    /// <summary>YouTrack Markdown shows an attachment by its file name; point it at the file's URL so the import re-hosts it.</summary>
    private string PointAtFiles(string text, IReadOnlyList<YtAttachment> files)
    {
        foreach (var file in files)
        {
            var url = Absolute(file.Url!);
            text = text.Replace($"]({file.Name})", $"]({url})", StringComparison.Ordinal)
                .Replace($"]({Uri.EscapeDataString(file.Name)})", $"]({url})", StringComparison.Ordinal);
        }

        return text;
    }

    private string Absolute(string url) => new Uri(_client.Site, url).AbsoluteUri;

    // ---------------------------------------------------------------- values

    private static JsonElement? Single(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(e => (JsonElement?)e).FirstOrDefault() : value;

    private static string? Name(JsonElement? value) =>
        Single(value) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;

    private static YtUser? User(JsonElement? value) =>
        Single(value) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String
            && o.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? new YtUser
            {
                Id = id.GetString()!,
                Login = login.GetString(),
                FullName = o.TryGetProperty("fullName", out var full) && full.ValueKind == JsonValueKind.String ? full.GetString() : null,
                Email = o.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() : null,
            }
            : null;

    private static long? Minutes(JsonElement? value) => Single(value) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetInt64(),
        { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number => m.GetInt64(),
        _ => null,
    };

    private static long? Millis(JsonElement? value) => Single(value) is { ValueKind: JsonValueKind.Number } n ? n.GetInt64() : null;

    private static string? Seconds(JsonElement? value) => Minutes(value) is { } minutes ? (minutes * 60).ToString(CultureInfo.InvariantCulture) : null;

    private static string? DayText(JsonElement? value) => Day(Millis(value))?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>What a value shows: a name, a login, a text, a number, or a list of them.</summary>
    private static string? Text(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.Array } list => list.GetArrayLength() == 0 ? null : string.Join(", ", list.EnumerateArray().Select(e => Text(e)).OfType<string>()),
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        { ValueKind: JsonValueKind.Object } o => Name(o) ?? User(o)?.Login ?? (o.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
            : Minutes(o) is { } m ? m.ToString(CultureInfo.InvariantCulture) : null),
        _ => null,
    };

    /// <summary>A custom field's value in the snapshot's terms for its kind (docs/import-format.md, Fields).</summary>
    private static JsonElement? Value(JsonElement? value, FieldKind kind)
    {
        switch (kind)
        {
            case FieldKind.MultiSelect when value is { ValueKind: JsonValueKind.Array } list:
                var names = list.EnumerateArray().Select(e => Name(e) ?? User(e)?.Login).OfType<string>().ToArray();
                return names.Length == 0 ? null : FieldValue.Options(names);
            case FieldKind.Select:
                return Name(value) is { } name ? FieldValue.Text(name) : null;
            case FieldKind.User:
                return User(value)?.Id is { } user ? FieldValue.Text(user) : null;
            case FieldKind.Date:
                return Day(Millis(value)) is { } day ? FieldValue.Date(day) : null;
            case FieldKind.Number:
                return Single(value) switch
                {
                    { ValueKind: JsonValueKind.Number } n => FieldValue.Number(n.GetDecimal()),
                    { ValueKind: JsonValueKind.Object } when Minutes(value) is { } minutes => FieldValue.Number(Math.Round(minutes / 60m, 2)),
                    _ => null,
                };
            case FieldKind.Text:
                return Text(value) is { Length: > 0 } text ? FieldValue.Text(text) : null;
            default:
                return null;
        }
    }

    private static DateTimeOffset Time(long millis) => DateTimeOffset.FromUnixTimeMilliseconds(millis);

    private static DateOnly? Day(long? millis) => millis is { } ms ? DateOnly.FromDateTime(Time(ms).UtcDateTime) : null;

    private static SourcePerson Person(YtUser user) => new()
    {
        Id = user.Id,
        Name = string.IsNullOrWhiteSpace(user.FullName) ? user.Login ?? user.Id : user.FullName,
        Email = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
        Handle = user.Login,
    };

    /// <summary>The field kind for a YouTrack field type: <c>enum[1]</c>, <c>user[*]</c>, <c>date and time</c>...</summary>
    private static FieldKind? Kind(YtFieldType? type) => type?.Id.Split('[')[0] switch
    {
        "enum" or "state" or "version" or "build" or "ownedField" => type.IsMultiValue ? FieldKind.MultiSelect : FieldKind.Select,
        "user" or "group" => type.IsMultiValue ? FieldKind.MultiSelect : FieldKind.User,
        "integer" or "float" or "period" => FieldKind.Number,
        "date" or "date and time" => FieldKind.Date,
        "string" or "text" => FieldKind.Text,
        _ => null,
    };

    private async Task<Catalog> CatalogAsync(CancellationToken ct)
    {
        if (_catalog is not null)
        {
            return _catalog;
        }

        var projects = new List<YtProject>();
        await foreach (var project in _client.PagedAsync("admin/projects?fields=id,name,shortName,archived", YouTrackJson.Default.ListYtProject, ct))
        {
            projects.Add(project);
        }

        var statuses = new Dictionary<string, SourceStatus>(StringComparer.Ordinal);
        var types = new Dictionary<string, SourceItemType>(StringComparer.Ordinal);
        var fields = new Dictionary<string, SourceField>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var hasType = false;
            await foreach (var field in _client.PagedAsync($"admin/projects/{project.Id}/customFields?fields=field(name,fieldType(id,isMultiValue)),bundle(values(name,isResolved))",
                               YouTrackJson.Default.ListYtProjectCustomField, ct))
            {
                var values = field.Bundle?.Values ?? [];
                if (field.Field.FieldType?.Id.StartsWith("state", StringComparison.Ordinal) == true)
                {
                    foreach (var state in values)
                    {
                        // The resolved flag is what YouTrack itself means by done; which resolved states are Canceled is the mapping's.
                        statuses.TryAdd(state.Name, new SourceStatus { Id = state.Name, Name = state.Name, Category = state.IsResolved == true ? "done" : null });
                    }
                }
                else if (string.Equals(field.Field.Name, TypeField, StringComparison.OrdinalIgnoreCase))
                {
                    hasType = true;
                    foreach (var type in values)
                    {
                        types.TryAdd(type.Name, new SourceItemType { Id = type.Name, Name = type.Name });
                    }
                }
                else if (!Handled.Contains(field.Field.Name) && Kind(field.Field.FieldType) is { } kind && !fields.ContainsKey(field.Field.Name))
                {
                    fields[field.Field.Name] = new SourceField { Id = field.Field.Name, Name = field.Field.Name, Kind = kind, Options = [.. values.Select(v => v.Name)] };
                }
            }

            if (!hasType)
            {
                types.TryAdd(PlainIssueType, new SourceItemType { Id = PlainIssueType, Name = PlainIssueType });
            }
        }

        var people = new List<SourcePerson>();
        await foreach (var user in _client.PagedAsync($"users?fields={UserFields}", YouTrackJson.Default.ListYtUser, ct, missingIsEmpty: true))
        {
            people.Add(Person(user));
        }

        _catalog = new Catalog(projects, fields.Values.ToDictionary(f => f.Id, f => f.Kind, StringComparer.Ordinal), new SourceDescription
        {
            Source = new SnapshotSource { Kind = SourceKinds.YouTrack, BaseUrl = _client.Site.AbsoluteUri.TrimEnd('/') },
            Projects = [.. projects.Select(p => new SourceProject { Id = p.Id, Key = p.ShortName, Name = p.Name, Url = $"{_client.Site.AbsoluteUri.TrimEnd('/')}/projects/{p.ShortName}" })],
            People = people,
            ItemTypes = [.. types.Values],
            Statuses = [.. statuses.Values],
            Fields = [.. fields.Values, new SourceField { Id = SpentTimeField, Name = "Spent time (hours)", Kind = FieldKind.Number }],
        });
        return _catalog;
    }

    private sealed record Catalog(List<YtProject> Projects, Dictionary<string, FieldKind> Fields, SourceDescription Description);
}
