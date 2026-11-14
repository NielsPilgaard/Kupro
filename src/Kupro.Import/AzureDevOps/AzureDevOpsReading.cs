using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Import.Snapshot;
using static Kupro.Import.AzureDevOps.AzureDevOpsConnector;

namespace Kupro.Import.AzureDevOps;

/// <summary>
/// Turns Azure DevOps work items, links, comments and updates into snapshot records for one read. Keeps the state a
/// read needs across items: people already reported and relations already emitted (both ends of a link carry it).
/// </summary>
internal sealed class AzureDevOpsReading(Catalog catalog, Uri collection)
{
    private const string HierarchyReverse = "System.LinkTypes.Hierarchy-Reverse";
    private const string HierarchyForward = "System.LinkTypes.Hierarchy-Forward";
    private const string DependencyForward = "System.LinkTypes.Dependency-Forward";
    private const string DependencyReverse = "System.LinkTypes.Dependency-Reverse";
    private const string DuplicateForward = "System.LinkTypes.Duplicate-Forward";
    private const string DuplicateReverse = "System.LinkTypes.Duplicate-Reverse";
    private const string AttachedFile = "AttachedFile";
    private const string Hyperlink = "Hyperlink";
    private const string ArtifactLink = "ArtifactLink";
    private const string OriginalEstimate = "Microsoft.VSTS.Scheduling.OriginalEstimate";

    private readonly string _collection = collection.AbsoluteUri.TrimEnd('/');
    private readonly Dictionary<string, bool> _people = new(StringComparer.Ordinal);
    private readonly List<SourcePerson> _pending = [];
    private readonly HashSet<string> _relations = new(StringComparer.Ordinal);

    /// <summary>People first seen since the last call, to stream before the records that refer to them.</summary>
    public List<SourcePerson> TakePeople()
    {
        var people = _pending.ToList();
        _pending.Clear();
        return people;
    }

    public SourceItem Item(ProjectInfo project, AdoWorkItem raw)
    {
        var f = raw.Fields;
        var id = raw.Id.ToString(CultureInfo.InvariantCulture);
        var type = Ado.Text(f, "System.WorkItemType") ?? "Unknown";
        var status = project.Process.StatusId(type, Ado.Text(f, "System.State") ?? "");
        var created = Ado.Time(f, "System.CreatedDate") ?? DateTimeOffset.UnixEpoch;
        var updated = Ado.Time(f, "System.ChangedDate") ?? created;

        string? parent = null;
        var links = new List<SourceLink>();
        foreach (var relation in raw.Relations)
        {
            switch (relation.Rel)
            {
                case HierarchyReverse:
                    parent = Ado.WorkItemId(relation.Url);
                    break;
                case Hyperlink:
                    links.Add(new SourceLink { Url = relation.Url, Title = Ado.Text(relation.Attributes, "comment") });
                    break;
                case ArtifactLink:
                    links.Add(new SourceLink { Url = ArtifactUrl(project, relation.Url), Title = Ado.Text(relation.Attributes, "name") });
                    break;
            }
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (reference, field) in catalog.Fields)
        {
            if (f.TryGetValue(reference, out var value) && Value(field, value) is { } converted)
            {
                fields[field.ReferenceName] = converted;
            }
        }

        var original = Ado.Number(f, OriginalEstimate);
        var spent = Ado.Number(f, "Microsoft.VSTS.Scheduling.RemainingWork") + Ado.Number(f, "Microsoft.VSTS.Scheduling.CompletedWork")
            ?? Ado.Number(f, "Microsoft.VSTS.Scheduling.RemainingWork") ?? Ado.Number(f, "Microsoft.VSTS.Scheduling.CompletedWork");
        var hours = original is > 0 ? original : spent is > 0 ? spent : null;
        var iteration = Ado.Text(f, "System.IterationPath");
        var category = catalog.Categories.GetValueOrDefault(status);

        return new SourceItem
        {
            Id = id,
            Key = id,
            Project = project.Id,
            Type = project.Process.TypeId(type),
            Status = status,
            Title = Ado.Text(f, "System.Title") ?? "",
            Description = Description(f),
            DescriptionMarkup = MarkupKind.Html,
            Parent = parent ?? Ado.Text(f, "System.Parent"),
            Assignee = PersonId(Ado.Field(f, "System.AssignedTo")),
            Creator = PersonId(Ado.Field(f, "System.CreatedBy")),
            Priority = Ado.Text(f, "Microsoft.VSTS.Common.Priority"),
            Labels = Ado.Text(f, "System.Tags")?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [],
            Fields = fields,
            EstimateSeconds = hours is { } h ? (long)Math.Round(h * 3600) : null,
            Points = Ado.Number(f, "Microsoft.VSTS.Scheduling.StoryPoints")
                ?? Ado.Number(f, "Microsoft.VSTS.Scheduling.Effort")
                ?? Ado.Number(f, "Microsoft.VSTS.Scheduling.Size"),
            Start = Ado.Day(Ado.Time(f, "Microsoft.VSTS.Scheduling.StartDate")),
            Due = Ado.Day(Ado.Time(f, "Microsoft.VSTS.Scheduling.TargetDate")
                ?? Ado.Time(f, "Microsoft.VSTS.Scheduling.FinishDate")
                ?? Ado.Time(f, "Microsoft.VSTS.Scheduling.DueDate")),
            Sprint = iteration is not null && project.SprintsByPath.TryGetValue(iteration, out var sprint) ? sprint.Id : null,
            Links = links,
            Url = $"{_collection}/{Uri.EscapeDataString(project.Project.Name)}/_workitems/edit/{id}",
            Created = created,
            Updated = updated,
            Resolved = category is "Completed" or "Removed"
                ? Ado.Time(f, "Microsoft.VSTS.Common.ClosedDate") ?? Ado.Time(f, "Microsoft.VSTS.Common.StateChangeDate") ?? updated
                : null,
        };
    }

    /// <summary>
    /// Relations and attachments. A link sits on both items, so each kind is taken from one side only (successor
    /// links, "duplicate of"), and symmetric ones are emitted once per pair.
    /// </summary>
    public IEnumerable<ISnapshotRecord> Related(AdoWorkItem raw, SourceItem item)
    {
        foreach (var relation in raw.Relations)
        {
            var other = Ado.WorkItemId(relation.Url);
            var name = Ado.Text(relation.Attributes, "name");
            switch (relation.Rel)
            {
                case HierarchyReverse or HierarchyForward or DependencyReverse or DuplicateForward or Hyperlink or ArtifactLink:
                    break;
                case AttachedFile:
                    yield return Attachment(item, relation);
                    break;
                case DependencyForward when other is not null:
                    yield return new SourceRelation { From = item.Id, To = other, Kind = RelationKinds.Blocks, Name = name };
                    break;
                case DuplicateReverse when other is not null:
                    yield return new SourceRelation { From = item.Id, To = other, Kind = RelationKinds.Duplicates, Name = name };
                    break;
                default:
                    var pair = string.CompareOrdinal(item.Id, other) < 0 ? $"{item.Id}|{other}" : $"{other}|{item.Id}";
                    var kind = relation.Rel.Replace("-Forward", "", StringComparison.Ordinal).Replace("-Reverse", "", StringComparison.Ordinal);
                    if (other is not null && _relations.Add($"{pair}|{kind}"))
                    {
                        yield return new SourceRelation { From = item.Id, To = other, Kind = RelationKinds.Relates, Name = name };
                    }

                    break;
            }
        }
    }

    public SourceComment Comment(SourceItem item, AdoComment comment) => new()
    {
        Id = $"{item.Id}-{comment.Id.ToString(CultureInfo.InvariantCulture)}",
        Item = item.Id,
        Author = PersonId(comment.CreatedBy),
        Body = comment.Text ?? "",
        Markup = string.Equals(comment.Format, "markdown", StringComparison.OrdinalIgnoreCase) ? MarkupKind.Markdown : MarkupKind.Html,
        Created = comment.CreatedDate,
        Updated = comment.ModifiedDate > comment.CreatedDate ? comment.ModifiedDate : null,
    };

    /// <summary>
    /// Revisions as changes: one per changed field and update (ID <c>update:field</c>), the parent from hierarchy link
    /// changes, other work item links as source changes. Revision 1 is the creation and bookkeeping fields are skipped.
    /// </summary>
    public IEnumerable<SourceChange> Changes(ProjectInfo project, SourceItem item, IReadOnlyList<AdoUpdate> updates)
    {
        string? type = null;
        foreach (var update in updates)
        {
            var typeChange = update.Fields.GetValueOrDefault("System.WorkItemType");
            var before = type ?? Ado.Text(typeChange?.OldValue) ?? Ado.Text(typeChange?.NewValue);
            var after = Ado.Text(typeChange?.NewValue) ?? before;
            type = after;
            if (update.Rev <= 1)
            {
                continue;
            }

            var at = Ado.Time(update.Fields.GetValueOrDefault("System.ChangedDate")?.NewValue)
                ?? (update.RevisedDate is { Year: < 9999 } revised ? revised : item.Updated);
            var actor = PersonId(update.Fields.GetValueOrDefault("System.ChangedBy")?.NewValue ?? update.RevisedBy);
            var description = false;
            foreach (var (name, value) in update.Fields)
            {
                if (Map(project, before, after, name, value) is not { } mapped)
                {
                    continue;
                }

                if (mapped.Field == "description")
                {
                    // Description, acceptance criteria and repro steps are one Kupro description: one marker per update.
                    if (description)
                    {
                        continue;
                    }

                    description = true;
                }

                yield return new SourceChange
                {
                    Id = $"{update.Id.ToString(CultureInfo.InvariantCulture)}:{name}",
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

            if (update.Relations is not { } relations)
            {
                continue;
            }

            var removedParent = Ado.WorkItemId(relations.Removed.FirstOrDefault(r => r.Rel == HierarchyReverse)?.Url);
            var addedParent = Ado.WorkItemId(relations.Added.FirstOrDefault(r => r.Rel == HierarchyReverse)?.Url);
            if (removedParent is not null || addedParent is not null)
            {
                yield return new SourceChange
                {
                    Id = $"{update.Id.ToString(CultureInfo.InvariantCulture)}:parent",
                    Item = item.Id,
                    Actor = actor,
                    At = at,
                    Field = "parent",
                    From = removedParent,
                    To = addedParent,
                    FromText = removedParent is null ? null : "#" + removedParent,
                    ToText = addedParent is null ? null : "#" + addedParent,
                };
            }

            foreach (var (relation, added) in relations.Removed.Select(r => (r, false)).Concat(relations.Added.Select(r => (r, true))))
            {
                if (relation.Rel is HierarchyReverse or HierarchyForward || Ado.WorkItemId(relation.Url) is not { } other)
                {
                    continue;
                }

                yield return new SourceChange
                {
                    Id = $"{update.Id.ToString(CultureInfo.InvariantCulture)}:{relation.Rel}:{other}{(added ? "+" : "-")}",
                    Item = item.Id,
                    Actor = actor,
                    At = at,
                    Field = Ado.Text(relation.Attributes, "name") ?? relation.Rel,
                    From = added ? null : other,
                    To = added ? other : null,
                    FromText = added ? null : "#" + other,
                    ToText = added ? "#" + other : null,
                };
            }
        }
    }

    private sealed record Mapped(string Field, string? From, string? To, string? FromText = null, string? ToText = null);

    private Mapped? Map(ProjectInfo project, string? before, string? after, string field, AdoFieldUpdate value)
    {
        var (old, @new) = (value.OldValue, value.NewValue);
        var process = project.Process;
        switch (field)
        {
            case "System.State":
                var (fromState, toState) = (Ado.Text(old), Ado.Text(@new));
                return Ref(
                    "status",
                    fromState is not null && before is not null ? process.StatusId(before, fromState) : null,
                    toState is not null && after is not null ? process.StatusId(after, toState) : null,
                    fromState, toState);
            case "System.WorkItemType":
                return Ref("type", Ado.Text(old) is { } o ? process.TypeId(o) : null, Ado.Text(@new) is { } n ? process.TypeId(n) : null, Ado.Text(old), Ado.Text(@new));
            case "System.Title":
                return Raw("title", Ado.Text(old), Ado.Text(@new));
            case "System.AssignedTo":
                return Ref("assignee", PersonId(old), PersonId(@new), Ado.Person(old)?.Name, Ado.Person(@new)?.Name);
            case "System.IterationPath":
                return Ref("sprint", SprintId(project, Ado.Text(old)), SprintId(project, Ado.Text(@new)), Ado.Text(old), Ado.Text(@new));
            case "Microsoft.VSTS.Common.Priority":
                return Raw("priority", Ado.Text(old), Ado.Text(@new));
            case "System.Tags":
                return Raw("labels", Ado.Text(old), Ado.Text(@new));
            case "System.Description" or "Microsoft.VSTS.Common.AcceptanceCriteria" or "Microsoft.VSTS.TCM.ReproSteps":
                // ADR-019: description edits replay as a marker, without the text.
                return new Mapped("description", null, null);
            case OriginalEstimate:
                return Raw("estimateSeconds", Seconds(old), Seconds(@new));
            case "Microsoft.VSTS.Scheduling.StoryPoints" or "Microsoft.VSTS.Scheduling.Effort" or "Microsoft.VSTS.Scheduling.Size":
                return Raw("points", Ado.Text(old), Ado.Text(@new));
            case "Microsoft.VSTS.Scheduling.StartDate":
                return Raw("start", DayText(old), DayText(@new));
            case "Microsoft.VSTS.Scheduling.TargetDate" or "Microsoft.VSTS.Scheduling.FinishDate" or "Microsoft.VSTS.Scheduling.DueDate":
                return Raw("due", DayText(old), DayText(@new));
        }

        if (!catalog.Fields.TryGetValue(field, out var source))
        {
            return null;
        }

        return Ado.Kind(source) switch
        {
            FieldKind.User => Ref(source.ReferenceName, PersonId(old), PersonId(@new), Ado.Person(old)?.Name, Ado.Person(@new)?.Name),
            FieldKind.Date => Raw(source.ReferenceName, DayText(old), DayText(@new)),
            _ => Raw(source.ReferenceName, Ado.Text(old), Ado.Text(@new)),
        };

        static Mapped? Raw(string field, string? from, string? to) =>
            from == to ? null : new Mapped(field, from, to);

        static Mapped? Ref(string field, string? from, string? to, string? fromText, string? toText) =>
            from == to && fromText == toText ? null : new Mapped(field, from, to, fromText, toText);

        static string? Seconds(JsonElement hours) =>
            Ado.Number(hours) is { } h ? ((long)Math.Round(h * 3600)).ToString(CultureInfo.InvariantCulture) : null;

        static string? DayText(JsonElement value) =>
            Ado.Day(Ado.Time(value))?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? SprintId(ProjectInfo project, string? path) =>
        path is not null && project.SprintsByPath.TryGetValue(path, out var sprint) ? sprint.Id : null;

    private JsonElement? Value(AdoField field, JsonElement value) => Ado.Kind(field) switch
    {
        FieldKind.Number => Ado.Number(value) is { } number ? FieldValue.Number(number) : null,
        FieldKind.Date => Ado.Day(Ado.Time(value)) is { } day ? FieldValue.Date(day) : null,
        FieldKind.User => PersonId(value) is { } person ? FieldValue.Text(person) : null,
        _ => Ado.Text(value) is { Length: > 0 } text ? FieldValue.Text(text) : null,
    };

    private static SourceAttachment Attachment(SourceItem item, AdoRelation relation)
    {
        var id = relation.Url.Split('?')[0].Split('/')[^1];
        return new SourceAttachment
        {
            Id = id,
            Item = item.Id,
            FileName = Ado.Text(relation.Attributes, "name") ?? id,
            Size = Ado.Number(relation.Attributes, "resourceSize") is { } size ? (long)size : null,
            Created = Ado.Time(relation.Attributes, "resourceCreatedDate") ?? Ado.Time(relation.Attributes, "authorizedDate") ?? item.Created,
            Url = relation.Url,
        };
    }

    /// <summary>Description, then acceptance criteria and repro steps as their own sections, all still HTML.</summary>
    private static string? Description(Dictionary<string, JsonElement> fields)
    {
        var html = new StringBuilder();
        void Append(string field, string? heading)
        {
            if (Ado.Text(fields, field) is not { } text || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (heading is not null)
            {
                html.Append("<h2>").Append(heading).Append("</h2>");
            }

            html.Append(text);
        }

        Append("System.Description", null);
        Append("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance criteria");
        Append("Microsoft.VSTS.TCM.ReproSteps", "Repro steps");
        return html.Length == 0 ? null : html.ToString();
    }

    /// <summary>Commit, pull request and build links point at <c>vstfs://</c> URIs; turn them into web addresses.</summary>
    private string ArtifactUrl(ProjectInfo project, string uri)
    {
        const string Scheme = "vstfs:///";
        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var parts = uri[Scheme.Length..].Split('/');
        if (parts.Length < 3)
        {
            return uri;
        }

        var id = Uri.UnescapeDataString(parts[2]);
        var ids = id.Split('/');
        return (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), ids.Length) switch
        {
            ("GIT", "COMMIT", 3) => $"{_collection}/{ids[0]}/_git/{ids[1]}/commit/{ids[2]}",
            ("GIT", "PULLREQUESTID", 3) => $"{_collection}/{ids[0]}/_git/{ids[1]}/pullrequest/{ids[2]}",
            ("BUILD", "BUILD", 1) => $"{_collection}/{project.Id}/_build/results?buildId={id}",
            _ => uri,
        };
    }

    /// <summary>The person's ID, reporting the person the first time (and again when an email turns up).</summary>
    private string? PersonId(JsonElement? value)
    {
        if (value is not { } element || Ado.Person(element) is not { } person)
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
}

/// <summary>Reading Azure DevOps JSON values: fields come as strings, numbers or identity objects.</summary>
internal static partial class Ado
{
    public static JsonElement? Field(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value : null;

    public static string? Text(Dictionary<string, JsonElement> fields, string name) => Text(Field(fields, name));

    public static string? Text(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object => Person(value.Value)?.Name,
        _ => null,
    };

    public static decimal? Number(Dictionary<string, JsonElement> fields, string name) =>
        Field(fields, name) is { } value ? Number(value) : null;

    public static decimal? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    public static DateTimeOffset? Time(Dictionary<string, JsonElement> fields, string name) => Time(Field(fields, name));

    public static DateTimeOffset? Time(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text
        && DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    /// <summary>
    /// A date field's day. Azure DevOps stores the picked day as midnight in the user's zone, in UTC: a Danish
    /// 20 March is <c>2025-03-19T23:00Z</c>. Rounding to the nearest UTC midnight gives the day back for zones within ±12 h.
    /// </summary>
    public static DateOnly? Day(DateTimeOffset? time) =>
        time is { } t ? DateOnly.FromDateTime(t.UtcDateTime.AddHours(12)) : null;

    /// <summary>The ID at the end of a work item URL (<c>…/_apis/wit/workItems/123</c>).</summary>
    public static string? WorkItemId(string? url)
    {
        const string Marker = "/workItems/";
        var at = url?.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0)
        {
            return null;
        }

        var id = url![(at + Marker.Length)..].Split('?', '/')[0];
        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>
    /// An identity: an object (<c>id</c>, <c>displayName</c>, <c>uniqueName</c>) on current versions, or
    /// <c>"Name &lt;unique name&gt;"</c> on old ones. The unique name is an email on Services, <c>DOMAIN\user</c> on Server.
    /// </summary>
    public static SourcePerson? Person(JsonElement value)
    {
        string? id, name, unique;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var display = Property(value, "displayName");
            unique = Property(value, "uniqueName");
            id = Property(value, "id") ?? unique ?? display;
            name = display ?? unique ?? id;
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            var match = NameAndUnique().Match(text);
            (name, unique) = match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : (text, null);
            id = unique ?? text;
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(id) || name is null)
        {
            return null;
        }

        var email = unique is not null && unique.Contains('@', StringComparison.Ordinal) && !unique.Contains('\\', StringComparison.Ordinal) ? unique : null;
        return new SourcePerson { Id = id, Name = name, Email = email };
    }

    public static FieldKind Kind(AdoField field) => field.Type switch
    {
        _ when field.IsIdentity || field.Type == "identity" => FieldKind.User,
        "integer" or "double" or "picklistInteger" or "picklistDouble" => FieldKind.Number,
        "dateTime" => FieldKind.Date,
        "treePath" or "picklistString" or "boolean" => FieldKind.Select,
        _ when field.IsPicklist => FieldKind.Select,
        _ => FieldKind.Text,
    };

    private static string? Property(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    [GeneratedRegex(@"^(.*?)\s*<([^<>]+)>$", RegexOptions.CultureInvariant)]
    private static partial Regex NameAndUnique();
}
