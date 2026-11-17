using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Tests.Features;

/// <summary>
/// The pure decisions behind a commit (ADR-019 Re-runs and conflicts, Changelog replay, Shape): what a re-run does with
/// each field, how a source change reads in the feed, and what an item becomes. Nothing here needs a database.
/// </summary>
public sealed class ImportDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly SnapshotHeader Header = new()
    {
        Format = 1,
        Source = new SnapshotSource { Kind = SourceKinds.Jira },
        CreatedAt = Now,
        Statuses = [new SourceStatus { Id = "1", Name = "To Do" }, new SourceStatus { Id = "3", Name = "Done" }, new SourceStatus { Id = "4", Name = "Closed" }],
        Fields = [new SourceField { Id = "customfield_1", Name = "Team", Kind = FieldKind.Text }],
    };

    private static readonly ImportMapping Mapping = new()
    {
        States = new Dictionary<string, IssueState> { ["1"] = IssueState.Todo, ["3"] = IssueState.Done, ["4"] = IssueState.Done },
        HoursPerPoint = 2,
    };

    private static readonly ImportedValues Base = new("Title", null, IssueState.Todo, IssuePriority.Medium, null, null, null, null, null, null, []);

    [Fact]
    public void A_field_follows_the_source_only_while_Kupro_still_holds_what_was_imported()
    {
        var last = Base;
        var decisions = ImportedField.DecideAll(
            next: Base with { Title = "Source title", State = IssueState.Done, Priority = IssuePriority.High },
            last,
            current: Base with { State = IssueState.InProgress, Priority = IssuePriority.High });

        decisions[ImportedField.Named(ImportedField.Title)].ShouldBe(ImportFieldDecision.Apply);
        decisions[ImportedField.Named(ImportedField.State)].ShouldBe(ImportFieldDecision.Conflict);
        decisions[ImportedField.Named(ImportedField.Priority)].ShouldBe(ImportFieldDecision.Agreed);
        decisions[ImportedField.Named(ImportedField.Assignee)].ShouldBe(ImportFieldDecision.Unchanged);
    }

    [Fact]
    public void Dates_that_would_cross_become_conflicts_instead_of_an_invalid_issue()
    {
        var (june, july, august) = (new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1));
        var last = Base with { Start = june, Due = august };

        // The source moves the start past the due date the team brought forward.
        var decisions = ImportedField.DecideAll(next: last with { Start = august }, last, current: last with { Due = july });

        decisions[ImportedField.Named(ImportedField.Start)].ShouldBe(ImportFieldDecision.Conflict);
        decisions[ImportedField.Named(ImportedField.Due)].ShouldBe(ImportFieldDecision.Unchanged);
    }

    [Fact]
    public void A_status_change_reads_as_the_states_it_maps_to()
    {
        var (kind, data) = Translate(Change("status", "1", "3"));

        kind.ShouldBe(ChangeTranslator.Updated);
        data["state"].ShouldBe(new FieldChange(IssueState.Todo, IssueState.Done));
    }

    [Fact]
    public void A_change_between_statuses_with_the_same_state_is_dropped() =>
        Translate(Change("status", "3", "4")).Kind.ShouldBeNull();

    [Fact]
    public void A_change_Kupro_has_no_field_for_keeps_the_source_name_and_text()
    {
        var (kind, data) = Translate(Change("customfield_1", "a", "b") with { ToText = "Platform" });

        kind.ShouldBe(ChangeTranslator.SourceChanged);
        data["field"].ShouldBe("Team");
        data["from"].ShouldBe("a");
        data["to"].ShouldBe("Platform");
    }

    [Fact]
    public void An_assignee_the_import_skipped_stays_a_source_change()
    {
        var known = Guid.NewGuid();
        var targets = Targets(member: p => p == "u1" ? known : null);

        Translate(Change("assignee", null, "u1"), targets).Data["assignee"].ShouldBe(new FieldChange(null, known));
        Translate(Change("assignee", "u1", "bot"), targets).Kind.ShouldBe(ChangeTranslator.SourceChanged);
    }

    [Fact]
    public void Labels_and_points_translate_to_Kupro_values()
    {
        var labels = Translate(Change("labels", "b; a", "a, c")).Data["labels"].ShouldBeOfType<FieldChange>();
        labels.From.ShouldBe(new List<string> { "a", "b" });
        labels.To.ShouldBe(new List<string> { "a", "c" });
        Translate(Change("labels", "a; b", "b,a")).Kind.ShouldBeNull();
        Translate(Change("points", "1", "3")).Data["effort"].ShouldBe(new FieldChange(2m, 6m));
    }

    [Fact]
    public void An_item_keeps_what_Kupro_cannot_store_in_its_details_block()
    {
        var values = new ItemValues(new MappedSource(Header, Mapping, Now), new ImportDescription(Header, Mapping));
        var item = new SourceItem
        {
            Id = "10",
            Key = "WEB-10",
            Project = "p",
            Type = "story",
            Status = "3",
            Title = "  ",
            Assignee = "gone",
            Start = new DateOnly(2026, 9, 1),
            Due = new DateOnly(2026, 8, 1),
            Points = 2_000_000,
            Created = Now,
            Updated = Now,
        };

        var (imported, truncated) = values.Of(item, ["bug"], [], assignee: null, parent: null, sprint: null, foldedTop: null, [], ImportReferences.None);

        truncated.ShouldBeFalse();
        imported.Title.ShouldBe("(no title)");
        imported.State.ShouldBe(IssueState.Done);
        imported.Start.ShouldBeNull();
        imported.Due.ShouldBe(new DateOnly(2026, 8, 1));
        imported.Effort.ShouldBeNull();
        imported.Labels.ShouldBe(["bug"]);
        imported.Description.ShouldNotBeNull();
        imported.Description.ShouldContain("Start date");
        imported.Description.ShouldContain("Effort");
        imported.Description.ShouldContain("Assignee");
    }

    private static SourceChange Change(string field, string? from, string? to) =>
        new() { Id = "c1", Item = "10", At = Now, Field = field, From = from, To = to };

    private static ChangeTargets Targets(Func<string, Guid?>? member = null) => new(member ?? (_ => null), _ => null, _ => null);

    private static (string? Kind, Dictionary<string, object?> Data) Translate(SourceChange change, ChangeTargets? targets = null) =>
        new ChangeTranslator(Header, new MappedSource(Header, Mapping, Now)).Translate(change, targets ?? Targets());
}
