using System.Text.Json;
using System.Text.Json.Serialization;
using Kupro.Api.Features.Issues;
using Kupro.Import.Csv;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>Which end of a four-level source folds (ADR-019 Shape). Two equal choices; the connector only preselects.</summary>
public enum FoldEnd
{
    /// <summary>Keep the top: the bottom level's items become Tasks under their grandparent.</summary>
    Bottom,

    /// <summary>Fold the top: the lower three levels move up, and top-level items become Epics with no children.</summary>
    Top,
}

public enum PersonAction
{
    Member,
    Invite,
    Placeholder,
    Skip,
}

/// <summary>Where a source project lands. Several source projects with the same key merge into one project.</summary>
public sealed record ProjectTarget(string Key, string Name, bool Existing = false);

/// <summary>What happens to a source user. <see cref="MemberId"/> is set for <see cref="PersonAction.Member"/>.</summary>
public sealed record PersonTarget(PersonAction Action, Guid? MemberId = null);

/// <summary>A source type's Kupro type. <see cref="Label"/> keeps what the type lost (Bug is a Task labelled <c>bug</c>).</summary>
public sealed record TypeTarget(IssueType Type, string? Label = null, bool Checklist = false);

/// <summary>What to read and keep. Empty <see cref="Projects"/> means every project.</summary>
public sealed record ImportScope
{
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>Skip items resolved more than this many months ago; null keeps them all.</summary>
    public int? ClosedOlderThanMonths { get; init; }

    /// <summary>Source types left out entirely, such as Azure DevOps test plans and cases.</summary>
    public IReadOnlyList<string> SkipTypes { get; init; } = [];
}

/// <summary>
/// The owner's choices, saved on the import as JSON and reused on every run. All keys are source IDs, except
/// <see cref="Priorities"/>: the format has no priority list, so those are the names the source uses.
/// </summary>
public sealed record ImportMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public IReadOnlyDictionary<string, ProjectTarget> Projects { get; init; } = new Dictionary<string, ProjectTarget>();
    public IReadOnlyDictionary<string, PersonTarget> People { get; init; } = new Dictionary<string, PersonTarget>();
    public IReadOnlyDictionary<string, TypeTarget> Types { get; init; } = new Dictionary<string, TypeTarget>();
    public IReadOnlyDictionary<string, IssueState> States { get; init; } = new Dictionary<string, IssueState>();
    public IReadOnlyDictionary<string, IssuePriority> Priorities { get; init; } = new Dictionary<string, IssuePriority>();

    /// <summary>
    /// Field values that make a Done item Canceled, by field ID: a Jira status says only "done", and its resolution
    /// ("Won't Do", "Duplicate") says how.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CanceledValues { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Statuses with no category and no name hint: Todo for now, and flagged in the plan until the owner looks.</summary>
    public IReadOnlyList<string> StatusesToReview { get; init; } = [];

    /// <summary>Select-type fields that become labels; the rest go in the details block.</summary>
    public IReadOnlyList<string> LabelFields { get; init; } = [];

    /// <summary>Versions that become milestones.</summary>
    public IReadOnlyList<string> MilestoneVersions { get; init; } = [];

    /// <summary>Hours per point. No default: the owner must enter it, and blank imports no effort from points.</summary>
    public decimal? HoursPerPoint { get; init; }

    public ImportScope Scope { get; init; } = new();
    public FoldEnd Fold { get; init; } = FoldEnd.Bottom;

    /// <summary>For a CSV import: which column holds what. Null until saved; a run then uses the proposed one.</summary>
    public CsvColumns? Csv { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ImportMapping FromJson(string json) =>
        JsonSerializer.Deserialize<ImportMapping>(json, Json) ?? throw new JsonException("The mapping is empty.");
}

/// <summary>A member the people step can match a source user to.</summary>
public sealed record MemberCandidate(Guid Id, string Name, string? Email);

/// <summary>The defaults the wizard opens with (ADR-019 People, Shape). Every one of them is the owner's to change.</summary>
public static class ImportMappingDefaults
{
    private const int MaxProjectKeyLength = 10;

    /// <summary>The same proposal from what a connector describes, before any snapshot exists.</summary>
    public static ImportMapping Propose(SourceDescription description, IReadOnlyList<MemberCandidate> members) =>
        Propose(new SnapshotHeader
        {
            Format = 1,
            Source = description.Source,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Projects = description.Projects,
            People = description.People,
            ItemTypes = description.ItemTypes,
            Statuses = description.Statuses,
            Fields = description.Fields,
        }, members);

    public static ImportMapping Propose(SnapshotHeader header, IReadOnlyList<MemberCandidate> members)
    {
        // DistinctBy: a snapshot comes from customers and scripts, so a repeated ID must not stop the wizard.
        var states = header.Statuses.DistinctBy(s => s.Id).ToDictionary(s => s.Id, s => ProposeState(s.Name, s.Category), StringComparer.Ordinal);
        var mapping = new ImportMapping
        {
            Projects = header.Projects.DistinctBy(p => p.Id).ToDictionary(p => p.Id, ProposeProject, StringComparer.Ordinal),
            People = header.People.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => ProposePerson(p, members), StringComparer.Ordinal),
            Types = header.ItemTypes.DistinctBy(t => t.Id).ToDictionary(t => t.Id, t => ProposeType(t.Name), StringComparer.Ordinal),
            States = states.Where(s => s.Value is not null).ToDictionary(s => s.Key, s => s.Value!.Value, StringComparer.Ordinal),
            StatusesToReview = [.. states.Where(s => s.Value is null).Select(s => s.Key)],
        };
        return header.Source.Kind switch
        {
            SourceKinds.AzureDevOps => AzureDevOps(header, mapping),
            SourceKinds.Jira => Jira(header, mapping),
            _ => mapping,
        };
    }

    /// <summary>
    /// Jira (task 024): New Feature is a Task like Story and Improvement; the fit makes a parent of sub-tasks a Feature. An
    /// Initiative makes four levels, so the top folds by default: Epics stay Epics. Service Management request types are
    /// left out. New statuses named for a backlog are Backlog; statuses in progress named for QA or test are In Review.
    /// A done item whose resolution is named for dropped work is Canceled. Components become labels, and versions with
    /// a date milestones.
    /// </summary>
    private static ImportMapping Jira(SnapshotHeader header, ImportMapping mapping)
    {
        string[] requestTypes = ["Service Request", "Service request with approvals", "Incident", "Problem", "Change", "IT Help", "Support"];
        var serviceDesks = header.Projects.Where(p => p.Process == "service_desk").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var types = mapping.Types.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        var skip = new List<string>();
        foreach (var type in header.ItemTypes.DistinctBy(t => t.Id))
        {
            if ((type.Project is { } project && serviceDesks.Contains(project))
                || type.Name.StartsWith("[System]", StringComparison.OrdinalIgnoreCase)
                || (serviceDesks.Count > 0 && requestTypes.Contains(type.Name, StringComparer.OrdinalIgnoreCase)))
            {
                skip.Add(type.Id);
            }
            else if (type.Name.Equals("New Feature", StringComparison.OrdinalIgnoreCase))
            {
                types[type.Id] = new TypeTarget(IssueType.Task);
            }
        }

        var states = mapping.States.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        foreach (var status in header.Statuses.Where(s => states.GetValueOrDefault(s.Id) is not IssueState.Canceled))
        {
            if (status.Category == "new" && Has(status.Name, "backlog"))
            {
                states[status.Id] = IssueState.Backlog;
            }
            else if (status.Category == "indeterminate" && (Has(status.Name, "QA") || Has(status.Name, "test")))
            {
                states[status.Id] = IssueState.InReview;
            }
        }

        return mapping with
        {
            Types = types,
            States = states,
            Fold = FoldEnd.Top,
            LabelFields = header.Fields.Any(f => f.Id == "components") ? ["components"] : [],
            CanceledValues = header.Fields.FirstOrDefault(f => f.Id == "resolution") is { } resolution
                && resolution.Options.Where(Cancels).ToList() is { Count: > 0 } canceling
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [resolution.Id] = canceling }
                : new Dictionary<string, IReadOnlyList<string>>(),
            MilestoneVersions = [.. header.Versions.Where(v => v.Date is not null).Select(v => v.Id)],
            Scope = mapping.Scope with { SkipTypes = skip },
        };
    }

    /// <summary>
    /// Azure DevOps by process (task 025): Basic's Issue sits between Epic and Task, so it is a Feature; elsewhere it is
    /// a Task. Impediments and CMMI's change requests, risks and reviews keep their type as a label. Test artefacts are
    /// left out. Priorities run 1 to 4 with nothing urgent, and states in progress named for review or test are In Review.
    /// </summary>
    private static ImportMapping AzureDevOps(SnapshotHeader header, ImportMapping mapping)
    {
        string[] tests = ["Test Plan", "Test Suite", "Test Case", "Shared Steps", "Shared Parameter"];
        string[] cmmiLabels = ["Change Request", "Risk", "Review"];
        var processes = header.Projects.Where(p => p.Process is not null).Select(p => p.Process!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? ProcessOf(SourceItemType type) =>
            header.Projects.FirstOrDefault(p => p.Id == type.Project)?.Process ?? (processes.Count == 1 ? processes[0] : null);

        var types = mapping.Types.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        var skip = new List<string>();
        foreach (var type in header.ItemTypes.DistinctBy(t => t.Id))
        {
            var process = ProcessOf(type);
            if (tests.Contains(type.Name, StringComparer.OrdinalIgnoreCase))
            {
                skip.Add(type.Id);
            }
            else if (string.Equals(type.Name, "Issue", StringComparison.OrdinalIgnoreCase))
            {
                types[type.Id] = new TypeTarget(string.Equals(process, "Basic", StringComparison.OrdinalIgnoreCase) ? IssueType.Feature : IssueType.Task);
            }
            else if (string.Equals(type.Name, "Impediment", StringComparison.OrdinalIgnoreCase)
                     || (string.Equals(process, "CMMI", StringComparison.OrdinalIgnoreCase) && cmmiLabels.Contains(type.Name, StringComparer.OrdinalIgnoreCase)))
            {
                types[type.Id] = new TypeTarget(IssueType.Task, type.Name.ToLowerInvariant());
            }
        }

        var states = mapping.States.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        foreach (var status in header.Statuses.Where(s => s.Category == "InProgress" && (Has(s.Name, "test") || Has(s.Name, "resolved"))))
        {
            states[status.Id] = IssueState.InReview;
        }

        return mapping with
        {
            Types = types,
            States = states,
            Priorities = new Dictionary<string, IssuePriority>(StringComparer.Ordinal)
            {
                ["1"] = IssuePriority.High,
                ["2"] = IssuePriority.Medium,
                ["3"] = IssuePriority.Low,
                ["4"] = IssuePriority.Low,
            },
            Scope = mapping.Scope with { SkipTypes = skip },
        };
    }

    /// <summary>Email first, then a name only one member has: two people with the same name stay a placeholder. Bots are skipped.</summary>
    public static PersonTarget ProposePerson(SourcePerson person, IReadOnlyList<MemberCandidate> members)
    {
        if (person.Bot)
        {
            return new PersonTarget(PersonAction.Skip);
        }

        var match = person.Email is { Length: > 0 } email
            ? members.FirstOrDefault(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase))
            : null;
        if (match is null)
        {
            var named = members.Where(m => string.Equals(m.Name, person.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            match = named.Count == 1 ? named[0] : null;
        }

        return match is null ? new PersonTarget(PersonAction.Placeholder) : new PersonTarget(PersonAction.Member, match.Id);
    }

    public static ProjectTarget ProposeProject(SourceProject project) => new(ProjectKey(project), project.Name);

    public static TypeTarget ProposeType(string name)
    {
        if (Has(name, "checklist") || Has(name, "subitem"))
        {
            return new TypeTarget(IssueType.Task, Checklist: true);
        }

        if (Has(name, "bug") || Has(name, "defect"))
        {
            return new TypeTarget(IssueType.Task, "bug");
        }

        // A Linear project is time-boxed work: an epic, not a Kupro project (ADR-008).
        if (Has(name, "epic") || Has(name, "initiative") || Has(name, "project"))
        {
            return new TypeTarget(IssueType.Epic);
        }

        return new TypeTarget(Has(name, "feature") ? IssueType.Feature : IssueType.Task);
    }

    /// <summary>The source's category, refined by name hints; null when neither says anything.</summary>
    public static IssueState? ProposeState(string name, string? category)
    {
        if (Cancels(name))
        {
            return IssueState.Canceled;
        }

        if (Has(name, "review"))
        {
            return IssueState.InReview;
        }

        return category?.ToLowerInvariant() switch
        {
            "backlog" or "triage" => IssueState.Backlog,
            "new" or "proposed" or "unstarted" or "todo" or "planned" or "incomplete" => IssueState.Todo,
            "indeterminate" or "inprogress" or "in progress" or "started" => IssueState.InProgress,
            "resolved" => IssueState.InReview,
            "done" or "completed" or "complete" => IssueState.Done,
            "canceled" or "cancelled" or "removed" => IssueState.Canceled,
            _ => ProposeStateFromName(name),
        };
    }

    public static IssuePriority ProposePriority(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" => IssuePriority.None,
        "1" or "urgent" or "highest" or "blocker" or "critical" or "show-stopper" => IssuePriority.Urgent,
        "2" or "high" or "major" => IssuePriority.High,
        "3" or "medium" or "normal" => IssuePriority.Medium,
        "4" or "low" or "lowest" or "minor" or "trivial" => IssuePriority.Low,
        _ => IssuePriority.None,
    };

    private static IssueState? ProposeStateFromName(string name)
    {
        if (Has(name, "backlog"))
        {
            return IssueState.Backlog;
        }

        if (Has(name, "to do") || Has(name, "todo"))
        {
            return IssueState.Todo;
        }

        if (Has(name, "progress") || Has(name, "doing"))
        {
            return IssueState.InProgress;
        }

        return Has(name, "done") || Has(name, "closed") ? IssueState.Done : null;
    }

    /// <summary>A project key that passes <see cref="Projects.ProjectService.KeyPattern"/>: at least two characters.</summary>
    private static string ProjectKey(SourceProject project)
    {
        var letters = (project.Key ?? project.Name).ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).SkipWhile(char.IsAsciiDigit);
        var key = new string([.. letters.Take(MaxProjectKeyLength)]);
        return key.Length > 1 ? key : "IMP";
    }

    /// <summary>A status or resolution name that means the work was dropped, not done.</summary>
    private static bool Cancels(string name) =>
        Has(name, "won't") || Has(name, "wont") || Has(name, "cancel") || Has(name, "reject") || Has(name, "removed") || Has(name, "duplicate");

    private static bool Has(string text, string hint) => text.Contains(hint, StringComparison.OrdinalIgnoreCase);
}
