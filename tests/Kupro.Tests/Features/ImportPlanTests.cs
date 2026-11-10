using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>The mapping defaults, hierarchy fit and dry-run plan (task 023, ADR-019 Shape). Nothing here writes.</summary>
public sealed class ImportPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_task_with_children_is_promoted_when_its_parent_allows()
    {
        var fit = HierarchyFit.Run(
        [
            new FitInput("epic", IssueType.Epic, null),
            new FitInput("story", IssueType.Task, "epic"),
            new FitInput("sub", IssueType.Task, "story"),
        ], FoldEnd.Bottom);

        fit.Items["story"].Type.ShouldBe(IssueType.Feature);
        fit.Items["sub"].ShouldBe(new FittedItem("sub", IssueType.Task, "story"));
        fit.Promoted.ShouldBe(["story"]);
        fit.Reparented.ShouldBeEmpty();
        fit.FourLevels.ShouldBeFalse();
    }

    [Fact]
    public void Fold_bottom_puts_the_lowest_level_under_its_grandparent_and_relates_it()
    {
        var fit = HierarchyFit.Run(FourLevels(), FoldEnd.Bottom);

        fit.FourLevels.ShouldBeTrue();
        fit.Items["epic"].ShouldBe(new FittedItem("epic", IssueType.Epic, null));
        fit.Items["feature"].ShouldBe(new FittedItem("feature", IssueType.Feature, "epic"));
        fit.Items["story"].ShouldBe(new FittedItem("story", IssueType.Task, "feature"));
        fit.Items["task"].ShouldBe(new FittedItem("task", IssueType.Task, "feature"));
        fit.OldParents.ShouldBe([new OldParent("task", "story")]);
        fit.Reparented.ShouldBe(["task"]);
        ShouldBeValid(fit);
    }

    [Fact]
    public void Fold_top_moves_the_lower_levels_up_and_leaves_the_top_as_a_childless_epic()
    {
        var fit = HierarchyFit.Run(FourLevels(), FoldEnd.Top);

        fit.Items["epic"].ShouldBe(new FittedItem("epic", IssueType.Epic, null));
        fit.Items["feature"].ShouldBe(new FittedItem("feature", IssueType.Epic, null));
        fit.Items["story"].ShouldBe(new FittedItem("story", IssueType.Feature, "feature"));
        fit.Items["task"].ShouldBe(new FittedItem("task", IssueType.Task, "story"));
        fit.OldParents.ShouldBe([new OldParent("feature", "epic")]);
        fit.FoldedTops.ShouldBe([new FoldedTop("feature", "epic")]);
        fit.Reparented.ShouldBeEmpty();
        ShouldBeValid(fit);
    }

    [Fact]
    public void Fold_top_ranks_by_type_level_so_a_short_branch_folds_like_the_rest()
    {
        var fit = HierarchyFit.Run(
        [
            .. FourLevels(),
            new FitInput("epic2", IssueType.Epic, null, 3),
            new FitInput("feature2", IssueType.Feature, "epic2", 2),
        ], FoldEnd.Top);

        fit.Items["feature2"].ShouldBe(new FittedItem("feature2", IssueType.Epic, null));
        fit.FoldedTops.ShouldContain(new FoldedTop("feature2", "epic2"));
        ShouldBeValid(fit);
    }

    [Fact]
    public void Five_levels_still_produce_only_valid_shapes_on_either_fold()
    {
        List<FitInput> deep =
        [
            new("a", IssueType.Epic, null),
            new("b", IssueType.Epic, "a"),
            new("c", IssueType.Feature, "b"),
            new("d", IssueType.Feature, "c"),
            new("e", IssueType.Task, "d"),
        ];

        ShouldBeValid(HierarchyFit.Run(deep, FoldEnd.Bottom));
        ShouldBeValid(HierarchyFit.Run(deep, FoldEnd.Top));
    }

    [Fact]
    public void A_parent_loop_is_broken_and_reported_instead_of_hanging()
    {
        var fit = HierarchyFit.Run(
        [
            new FitInput("a", IssueType.Task, "b"),
            new FitInput("b", IssueType.Task, "a"),
        ], FoldEnd.Bottom);

        fit.Items.Count.ShouldBe(2);
        fit.Cycles.ShouldBe(["a"]);
        ShouldBeValid(fit);
    }

    [Fact]
    public void A_parent_outside_the_snapshot_counts_as_none()
    {
        var fit = HierarchyFit.Run([new FitInput("a", IssueType.Task, "gone")], FoldEnd.Bottom);

        fit.Items["a"].Parent.ShouldBeNull();
        fit.Reparented.ShouldBeEmpty();
    }

    [Fact]
    public void Checklist_items_fold_into_their_parent_unless_they_have_none()
    {
        var fit = HierarchyFit.Run(
        [
            new FitInput("card", IssueType.Task, null),
            new FitInput("check", IssueType.Task, "card", Checklist: true),
            new FitInput("alone", IssueType.Task, null, Checklist: true),
        ], FoldEnd.Bottom);

        fit.Checklists.ShouldBe([new FoldedChecklist("check", "card")]);
        fit.Items.Keys.ShouldBe(["card", "alone"], ignoreOrder: true);

        // The card's only child folded away, so it is not promoted for having children.
        fit.Items["card"].Type.ShouldBe(IssueType.Task);
    }

    [Theory]
    [InlineData("In Review", "indeterminate", IssueState.InReview)]
    [InlineData("Won't do", "done", IssueState.Canceled)]
    [InlineData("To Do", "new", IssueState.Todo)]
    [InlineData("Active", "InProgress", IssueState.InProgress)]
    [InlineData("Closed", "Completed", IssueState.Done)]
    [InlineData("Triage", "triage", IssueState.Backlog)]
    [InlineData("Closed", null, IssueState.Done)]
    [InlineData("Lane 3", null, null)]
    public void States_come_from_the_category_refined_by_name(string name, string? category, IssueState? expected) =>
        ImportMappingDefaults.ProposeState(name, category).ShouldBe(expected);

    [Fact]
    public void People_match_by_email_then_by_a_name_only_one_member_has()
    {
        var (sofie, jonas, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        List<MemberCandidate> members =
        [
            new(sofie, "Sofie Larsen", "SOFIE@acme.example"),
            new(jonas, "Jonas Holm", null),
            new(other, "Ida Berg", "ida1@acme.example"),
            new(Guid.NewGuid(), "Ida Berg", "ida2@acme.example"),
        ];

        Propose("Someone else", "sofie@acme.example").MemberId.ShouldBe(sofie);
        Propose("jonas holm", null).MemberId.ShouldBe(jonas);
        Propose("Ida Berg", null).Action.ShouldBe(PersonAction.Placeholder);
        Propose("Nobody", "nobody@acme.example").Action.ShouldBe(PersonAction.Placeholder);

        PersonTarget Propose(string name, string? email) =>
            ImportMappingDefaults.ProposePerson(new SourcePerson { Id = "1", Name = name, Email = email }, members);
    }

    [Fact]
    public void Types_default_by_name_with_bug_as_a_labelled_task()
    {
        ImportMappingDefaults.ProposeType("Bug").ShouldBe(new TypeTarget(IssueType.Task, "bug"));
        ImportMappingDefaults.ProposeType("Epic").Type.ShouldBe(IssueType.Epic);
        ImportMappingDefaults.ProposeType("Feature").Type.ShouldBe(IssueType.Feature);
        ImportMappingDefaults.ProposeType("User Story").Type.ShouldBe(IssueType.Task);
        ImportMappingDefaults.ProposeType("Checklist item").Checklist.ShouldBeTrue();
    }

    [Fact]
    public async Task A_mapping_survives_being_saved_on_the_import()
    {
        await using var snapshot = await SampleAsync();
        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []) with { HoursPerPoint = 4, Fold = FoldEnd.Top };

        var saved = ImportMapping.FromJson(mapping.ToJson());

        saved.Fold.ShouldBe(FoldEnd.Top);
        saved.HoursPerPoint.ShouldBe(4);
        saved.Projects["10"].Key.ShouldBe("WEB");
        saved.States["3"].ShouldBe(IssueState.Done);
        saved.Types["epic"].Type.ShouldBe(IssueType.Epic);
        saved.People["u2"].Action.ShouldBe(PersonAction.Placeholder);
    }

    [Fact]
    public async Task The_plan_counts_what_a_commit_would_do_without_writing()
    {
        await using var snapshot = await SampleAsync();
        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []);

        var plan = await ImportPlanner.PlanAsync(snapshot, mapping, new HashSet<string> { "200" }, Now, Ct);

        plan.Items.ShouldBe(3);
        plan.Create.Count.ShouldBe(2);
        plan.Update.Samples.Single().ShouldBe(new PlanSample("200", "OPS-1", "Rotate keys"));
        plan.Comments.ShouldBe(1);
        plan.Attachments.ShouldBe(1);
        plan.AttachmentBytes.ShouldBe(4);
        plan.Changes.ShouldBe(1);
        plan.EffortHours.ShouldBe(6);
        plan.FourLevels.ShouldBeFalse();
        plan.FoldTop.ShouldBeNull();
        plan.StatusesToReview.Count.ShouldBe(0);
    }

    [Fact]
    public async Task The_plan_leaves_out_what_the_scope_skips_with_its_history()
    {
        await using var snapshot = await SampleAsync();
        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []) with
        {
            Scope = new ImportScope { Projects = ["20"] },
        };

        var plan = await ImportPlanner.PlanAsync(snapshot, mapping, new HashSet<string>(), Now, Ct);

        plan.Create.Samples.Single().SourceId.ShouldBe("200");
        plan.SkippedByScope.Count.ShouldBe(2);
        plan.Comments.ShouldBe(0);
        plan.Attachments.ShouldBe(0);
        plan.Changes.ShouldBe(0);
        plan.EffortHours.ShouldBe(0);
    }

    [Fact]
    public async Task The_plan_skips_items_closed_before_the_cutoff()
    {
        await using var snapshot = await SampleAsync();
        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []) with
        {
            Scope = new ImportScope { ClosedOlderThanMonths = 6 },
        };

        var plan = await ImportPlanner.PlanAsync(snapshot, mapping, new HashSet<string>(), Now, Ct);

        plan.SkippedByScope.Samples.Single().SourceId.ShouldBe("101");
    }

    [Fact]
    public void Effort_uses_the_estimate_first_and_points_only_with_a_factor()
    {
        var created = Now.AddDays(-1);
        var pointsOnly = new SourceItem
        {
            Id = "1",
            Project = "p",
            Type = "t",
            Status = "s",
            Title = "T",
            Points = 3,
            Created = created,
            Updated = created,
        };
        var both = pointsOnly with { EstimateSeconds = 5400 };

        var header = new SnapshotHeader { Format = 1, Source = new SnapshotSource { Kind = SourceKinds.Jira }, CreatedAt = created };
        var withFactor = new MappedSource(header, new ImportMapping { HoursPerPoint = 2 }, created);

        new MappedSource(header, new ImportMapping(), created).Effort(pointsOnly).ShouldBeNull();
        withFactor.Effort(pointsOnly).ShouldBe(6);
        withFactor.Effort(both).ShouldBe(1.5m);
    }

    [Fact]
    public async Task A_status_with_no_hint_or_added_later_is_flagged_for_a_look()
    {
        var source = FakeConnector.Sample();
        var created = Now.AddDays(-1);
        source.Records.Add(new SourceItem
        {
            Id = "300",
            Project = "10",
            Type = "story",
            Status = "new-status",
            Title = "New lane",
            Created = created,
            Updated = created,
        });
        await using var snapshot = await SnapshotOfAsync(source);

        var plan = await ImportPlanner.PlanAsync(snapshot, ImportMappingDefaults.Propose(snapshot.Header, []), new HashSet<string>(), Now, Ct);

        plan.StatusesToReview.Samples.Single().SourceId.ShouldBe("300");
    }

    [Fact]
    public async Task An_item_read_twice_counts_once()
    {
        var source = FakeConnector.Sample();
        source.Records.Add(source.Records.OfType<SourceItem>().First());
        await using var snapshot = await SnapshotOfAsync(source);

        var plan = await ImportPlanner.PlanAsync(snapshot, ImportMappingDefaults.Propose(snapshot.Header, []), new HashSet<string>(), Now, Ct);

        plan.Create.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_four_level_source_gets_counts_for_both_folds()
    {
        var created = Now.AddDays(-1);
        var description = new SourceDescription
        {
            Source = new SnapshotSource { Kind = SourceKinds.AzureDevOps },
            Projects = [new SourceProject { Id = "p", Name = "Web" }],
            ItemTypes =
            [
                new SourceItemType { Id = "epic", Name = "Epic", Level = 3 },
                new SourceItemType { Id = "feature", Name = "Feature", Level = 2 },
                new SourceItemType { Id = "story", Name = "User Story", Level = 1 },
                new SourceItemType { Id = "task", Name = "Task", Level = 0 },
            ],
            Statuses = [new SourceStatus { Id = "s", Name = "New", Category = "Proposed" }],
        };
        var source = new FakeConnector(description);
        source.Records.AddRange(
        [
            Item("1", "epic", null),
            Item("2", "feature", "1"),
            Item("3", "story", "2"),
            Item("4", "task", "3"),
        ]);
        await using var snapshot = await SnapshotOfAsync(source);

        var plan = await ImportPlanner.PlanAsync(snapshot, ImportMappingDefaults.Propose(snapshot.Header, []), new HashSet<string>(), Now, Ct);

        plan.FourLevels.ShouldBeTrue();
        plan.FoldBottom.ShouldBe(new FoldCounts(1, 0, 0));
        plan.FoldTop.ShouldBe(new FoldCounts(0, 0, 1));
        plan.Reparented.Samples.Single().SourceId.ShouldBe("4");

        SourceItem Item(string id, string type, string? parent) => new()
        {
            Id = id,
            Project = "p",
            Type = type,
            Status = "s",
            Title = id,
            Parent = parent,
            Created = created,
            Updated = created,
        };
    }

    private static List<FitInput> FourLevels() =>
    [
        new("epic", IssueType.Epic, null, 3),
        new("feature", IssueType.Feature, "epic", 2),
        new("story", IssueType.Task, "feature", 1),
        new("task", IssueType.Task, "story", 0),
    ];

    /// <summary>Every parent exists and is one ADR-008 allows.</summary>
    private static void ShouldBeValid(FitResult fit)
    {
        foreach (var item in fit.Items.Values.Where(i => i.Parent is not null))
        {
            IssueRules.CanBeParent(item.Type, fit.Items[item.Parent!].Type).ShouldBeTrue($"{item.Type} {item.Id} under {fit.Items[item.Parent!].Type} {item.Parent}");
        }
    }

    private static Task<SnapshotReader> SampleAsync() => SnapshotOfAsync(FakeConnector.Sample());

    private static async Task<SnapshotReader> SnapshotOfAsync(FakeConnector source)
    {
        var stream = new MemoryStream();
        await source.WriteSnapshotAsync(stream, new ReadScope(), null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        return await SnapshotReader.OpenAsync(stream, Ct);
    }
}
