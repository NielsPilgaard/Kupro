using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Import.Snapshot;
using static Kupro.Import.Linear.LinearConnector;

namespace Kupro.Import.Linear;

/// <summary>
/// Turns Linear projects, issues, history and comments into snapshot records for one read. Keeps the state a read needs
/// across them: people already reported, cycle and project names for change texts, issues left out (so their comments
/// are too) and uploads already reported per issue.
/// </summary>
internal sealed partial class LinearReading(Catalog catalog)
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<SourcePerson> _pending = [];
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _projects = new(StringComparer.Ordinal);

    /// <summary>People first referred to since the last call, to stream before the records that refer to them.</summary>
    public List<SourcePerson> TakePeople()
    {
        var people = _pending.ToList();
        _pending.Clear();
        return people;
    }

    /// <summary>An issue left out of the read (trashed, or resolved before the cutoff): its comments are left out too.</summary>
    public void Skip(string issue) => _skipped.Add(issue);

    public bool Skipped(string issue) => _skipped.Contains(issue);

    public void Learn(LinearProject project) => _projects[project.Id] = project.Name;

    public SourceSprint Sprint(LinearTeam team, LinearCycle cycle)
    {
        var name = string.IsNullOrWhiteSpace(cycle.Name) ? $"Cycle {cycle.Number}" : cycle.Name;
        _cycles[cycle.Id] = name;
        return new SourceSprint
        {
            Id = cycle.Id,
            Name = name,
            Project = team.Id,
            Start = Linear.Day(cycle.StartsAt),
            End = Linear.Day(cycle.EndsAt),
            Closed = cycle.CompletedAt is not null,
        };
    }

    /// <summary>A Linear project as an item, under <paramref name="team"/>: the lead is the assignee, the target date the due date.</summary>
    public SourceItem Project(LinearProject project, string team)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var initiatives = project.Initiatives.Nodes.Select(i => i.Name).ToArray();
        if (initiatives.Length > 0)
        {
            fields[InitiativesField] = FieldValue.Options(initiatives);
        }

        // The summary is a one-liner over the body; both are kept.
        var description = string.Join("\n\n", new[] { project.Description, project.Content }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return new SourceItem
        {
            Id = project.Id,
            Project = team,
            Type = ProjectType,
            Status = project.Status?.Id ?? "",
            Title = project.Name,
            Description = description.Length == 0 ? null : description,
            DescriptionMarkup = MarkupKind.Markdown,
            Assignee = PersonId(project.Lead?.Id),
            Creator = PersonId(project.Creator?.Id),
            Priority = Linear.Priority(project.Priority),
            Labels = [.. project.Labels.Nodes.Select(l => l.Name)],
            Fields = fields,
            Start = project.StartDate,
            Due = project.TargetDate,
            Url = project.Url,
            Created = project.CreatedAt,
            Updated = project.UpdatedAt,
            Resolved = project.CompletedAt ?? project.CanceledAt,
        };
    }

    /// <summary>A project's milestones, as versions of the team the project is read under.</summary>
    public static IEnumerable<SourceVersion> Milestones(LinearProject project, string team) =>
        project.ProjectMilestones.Nodes.Select(m => new SourceVersion
        {
            Id = m.Id,
            Project = team,
            Name = m.Name,
            Date = m.TargetDate,
            Released = m.Status == "done",
        });

    public SourceItem Issue(LinearTeam team, LinearIssue issue)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (issue.Project is { } project)
        {
            fields[ProjectField] = FieldValue.Text(project.Id);
        }

        return new SourceItem
        {
            Id = issue.Id,
            Key = issue.Identifier,
            Project = issue.Team?.Id ?? team.Id,
            Type = IssueType,
            Status = issue.State?.Id ?? "",
            Title = issue.Title,
            Description = string.IsNullOrEmpty(issue.Description) ? null : issue.Description,
            DescriptionMarkup = MarkupKind.Markdown,

            // A sub-issue's parent is its issue; a top-level issue's is its project, where an Epic would be.
            Parent = issue.Parent?.Id ?? issue.Project?.Id,
            Assignee = PersonId(issue.Assignee?.Id),
            Creator = PersonId(issue.Creator?.Id),
            Priority = Linear.Priority(issue.Priority),
            Labels = [.. issue.LabelIds.Select(id => catalog.Labels.GetValueOrDefault(id)).OfType<string>()],
            Fields = fields,
            Points = issue.Estimate,
            Due = issue.DueDate,
            Sprint = issue.Cycle?.Id,
            Versions = issue.ProjectMilestone is { } milestone ? [milestone.Id] : [],
            Url = issue.Url,
            Created = issue.CreatedAt,
            Updated = issue.UpdatedAt,
            Resolved = issue.CompletedAt ?? issue.CanceledAt,
        };
    }

    /// <summary>Attachments that aren't files on Linear's upload host: pull requests, Slack threads, web pages.</summary>
    public static IReadOnlyList<SourceLink> Links(List<LinearAttachment> attachments) =>
    [
        .. attachments
            .Where(a => !(Uri.TryCreate(a.Url, UriKind.Absolute, out var url) && LinearClient.IsUpload(url)))
            .Select(a => new SourceLink { Url = a.Url, Title = string.IsNullOrWhiteSpace(a.Title) ? null : a.Title }),
    ];

    /// <summary>
    /// Relations from this issue (each relation is on its source issue only, so it is read once) and uploaded files:
    /// attachments on Linear's upload host and files pasted into the description.
    /// </summary>
    public IEnumerable<ISnapshotRecord> Related(LinearIssue issue, SourceItem item, List<LinearRelation> relations, List<LinearAttachment> attachments)
    {
        foreach (var relation in relations)
        {
            // "similar" is Linear's suggestion, not something a person linked.
            var kind = relation.Type switch
            {
                "blocks" => RelationKinds.Blocks,
                "related" => RelationKinds.Relates,
                "duplicate" => RelationKinds.Duplicates,
                _ => null,
            };
            if (kind is not null && relation.RelatedIssue is { } other)
            {
                yield return new SourceRelation { From = issue.Id, To = other.Id, Kind = kind, Name = relation.Type };
            }
        }

        foreach (var attachment in attachments)
        {
            if (Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url) && LinearClient.IsUpload(url) && _uploads.Add(UploadKey(item.Id, url)))
            {
                yield return new SourceAttachment
                {
                    Id = attachment.Id,
                    Item = item.Id,
                    FileName = string.IsNullOrWhiteSpace(attachment.Title) ? Linear.FileName(url) : attachment.Title,
                    Author = PersonId(attachment.Creator?.Id),
                    Created = attachment.CreatedAt,
                    Url = attachment.Url,
                };
            }
        }

        foreach (var upload in Uploads(item.Id, issue.Description, item.Creator, item.Created))
        {
            yield return upload;
        }
    }

    /// <summary>
    /// The issue's history as changes, oldest first. One history entry can change several things (a state and an
    /// assignee in one edit), so a change's ID is the entry's ID plus the field, plus the label for label changes.
    /// </summary>
    public IEnumerable<SourceChange> Changes(SourceItem item, List<LinearHistory> history)
    {
        foreach (var entry in history.OrderBy(h => h.CreatedAt).ThenBy(h => h.Id, StringComparer.Ordinal))
        {
            var actor = PersonId(entry.ActorId);
            SourceChange Change(string field, string? from, string? to, string? fromText = null, string? toText = null, string? suffix = null) => new()
            {
                Id = $"{entry.Id}:{field}{suffix}",
                Item = item.Id,
                Actor = actor,
                At = entry.CreatedAt,
                Field = field,
                From = from,
                To = to,
                FromText = fromText,
                ToText = toText,
            };

            if (entry.FromStateId != entry.ToStateId)
            {
                yield return Change("status", entry.FromStateId, entry.ToStateId, StateName(entry.FromStateId), StateName(entry.ToStateId));
            }

            if (entry.FromAssigneeId != entry.ToAssigneeId)
            {
                yield return Change("assignee", PersonId(entry.FromAssigneeId) ?? entry.FromAssigneeId, PersonId(entry.ToAssigneeId) ?? entry.ToAssigneeId,
                    UserName(entry.FromAssigneeId), UserName(entry.ToAssigneeId));
            }

            if (entry.FromPriority != entry.ToPriority)
            {
                yield return Change("priority", Priority(entry.FromPriority), Priority(entry.ToPriority));
            }

            if (entry.FromEstimate != entry.ToEstimate)
            {
                yield return Change("points", Linear.Number(entry.FromEstimate), Linear.Number(entry.ToEstimate));
            }

            if (entry.FromCycleId != entry.ToCycleId)
            {
                yield return Change("sprint", entry.FromCycleId, entry.ToCycleId, Name(_cycles, entry.FromCycleId), Name(_cycles, entry.ToCycleId));
            }

            if (entry.FromProjectId != entry.ToProjectId)
            {
                yield return Change(ProjectField, entry.FromProjectId, entry.ToProjectId, Name(_projects, entry.FromProjectId), Name(_projects, entry.ToProjectId));
            }

            if (entry.FromParentId != entry.ToParentId)
            {
                yield return Change("parent", entry.FromParentId, entry.ToParentId);
            }

            if (entry.FromTeamId != entry.ToTeamId)
            {
                // A move to another team: the item's project, and a new key there.
                yield return Change("project", entry.FromTeamId, entry.ToTeamId, TeamName(entry.FromTeamId), TeamName(entry.ToTeamId));
            }

            if (entry.FromTitle != entry.ToTitle && (entry.FromTitle is not null || entry.ToTitle is not null))
            {
                yield return Change("title", entry.FromTitle, entry.ToTitle);
            }

            if (entry.FromDueDate != entry.ToDueDate)
            {
                yield return Change("due", Linear.Text(entry.FromDueDate), Linear.Text(entry.ToDueDate));
            }

            if (entry.UpdatedDescription is true)
            {
                // ADR-019: description edits replay as a marker, without the text.
                yield return Change("description", null, null);
            }

            foreach (var label in entry.AddedLabelIds ?? [])
            {
                if (catalog.Labels.TryGetValue(label, out var name))
                {
                    yield return Change("labels", null, name, suffix: ":+" + label);
                }
            }

            foreach (var label in entry.RemovedLabelIds ?? [])
            {
                if (catalog.Labels.TryGetValue(label, out var name))
                {
                    yield return Change("labels", name, null, suffix: ":-" + label);
                }
            }
        }

        static string? Priority(double? value) => value is { } p ? Linear.Priority(p) : null;
    }

    /// <summary>
    /// A comment, replies included: Linear threads one level deep, and Kupro's comments are flat, so a reply is a
    /// comment in time order like any other. Files pasted into it are attachments of its issue.
    /// </summary>
    public IEnumerable<ISnapshotRecord> Comment(LinearComment comment)
    {
        var author = PersonId(comment.User?.Id);
        yield return new SourceComment
        {
            Id = comment.Id,
            Item = comment.IssueId!,
            Author = author,
            Body = comment.Body,
            Markup = MarkupKind.Markdown,
            Created = comment.CreatedAt,
            Updated = comment.EditedAt,
        };

        foreach (var upload in Uploads(comment.IssueId!, comment.Body, author, comment.CreatedAt))
        {
            yield return upload;
        }
    }

    /// <summary>
    /// Files on Linear's upload host that Markdown links or shows inline. Linear has no file record for them, so the ID
    /// is the issue and the upload's path (the same file pasted into two issues is two attachments), and the name is the
    /// link text or the path's last segment.
    /// </summary>
    private IEnumerable<SourceAttachment> Uploads(string item, string? markdown, string? author, DateTimeOffset created)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            yield break;
        }

        foreach (Match match in UploadLink().Matches(markdown))
        {
            if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var url) || !_uploads.Add(UploadKey(item, url)))
            {
                continue;
            }

            var label = match.Groups["label"].Value;
            yield return new SourceAttachment
            {
                Id = $"{item}:{url.AbsolutePath.TrimStart('/')}",
                Item = item,
                FileName = string.IsNullOrWhiteSpace(label) ? Linear.FileName(url) : label,
                Author = author,
                Created = created,
                Url = url.AbsoluteUri,
            };
        }
    }

    private static string UploadKey(string item, Uri url) => $"{item} {url.AbsolutePath}";

    private string? StateName(string? id) => id is null ? null : catalog.States.GetValueOrDefault(id)?.Name;

    private string? UserName(string? id) => id is null ? null : catalog.Users.GetValueOrDefault(id)?.Name;

    private string? TeamName(string? id) => id is null ? null : catalog.TeamsById.GetValueOrDefault(id)?.Name;

    private static string? Name(Dictionary<string, string> names, string? id) => id is null ? null : names.GetValueOrDefault(id);

    /// <summary>The person's ID, reporting the person the first time. Null for an ID Linear's user list doesn't have.</summary>
    private string? PersonId(string? id)
    {
        if (id is null || !catalog.Users.TryGetValue(id, out var user))
        {
            return null;
        }

        if (_reported.Add(id))
        {
            _pending.Add(Linear.Person(user));
        }

        return id;
    }

    /// <summary><c>[label](url)</c>, <c>![label](url)</c> or a bare URL on Linear's upload host.</summary>
    [GeneratedRegex(@"(?:!?\[(?<label>[^\]\n]*)\]\()?(?<url>https://uploads\.linear\.app/[^\s)\]<>""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex UploadLink();
}

/// <summary>Reading Linear values: people, priorities, dates.</summary>
internal static class Linear
{
    public static SourcePerson Person(LinearUser user) => new()
    {
        Id = user.Id,
        Name = string.IsNullOrWhiteSpace(user.Name) ? user.DisplayName ?? user.Id : user.Name,
        Email = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
        Handle = string.IsNullOrWhiteSpace(user.DisplayName) ? null : user.DisplayName,
    };

    /// <summary>Linear's priority names: 0 is "No priority", then Urgent, High, Medium, Low.</summary>
    public static string Priority(double priority) => (int)priority switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        _ => "No priority",
    };

    /// <summary>The day of a cycle boundary, in UTC (Linear starts and ends cycles at the team's midnight).</summary>
    public static DateOnly Day(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    public static string? Text(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Number(decimal? value) => value?.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>The last segment of an upload URL, which is the file's own ID when Linear keeps no name.</summary>
    public static string FileName(Uri url) =>
        Uri.UnescapeDataString(url.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } last ? last : "file");
}
