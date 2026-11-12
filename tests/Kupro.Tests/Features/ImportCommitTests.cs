using System.Net;
using System.Text.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>The commit path: queue, batches, keys, hierarchy, resume (task 023, ADR-019 Pipeline).</summary>
[Collection(nameof(StorageTests))]
public sealed partial class ImportCommitTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, storage.Settings), IClassFixture<StorageFixture>
{
    private static readonly DateTimeOffset Created = new(2025, 3, 1, 9, 0, 0, TimeSpan.FromHours(2));

    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private Guid _owner;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync("Importing");
        _owner = (await GetAsync<List<MemberResponse>>(_client, Url("members"))).Single().Id;
    }

    [Fact]
    public async Task A_snapshot_commits_with_its_keys_people_times_and_details()
    {
        var run = await CommitAsync(FakeConnector.Sample());
        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);

        var story = await IssueAsync("WEB-2");
        story.Title.ShouldBe("Pay with MobilePay");
        story.Type.ShouldBe(IssueType.Task);
        story.Parent!.Key.ShouldBe("WEB-1");
        story.State.ShouldBe(IssueState.Done);
        story.Priority.ShouldBe(IssuePriority.High);
        story.Assignee!.Name.ShouldBe("Sofie Larsen");
        story.CreatedBy.Name.ShouldBe("Jonas Holm");
        story.Effort.ShouldBe(6);
        story.DueDate.ShouldBe(new DateOnly(2025, 3, 20));
        story.Labels.ShouldBe(["payments"]);
        story.SprintId.ShouldNotBeNull();
        story.CreatedAt.ShouldBe(new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero));
        story.CompletedAt.ShouldNotBeNull();
        story.Description!.ShouldStartWith("Æble **først**");
        story.Description.ShouldContain("Imported from Jira");
        story.Description.ShouldContain("| Points | 3 |");
        story.Relations.ShouldContain(r => r.Type == RelationKind.Relates && r.Issue.Key == "WEB-1");

        (await IssueAsync("WEB-1")).Type.ShouldBe(IssueType.Epic);
        (await IssueAsync("OPS-1")).Title.ShouldBe("Rotate keys");

        // The team's next issue doesn't take a kept number.
        var next = await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync(Url("issues"), new CreateIssueRequest("WEB", "Native"), Json.Options, Ct), HttpStatusCode.Created);
        next.Key.ShouldBe("WEB-3");

        var counts = JsonSerializer.Deserialize<ImportRunCounts>(run.Counts!, JsonSerializerOptions.Web)!;
        counts.Issues.ShouldBe(3);
        counts.Projects.ShouldBe(2);
        counts.Members.ShouldBe(2);
        counts.Sprints.ShouldBe(1);
        counts.Relations.ShouldBe(1);

        var members = await GetAsync<List<MemberResponse>>(_client, Url("members"));
        members.Count(m => m.Status == MemberStatus.Placeholder).ShouldBe(2);

        await WithDbAsync(async db =>
        {
            var issue = await db.Issues.SingleAsync(i => i.Key == "WEB-2", Ct);
            (await db.ImportedItems.AnyAsync(i => i.SourceKind == "item" && i.SourceId == "101" && i.EntityId == issue.Id && i.LastValues != null, Ct)).ShouldBeTrue();
            (await db.Activity.Where(a => a.IssueId == issue.Id).Select(a => a.Via).Distinct().ToListAsync(Ct)).ShouldBe([Surface.Import]);
            (await db.Activity.CountAsync(a => a.IssueId == issue.Id && a.Kind == "imported", Ct)).ShouldBe(1);
            (await db.Activity.SingleAsync(a => a.IssueId == issue.Id && a.Kind == "created", Ct)).CreatedAt.ShouldBe(new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero));
        });
    }

    [Fact]
    public async Task The_plan_runs_over_the_stored_snapshot_and_matches_what_the_commit_writes()
    {
        var source = Source(
            Item("e", key: "WEB-1", type: "epic"),
            Item("s", key: "WEB-2", type: "story", parent: "e"),
            Item("t", key: "WEB-3", type: "task", parent: "s"),
            Item("old", key: "WEB-4", type: "story", resolved: Created.AddYears(-10)));
        var mapping = await MappingAsync(source) with { Scope = new ImportScope { ClosedOlderThanMonths = 12 } };
        var import = await AddImportAsync(source, mapping);

        var dryRun = await RunAsync(import, ImportRunKind.DryRun);
        dryRun.State.ShouldBe(ImportRunState.Succeeded, dryRun.Error);
        var plan = JsonSerializer.Deserialize<ImportPlan>(dryRun.Report!, JsonSerializerOptions.Web)!;
        plan.Create.Count.ShouldBe(3);
        plan.SkippedByScope.Count.ShouldBe(1);
        plan.Promoted.Count.ShouldBe(1);

        await WithDbAsync(async db => (await db.Issues.CountAsync(Ct)).ShouldBe(0));

        var commit = await RunAsync(import, ImportRunKind.Commit);
        commit.State.ShouldBe(ImportRunState.Succeeded, commit.Error);
        JsonSerializer.Deserialize<ImportRunCounts>(commit.Counts!, JsonSerializerOptions.Web)!.Issues.ShouldBe(plan.Create.Count);
        (await IssueAsync("WEB-2")).Type.ShouldBe(IssueType.Feature);
        (await IssueAsync("WEB-3")).Parent!.Key.ShouldBe("WEB-2");
    }

    [Fact]
    public async Task Fold_bottom_keeps_valid_shapes_and_relates_each_item_to_its_old_parent()
    {
        var source = Source(
            Item("a", key: "WEB-1", type: "epic"),
            Item("b", key: "WEB-2", type: "feature", parent: "a"),
            Item("c", key: "WEB-3", type: "story", parent: "b"),
            Item("d", key: "WEB-4", type: "task", parent: "c"));

        (await CommitAsync(source)).State.ShouldBe(ImportRunState.Succeeded);

        (await IssueAsync("WEB-1")).Type.ShouldBe(IssueType.Epic);
        (await IssueAsync("WEB-2")).Parent!.Key.ShouldBe("WEB-1");
        (await IssueAsync("WEB-3")).Parent!.Key.ShouldBe("WEB-2");
        var bottom = await IssueAsync("WEB-4");
        bottom.Type.ShouldBe(IssueType.Task);
        bottom.Parent!.Key.ShouldBe("WEB-2");
        bottom.Relations.ShouldContain(r => r.Type == RelationKind.Relates && r.Issue.Key == "WEB-3");
    }

    [Fact]
    public async Task Fold_top_keeps_the_top_as_a_childless_epic_and_labels_its_old_children()
    {
        var source = Source(
            Item("a", key: "WEB-1", type: "epic", title: "Big Initiative"),
            Item("b", key: "WEB-2", type: "feature", parent: "a"),
            Item("c", key: "WEB-3", type: "story", parent: "b"),
            Item("d", key: "WEB-4", type: "task", parent: "c"));
        var mapping = await MappingAsync(source) with { Fold = FoldEnd.Top };

        (await CommitAsync(source, mapping)).State.ShouldBe(ImportRunState.Succeeded);

        var top = await IssueAsync("WEB-1");
        top.Type.ShouldBe(IssueType.Epic);
        top.Children.ShouldBeEmpty();
        top.Relations.ShouldContain(r => r.Type == RelationKind.Relates && r.Issue.Key == "WEB-2");
        var movedUp = await IssueAsync("WEB-2");
        movedUp.Type.ShouldBe(IssueType.Epic);
        movedUp.Parent.ShouldBeNull();
        movedUp.Labels.ShouldContain("big initiative");
        (await IssueAsync("WEB-3")).Type.ShouldBe(IssueType.Feature);
        (await IssueAsync("WEB-4")).Parent!.Key.ShouldBe("WEB-3");
    }

    [Fact]
    public async Task A_taken_number_takes_the_next_free_one_and_the_source_key_becomes_an_alias()
    {
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync(Url("projects"), new CreateProjectRequest("WEB", "Web"), Json.Options, Ct), HttpStatusCode.Created);
        var native = await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync(Url("issues"), new CreateIssueRequest("WEB", "Native"), Json.Options, Ct), HttpStatusCode.Created);
        native.Key.ShouldBe("WEB-1");

        var source = Source(
            Item("clash", key: "WEB-1", url: "https://src.example/WEB-1"),
            Item("ado", key: "4567"),
            Item("nokey", key: null));
        var mapping = await MappingAsync(source) with
        {
            Projects = new Dictionary<string, ProjectTarget> { ["p"] = new("WEB", "Web", Existing: true) },
        };

        var run = await CommitAsync(source, mapping);
        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);

        await WithDbAsync(async db =>
        {
            var keys = await db.ImportedItems.Where(i => i.SourceKind == "item")
                .Join(db.Issues, i => i.EntityId, x => x.Id, (i, x) => new { i.SourceId, x.Key }).ToDictionaryAsync(r => r.SourceId, r => r.Key, Ct);
            keys["ado"].ShouldBe("WEB-4567");
            keys["clash"].ShouldNotBe("WEB-1");
            keys["nokey"].ShouldNotBe(keys["clash"]);

            var aliases = await db.IssueAliases.Join(db.Issues, a => a.IssueId, x => x.Id, (a, x) => new { a.Alias, x.Key }).ToDictionaryAsync(r => r.Alias, r => r.Key, Ct);
            aliases["WEB-1"].ShouldBe(keys["clash"]);
            aliases["https://src.example/WEB-1"].ShouldBe(keys["clash"]);
            aliases["#4567"].ShouldBe("WEB-4567");
            aliases.ContainsKey("WEB-4567").ShouldBeFalse();
        });

        // Issue numbers still count on from the highest one kept.
        var next = await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync(Url("issues"), new CreateIssueRequest("WEB", "After"), Json.Options, Ct), HttpStatusCode.Created);
        int.Parse(next.Key["WEB-".Length..], System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(4567);
    }

    [Fact]
    public async Task Merged_source_projects_share_one_project_and_keep_apart_by_alias()
    {
        var description = Description();
        var source = new FakeConnector(description with { Projects = [new SourceProject { Id = "p", Key = "AAA", Name = "A" }, new SourceProject { Id = "q", Key = "BBB", Name = "B" }] });
        source.Records.AddRange([Item("one", key: "AAA-1"), Item("two", key: "BBB-1", project: "q")]);
        var mapping = await MappingAsync(source) with
        {
            Projects = new Dictionary<string, ProjectTarget> { ["p"] = new("WEB", "Web"), ["q"] = new("WEB", "Web") },
        };

        (await CommitAsync(source, mapping)).State.ShouldBe(ImportRunState.Succeeded);

        await WithDbAsync(async db =>
        {
            (await db.Projects.CountAsync(Ct)).ShouldBe(1);
            (await db.Issues.Select(i => i.Key).OrderBy(k => k).ToListAsync(Ct)).Count.ShouldBe(2);
            (await db.IssueAliases.Select(a => a.Alias).OrderBy(a => a).ToListAsync(Ct)).ShouldBe(["AAA-1", "BBB-1"]);
        });
    }

    [Fact]
    public async Task A_second_import_taking_the_same_alias_leaves_it_with_the_first_and_says_so()
    {
        var first = await CommitAsync(Source(Item("x", key: "12")));
        first.State.ShouldBe(ImportRunState.Succeeded);

        var other = Source(Item("y", key: "12"));
        var mapping = await MappingAsync(other) with { Projects = new Dictionary<string, ProjectTarget> { ["p"] = new("OTH", "Other") } };
        var second = await CommitAsync(other, mapping);

        second.State.ShouldBe(ImportRunState.Succeeded, second.Error);
        JsonSerializer.Deserialize<ImportRunCounts>(second.Counts!, JsonSerializerOptions.Web)!.AliasesSkipped.ShouldBe(1);
        await WithDbAsync(async db => (await db.IssueAliases.Join(db.Issues, a => a.IssueId, i => i.Id, (a, i) => i.Key).SingleAsync(Ct)).ShouldBe("WEB-12"));
    }

    [Fact]
    public async Task Checklist_items_fold_into_their_parent_and_a_closed_sprint_takes_its_old_issues()
    {
        var description = Description();
        var source = new FakeConnector(description with
        {
            ItemTypes = [.. description.ItemTypes, new SourceItemType { Id = "check", Name = "Checklist item" }],
        });
        source.Records.AddRange(
        [
            new SourceSprint { Id = "sp", Name = "Old sprint", Start = new DateOnly(2024, 1, 1), End = new DateOnly(2024, 1, 14), Closed = true },
            new SourceSprint { Id = "next", Name = "Next sprint", Project = "p", Start = new DateOnly(2030, 1, 1), End = new DateOnly(2030, 1, 14) },
            Item("card", key: "WEB-1") with { Sprint = "sp" },
            Item("c1", key: null, type: "check", parent: "card", title: "Step one"),
            Item("c2", key: null, type: "check", parent: "card", title: "Step two", status: "done"),
        ]);

        (await CommitAsync(source)).State.ShouldBe(ImportRunState.Succeeded);

        var card = await IssueAsync("WEB-1");
        var text = card.Description!;
        text.ShouldContain("- [ ] Step one");
        text.ShouldContain("- [x] Step two");
        card.Children.ShouldBeEmpty();
        await WithDbAsync(async db =>
        {
            (await db.Issues.CountAsync(Ct)).ShouldBe(1);
            var states = await db.Sprints.ToDictionaryAsync(s => s.Name, s => s.State, Ct);
            states["Old sprint"].ShouldBe(SprintState.Closed);
            states["Next sprint"].ShouldBe(SprintState.Planned);
            (await db.SprintLedger.CountAsync(Ct)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task A_commit_killed_mid_run_resumes_without_duplicates()
    {
        const int Total = ImportCommit.BatchSize * 4;
        var items = Enumerable.Range(1, Total).Select(n => Item($"i{n}", key: $"WEB-{n}", assignee: "u1", creator: "u1")).ToArray();
        var source = Source(items);
        source.Records.Insert(0, new SourcePerson { Id = "u1", Name = "Sofie Larsen" });
        var import = await AddImportAsync(source, await MappingAsync(source));

        // A run row the job ignores, driven by hand so it can be killed between batches.
        var killed = await AddRunAsync(import, ImportRunState.Failed);
        using var kill = new CancellationTokenSource();
        var running = KillableRunAsync(import, killed, kill.Token);
        await WaitUntilAsync(async db => await db.ImportedItems.CountAsync(i => i.ImportId == import && i.SourceKind == "item", Ct) >= ImportCommit.BatchSize);
        await kill.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => running);

        var written = 0;
        await WithDbAsync(async db => written = await db.Issues.CountAsync(Ct));
        written.ShouldBeGreaterThan(0);
        written.ShouldBeLessThan(Total);
        (written % ImportCommit.BatchSize).ShouldBe(0, "a batch commits whole or not at all");

        var resumed = await RunAsync(import, ImportRunKind.Commit);
        resumed.State.ShouldBe(ImportRunState.Succeeded, resumed.Error);

        await WithDbAsync(async db =>
        {
            (await db.Issues.CountAsync(Ct)).ShouldBe(Total);
            (await db.Issues.Select(i => i.Key).Distinct().CountAsync(Ct)).ShouldBe(Total);
            (await db.Issues.AnyAsync(i => i.Key == $"WEB-{Total}", Ct)).ShouldBeTrue();
            (await db.Members.CountAsync(m => m.Status == MemberStatus.Placeholder, Ct)).ShouldBe(1);
        });
        JsonSerializer.Deserialize<ImportRunCounts>(resumed.Counts!, JsonSerializerOptions.Web)!.AlreadyImported.ShouldBe(written);

        // Running it again finds nothing to do.
        var again = await RunAsync(import, ImportRunKind.Commit);
        JsonSerializer.Deserialize<ImportRunCounts>(again.Counts!, JsonSerializerOptions.Web)!.Issues.ShouldBe(0);
        await WithDbAsync(async db => (await db.Issues.CountAsync(Ct)).ShouldBe(Total));
    }

    [Fact]
    public async Task A_second_run_in_a_workspace_waits_for_the_first()
    {
        var one = Source(Enumerable.Range(1, 200).Select(n => Item($"a{n}", key: $"WEB-{n}")).ToArray());
        var other = Source(Item("b", key: "OTH-1"));
        var first = await AddImportAsync(one, await MappingAsync(one));
        var second = await AddImportAsync(other, await MappingAsync(other) with { Projects = new Dictionary<string, ProjectTarget> { ["p"] = new("OTH", "Other") } });

        var firstRun = await QueueAsync(first, ImportRunKind.Commit);
        var secondRun = await QueueAsync(second, ImportRunKind.Commit);
        var done = await WaitForAsync(firstRun);
        var after = await WaitForAsync(secondRun);

        done.State.ShouldBe(ImportRunState.Succeeded, done.Error);
        after.State.ShouldBe(ImportRunState.Succeeded, after.Error);
        after.StartedAt!.Value.ShouldBeGreaterThanOrEqualTo(done.CompletedAt!.Value);
    }

    [Fact]
    public async Task A_project_key_someone_else_uses_fails_the_run_with_a_message_for_the_owner()
    {
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync(Url("projects"), new CreateProjectRequest("WEB", "Web"), Json.Options, Ct), HttpStatusCode.Created);

        var run = await CommitAsync(Source(Item("a", key: "WEB-1")));

        run.State.ShouldBe(ImportRunState.Failed);
        run.Error!.ShouldContain("WEB");
        await WithDbAsync(async db => (await db.Issues.CountAsync(Ct)).ShouldBe(0));
    }

    [Fact]
    public async Task A_person_with_a_member_gone_from_the_workspace_fails_rather_than_inventing_someone()
    {
        var source = Source(Item("a", key: "WEB-1", assignee: "u1"));
        var mapping = await MappingAsync(source) with
        {
            People = new Dictionary<string, PersonTarget> { ["u1"] = new(PersonAction.Member, Guid.NewGuid()) },
        };

        var run = await CommitAsync(source, mapping);

        run.State.ShouldBe(ImportRunState.Failed);
        run.Error!.ShouldContain("no longer active");
    }

    [Fact]
    public async Task Skipped_people_author_through_one_shared_placeholder_and_leave_assignments_empty()
    {
        var source = Source(Item("a", key: "WEB-1", assignee: "bot", creator: "bot"), Item("b", key: "WEB-2", creator: "bot"), Item("c", key: "WEB-3"));
        source.Records.Insert(0, new SourcePerson { Id = "bot", Name = "Jira Automation" });
        var mapping = await MappingAsync(source) with { People = new Dictionary<string, PersonTarget> { ["bot"] = new(PersonAction.Skip) } };

        (await CommitAsync(source, mapping)).State.ShouldBe(ImportRunState.Succeeded);

        var issue = await IssueAsync("WEB-1");
        issue.Assignee.ShouldBeNull();
        issue.CreatedBy.Name.ShouldBe("Skipped users from Jira");
        issue.Description!.ShouldContain("| Assignee | Jira Automation |");
        (await IssueAsync("WEB-3")).CreatedBy.Id.ShouldBe(_owner, "the source names nobody, so nobody is misattributed");
        var members = await GetAsync<List<MemberResponse>>(_client, Url("members"));
        members.Count(m => m.Status == MemberStatus.Placeholder).ShouldBe(1);
        await WithDbAsync(async db =>
        {
            var id = (await db.Issues.SingleAsync(i => i.Key == "WEB-1", Ct)).Id;
            (await db.Activity.SingleAsync(a => a.IssueId == id && a.Kind == "created", Ct)).Data!.ShouldContain("Jira Automation");
        });
    }

    [Fact]
    public async Task An_import_in_another_workspace_can_not_be_run_from_here()
    {
        var source = Source(Item("a", key: "WEB-1"));
        var import = await AddImportAsync(source, await MappingAsync(source));
        var (_, theirs, _) = await NewWorkspaceAsync("Theirs");

        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(theirs.Id);

        var result = await scope.ServiceProvider.GetRequiredService<ImportService>().QueueAsync(import, ImportRunKind.Commit, Ct);

        result.Error!.Code.ShouldBe("import_not_found");
    }

    [Fact]
    public async Task History_comes_across_with_comments_files_and_the_changelog_in_the_feed()
    {
        var source = History();

        var run = await CommitAsync(source, files: true);
        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);

        var counts = Counts(run);
        counts.Comments.ShouldBe(2);
        counts.Attachments.ShouldBe(1);
        counts.AttachmentsMissing.ShouldBe(0);
        counts.Changes.ShouldBe(2);
        counts.SourceChanges.ShouldBe(1);

        Guid attachment = default;
        await WithDbAsync(async db =>
        {
            var file = await db.Attachments.SingleAsync(Ct);
            file.Ready.ShouldBeTrue();
            file.Size.ShouldBe(4);
            file.FileName.ShouldBe("flow.png");
            file.CreatedAt.ShouldBe(new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero));
            attachment = file.Id;
        });

        var text = (await IssueAsync("WEB-2")).Description!;
        text.ShouldContain($"![flow](attachment:{attachment})");
        text.ShouldContain("@Sofie Larsen");
        text.ShouldContain("see WEB-1");
        await using (var stored = await Factory.Services.GetRequiredService<ObjectStorage>().OpenReadAsync($"{_workspace.Id}/{attachment}", Ct))
        {
            using var bytes = new MemoryStream();
            await stored!.CopyToAsync(bytes, Ct);
            bytes.ToArray().ShouldBe("PNG!"u8.ToArray());
        }

        var day = new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var feed = await GetAsync<List<FeedItem>>(_client, Url("issues/WEB-2/activity"));
        var comment = feed.Single(f => f.Kind == FeedItemKind.Comment && f.Body == "Looks good");
        comment.Actor.Name.ShouldBe("Jonas Holm");
        comment.Via.ShouldBe(Surface.Import);
        comment.At.ShouldBe(day.AddDays(1));
        var state = feed.Single(f => f.Kind == FeedItemKind.Updated && f.Changes.Any(c => c.Field == "state"));
        state.Actor.Name.ShouldBe("Sofie Larsen");
        state.At.ShouldBe(day.AddDays(2));
        state.Changes.Single().ShouldBe(new FeedChange("state", "todo", "done"));
        feed.ShouldContain(f => f.Kind == FeedItemKind.Updated && f.Changes.Any(c => c.Field == "title" && c.From == "Pay by card"));

        await WithDbAsync(async db =>
        {
            var issue = await db.Issues.SingleAsync(i => i.Key == "WEB-2", Ct);
            var raw = await db.Activity.SingleAsync(a => a.IssueId == issue.Id && a.Kind == "source_changed", Ct);
            raw.Via.ShouldBe(Surface.Import);
            var data = JsonElement.Parse(raw.Data!);
            data.GetProperty("field").GetString().ShouldBe("Team");
            data.GetProperty("to").GetString().ShouldBe("Red");
        });
    }

    [Fact]
    public async Task Running_an_import_again_writes_no_comment_file_or_change_twice()
    {
        var source = History();
        var import = await AddImportAsync(source, await MappingAsync(source), files: true);
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);

        var before = 0;
        await WithDbAsync(async db => before = await db.Activity.CountAsync(Ct));

        var again = await RunAsync(import, ImportRunKind.Commit);
        again.State.ShouldBe(ImportRunState.Succeeded, again.Error);
        var counts = Counts(again);
        (counts.Comments, counts.Attachments, counts.Changes, counts.SourceChanges).ShouldBe((0, 0, 0, 0));
        await WithDbAsync(async db =>
        {
            (await db.Activity.CountAsync(Ct)).ShouldBe(before);
            (await db.Comments.CountAsync(Ct)).ShouldBe(2);
            (await db.Attachments.CountAsync(Ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task A_skipped_persons_comments_and_changes_keep_their_name_under_the_shared_placeholder()
    {
        var source = History();
        var mapping = await MappingAsync(source) with { People = new Dictionary<string, PersonTarget> { ["u2"] = new(PersonAction.Skip) } };

        (await CommitAsync(source, mapping)).State.ShouldBe(ImportRunState.Succeeded);

        var feed = await GetAsync<List<FeedItem>>(_client, Url("issues/WEB-2/activity"));
        feed.Single(f => f.Kind == FeedItemKind.Comment && f.Body == "Looks good").Actor.Name.ShouldBe("Skipped users from Jira");
        await WithDbAsync(async db =>
        {
            (await db.Comments.SingleAsync(c => c.Body == "Looks good", Ct)).SourceAuthor.ShouldBe("Jonas Holm");
            var updates = await db.Activity.Where(a => a.Kind == "updated").Select(a => a.Data!).ToListAsync(Ct);
            var title = JsonElement.Parse(updates.Single(d => d.Contains("title", StringComparison.Ordinal)));
            title.GetProperty("sourceActor").GetString().ShouldBe("Jonas Holm");
        });
        feed.Single(f => f.Kind == FeedItemKind.Updated && f.Changes.Any(c => c.Field == "title")).Changes.ShouldAllBe(c => c.Field == "title");
    }

    [Fact]
    public async Task A_file_the_snapshot_does_not_carry_is_counted_and_its_link_kept()
    {
        var run = await CommitAsync(History());

        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);
        Counts(run).AttachmentsMissing.ShouldBe(1);
        (await IssueAsync("WEB-2")).Description!.ShouldContain("](https://acme.example/files/a1)");
        await WithDbAsync(async db => (await db.Attachments.CountAsync(Ct)).ShouldBe(0));
    }

    [Fact]
    public async Task Comments_and_files_on_a_folded_checklist_item_go_to_the_issue_that_holds_it()
    {
        var description = Description();
        var source = new FakeConnector(description with { ItemTypes = [.. description.ItemTypes, new SourceItemType { Id = "check", Name = "Checklist item" }] });
        source.Records.AddRange(
        [
            Item("card", key: "WEB-1"),
            Item("c1", key: null, type: "check", parent: "card", title: "Step one"),
            new SourceComment { Id = "k1", Item = "c1", Body = "On the step", Created = Created },
            new SourceChange { Id = "x", Item = "c1", At = Created, Field = "status", From = "open", To = "done" },
        ]);

        (await CommitAsync(source)).State.ShouldBe(ImportRunState.Succeeded);

        var feed = await GetAsync<List<FeedItem>>(_client, Url("issues/WEB-1/activity"));
        feed.ShouldContain(f => f.Kind == FeedItemKind.Comment && f.Body == "On the step");
        feed.ShouldNotContain(f => f.Kind == FeedItemKind.Updated, "a checklist line's history isn't the card's");
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>The sample with an inline image, a mention, a link to another source issue, and more history.</summary>
    private static FakeConnector History()
    {
        var source = FakeConnector.Sample();
        var at = new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var records = source.Records;
        records[records.FindIndex(r => r is SourcePerson { Id: "u1" })] = new SourcePerson { Id = "u1", Name = "Sofie Larsen", Email = "sofie@acme.example", Handle = "sofie" };
        records[records.FindIndex(r => r is SourceAttachment)] = new SourceAttachment
        {
            Id = "a1",
            Item = "101",
            FileName = "flow.png",
            ContentType = "image/png",
            Size = 4,
            Created = at,
            Url = "https://acme.example/files/a1",
        };
        var story = (SourceItem)records.Single(r => r is SourceItem { Id: "101" });
        records[records.IndexOf(story)] = story with
        {
            Description = "<p>Ask @sofie, see <a href=\"https://acme.example/browse/WEB-1\">WEB-1</a></p><p><img src=\"https://acme.example/files/a1\" alt=\"flow\"></p>",
        };
        records[records.FindIndex(r => r is SourceItem { Id: "100" })] = ((SourceItem)records.Single(r => r is SourceItem { Id: "100" })) with { Url = "https://acme.example/browse/WEB-1" };
        records.AddRange(
        [
            new SourceComment { Id = "c2", Item = "101", Author = "u1", Body = "<p>Shipped</p>", Markup = MarkupKind.Html, Created = at.AddDays(3) },
            new SourceChange { Id = "h2", Item = "101", Actor = "u2", At = at.AddDays(1), Field = "title", From = "Pay by card", To = "Pay with MobilePay" },
            new SourceChange { Id = "h3", Item = "101", Actor = "u1", At = at.AddDays(1), Field = "cf_team", From = "Blue", To = "Red" },
        ]);
        return source;
    }

    private string Url(string path) => $"/api/v1/workspaces/{_workspace.Slug}/{path}";

    private Task<IssueDetail> IssueAsync(string key) => GetAsync<IssueDetail>(_client, Url($"issues/{key}"));

    private static SourceDescription Description() => new()
    {
        Source = new SnapshotSource { Kind = SourceKinds.Jira },
        Projects = [new SourceProject { Id = "p", Key = "WEB", Name = "Web" }],
        ItemTypes =
        [
            new SourceItemType { Id = "epic", Name = "Epic", Level = 3 },
            new SourceItemType { Id = "feature", Name = "Feature", Level = 2 },
            new SourceItemType { Id = "story", Name = "Story", Level = 1 },
            new SourceItemType { Id = "task", Name = "Task", Level = 0 },
        ],
        Statuses = [new SourceStatus { Id = "open", Name = "To Do", Category = "new" }, new SourceStatus { Id = "done", Name = "Done", Category = "done" }],
    };

    private static FakeConnector Source(params SourceItem[] items)
    {
        var source = new FakeConnector(Description());
        source.Records.AddRange(items);
        return source;
    }

    private static SourceItem Item(
        string id, string? key = null, string project = "p", string type = "task", string status = "open", string? parent = null, string? title = null,
        string? assignee = null, string? creator = null, string? url = null, DateTimeOffset? resolved = null) => new()
        {
            Id = id,
            Key = key,
            Project = project,
            Type = type,
            Status = status,
            Title = title ?? $"Item {id}",
            Parent = parent,
            Assignee = assignee,
            Creator = creator,
            Url = url,
            Created = Created,
            Updated = Created,
            Resolved = resolved,
        };

    private static async Task<string> WriteAsync(FakeConnector source, bool files = false, string? cursor = null)
    {
        var path = Path.GetTempFileName();
        await using var file = File.Create(path);
        await source.WriteSnapshotAsync(file, new ReadScope(), cursor, includeFiles: files, TimeProvider.System, Ct);
        return path;
    }

    private static async Task<ImportMapping> MappingAsync(FakeConnector source)
    {
        var path = await WriteAsync(source);
        try
        {
            await using var file = File.OpenRead(path);
            await using var reader = await SnapshotReader.OpenAsync(file, Ct);
            return ImportMappingDefaults.Propose(reader.Header, []);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<Guid> AddImportAsync(FakeConnector source, ImportMapping mapping, bool files = false)
    {
        var import = Guid.CreateVersion7();
        await WithDbAsync(async db =>
        {
            db.WorkspaceImports.Add(new WorkspaceImport
            {
                Id = import,
                WorkspaceId = _workspace.Id,
                SourceKind = SourceKinds.Jira,
                Mapping = mapping.ToJson(),
                CreatedById = _owner,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });

        await StoreAsync(import, source, files, null);
        return import;
    }

    private async Task StoreAsync(Guid import, FakeConnector source, bool files, string? cursor)
    {
        var path = await WriteAsync(source, files, cursor);
        try
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
            var stored = await scope.ServiceProvider.GetRequiredService<ImportService>().StoreSnapshotAsync(import, path, Ct);
            stored.Error.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<ImportRun> CommitAsync(FakeConnector source, ImportMapping? mapping = null, bool files = false) =>
        await RunAsync(await AddImportAsync(source, mapping ?? await MappingAsync(source), files), ImportRunKind.Commit);

    private static ImportRunCounts Counts(ImportRun run) => JsonSerializer.Deserialize<ImportRunCounts>(run.Counts!, JsonSerializerOptions.Web)!;

    private async Task<ImportRun> RunAsync(Guid import, ImportRunKind kind) => await WaitForAsync(await QueueAsync(import, kind));

    private async Task<Guid> QueueAsync(Guid import, ImportRunKind kind)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        var queued = await scope.ServiceProvider.GetRequiredService<ImportService>().QueueAsync(import, kind, Ct);
        queued.Error.ShouldBeNull();
        return queued.Value!.Id;
    }

    private async Task<Guid> AddRunAsync(Guid import, ImportRunState state)
    {
        var id = Guid.CreateVersion7();
        await WithDbAsync(async db =>
        {
            db.ImportRuns.Add(new ImportRun { Id = id, WorkspaceId = _workspace.Id, ImportId = import, Kind = ImportRunKind.Commit, State = state, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        return id;
    }

    private async Task KillableRunAsync(Guid importId, Guid runId, CancellationToken kill)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
        context.SetForSystem(_workspace.Id);
        context.Surface = Surface.Import;
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(kill);
        var import = await db.WorkspaceImports.AsNoTracking().SingleAsync(i => i.Id == importId, kill);
        var run = await db.ImportRuns.AsNoTracking().SingleAsync(r => r.Id == runId, kill);
        await using var file = await scope.ServiceProvider.GetRequiredService<ImportSnapshots>().OpenAsync(_workspace.Id, importId, kill);
        await using var snapshot = await SnapshotReader.OpenAsync(file!, kill);
        await scope.ServiceProvider.GetRequiredService<ImportCommit>().RunAsync(import, run, snapshot, ImportMapping.FromJson(import.Mapping!), kill);
    }

    private async Task<ImportRun> WaitForAsync(Guid run)
    {
        ImportRun? found = null;
        await WaitUntilAsync(async db =>
        {
            found = await db.ImportRuns.AsNoTracking().SingleAsync(r => r.Id == run, Ct);
            return found.State is ImportRunState.Succeeded or ImportRunState.Failed;
        });
        return found!;
    }

    private async Task WaitUntilAsync(Func<KuproDbContext, Task<bool>> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            var finished = false;
            await WithDbAsync(async db => finished = await done(db));
            if (finished)
            {
                return;
            }

            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the import job didn't get there in time");
            await Task.Delay(10, Ct);
        }
    }

    private async Task WithDbAsync(Func<KuproDbContext, Task> use)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        await use(db);
    }
}
