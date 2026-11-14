using Kupro.Import.Snapshot;

namespace Kupro.Import.Csv;

/// <summary>
/// Which column holds what, by header name; null when the file has no such column (task 023, CSV). Saved with the
/// import's mapping. A CSV without <see cref="Key"/> or <see cref="Id"/> numbers its rows, so it can't be re-run.
/// </summary>
public sealed record CsvColumns
{
    /// <summary>Which known export the headers matched: <c>jira</c>, <c>linear</c>, or null for one Kupro guessed.</summary>
    public string? Preset { get; init; }

    public string? Key { get; init; }
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public MarkupKind DescriptionMarkup { get; init; } = MarkupKind.Plain;
    public string? Type { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }

    /// <summary>A name or an email address.</summary>
    public string? Assignee { get; init; }

    public string? Creator { get; init; }

    /// <summary>The parent's key or ID, as another row has it.</summary>
    public string? Parent { get; init; }

    /// <summary>Every column of this name is a label; one column alone is split on commas and semicolons.</summary>
    public string? Labels { get; init; }

    public string? EstimateHours { get; init; }
    public string? EstimateSeconds { get; init; }
    public string? Points { get; init; }
    public string? Start { get; init; }
    public string? Due { get; init; }
    public string? Created { get; init; }
    public string? Updated { get; init; }
    public string? Resolved { get; init; }

    /// <summary>A column naming each row's project; without one the whole file is one project.</summary>
    public string? Project { get; init; }

    /// <summary>Every column this mapping uses, so the rest go to the issue's details block.</summary>
    public IEnumerable<string> Used() =>
        new[] { Key, Id, Title, Description, Type, Status, Priority, Assignee, Creator, Parent, Labels, EstimateHours, EstimateSeconds, Points, Start, Due, Created, Updated, Resolved, Project }
            .OfType<string>();

    /// <summary>The known export the headers come from, else a guess by common names. The owner can change any of it.</summary>
    public static CsvColumns Propose(IReadOnlyList<string> headers)
    {
        var names = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        string? Has(params string[] candidates) => candidates.FirstOrDefault(names.Contains) is { } found ? headers.First(h => string.Equals(h, found, StringComparison.OrdinalIgnoreCase)) : null;

        if (names.Contains("Issue key") && names.Contains("Summary"))
        {
            return new CsvColumns
            {
                Preset = "jira",
                Key = Has("Issue key"),
                Id = Has("Issue id"),
                Title = Has("Summary"),
                Description = Has("Description"),
                DescriptionMarkup = MarkupKind.JiraWiki,
                Type = Has("Issue Type"),
                Status = Has("Status"),
                Priority = Has("Priority"),
                Assignee = Has("Assignee"),
                Creator = Has("Reporter", "Creator"),
                Parent = Has("Parent id", "Parent"),
                Labels = Has("Labels"),
                EstimateSeconds = Has("Original Estimate", "Original estimate"),
                Points = Has("Custom field (Story Points)", "Custom field (Story point estimate)", "Story Points"),
                Start = Has("Start date", "Custom field (Start date)"),
                Due = Has("Due Date", "Due date"),
                Created = Has("Created"),
                Updated = Has("Updated"),
                Resolved = Has("Resolved"),
                Project = Has("Project key"),
            };
        }

        if (names.Contains("ID") && names.Contains("Title") && names.Contains("Team"))
        {
            return new CsvColumns
            {
                Preset = "linear",
                Key = Has("ID"),
                Title = Has("Title"),
                Description = Has("Description"),
                DescriptionMarkup = MarkupKind.Markdown,
                Status = Has("Status"),
                Priority = Has("Priority"),
                Assignee = Has("Assignee"),
                Creator = Has("Creator"),
                Parent = Has("Parent issue"),
                Labels = Has("Labels"),
                Points = Has("Estimate"),
                Due = Has("Due Date"),
                Created = Has("Created"),
                Updated = Has("Updated"),
                Resolved = Has("Completed"),
                Project = Has("Team"),
            };
        }

        return new CsvColumns
        {
            Key = Has("Key", "Issue key"),
            Id = Has("Id", "Issue id"),
            Title = Has("Title", "Summary", "Name", "Subject"),
            Description = Has("Description", "Body", "Details"),
            Type = Has("Type", "Issue type", "Work item type", "Kind"),
            Status = Has("Status", "State"),
            Priority = Has("Priority"),
            Assignee = Has("Assignee", "Assigned to", "Owner"),
            Creator = Has("Creator", "Reporter", "Created by", "Author"),
            Parent = Has("Parent", "Parent key", "Parent id", "Epic"),
            Labels = Has("Labels", "Tags", "Label"),
            EstimateHours = Has("Estimate", "Estimate (hours)", "Hours", "Original estimate"),
            Points = Has("Points", "Story points", "Story Points"),
            Start = Has("Start", "Start date"),
            Due = Has("Due", "Due date", "Deadline"),
            Created = Has("Created", "Created at", "Created date"),
            Updated = Has("Updated", "Updated at", "Modified"),
            Resolved = Has("Resolved", "Completed", "Closed", "Done"),
            Project = Has("Project", "Project key"),
        };
    }
}
