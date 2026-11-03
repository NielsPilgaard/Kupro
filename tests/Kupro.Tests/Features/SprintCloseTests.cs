using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Sprint close review, split, ledger and burndown (task 028, ADR-020).</summary>
public sealed class SprintCloseTests : ApiTestBase
{
    private readonly TestClock _clock;

    public SprintCloseTests(PostgresFixture postgres) : this(postgres, new TestClock())
    {
    }

    private SprintCloseTests(PostgresFixture postgres, TestClock clock) : base(postgres, clock: clock) => _clock = clock;

    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("SPR", "Sprinting"), Json.Options, Ct),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task Close_applies_a_mixed_plan_and_leaves_every_outcome_where_the_adr_says()
    {
        var (one, two) = await TwoSprintsAsync();
        var carry = await IssueAsync("Carry", 3);
        var split = await IssueAsync("Split", 8, IssueState.InReview);
        var backlog = await IssueAsync("Backlog", 2, IssueState.InProgress);
        var cancel = await IssueAsync("Cancel", 1);
        var done = await IssueAsync("Done", 5);
        var unlisted = await IssueAsync("Unlisted", 4);

        var closed = await CloseAsync(one.Id, new CloseSprintRequest(Items:
        [
            new(carry.Key, CloseOutcome.Carry),
            new(split.Key, CloseOutcome.Split, HoursLeft: 3),
            new(backlog.Key, CloseOutcome.Backlog),
            new(cancel.Key, CloseOutcome.Cancel),
            new(done.Key, CloseOutcome.Done),
        ]));

        closed.Sprint.State.ShouldBe(SprintState.Closed);
        closed.Moved.ShouldBe(4); // carry, split, backlog and the unlisted issue
        closed.MovedTo!.Id.ShouldBe(two.Id);
        closed.Outcomes.Single(o => o.Outcome == CloseOutcome.Carry).ShouldBe(new CloseOutcomeTotal(CloseOutcome.Carry, 2, 7));
        closed.Outcomes.Single(o => o.Outcome == CloseOutcome.Split).ShouldBe(new CloseOutcomeTotal(CloseOutcome.Split, 1, 3));
        closed.Skipped.ShouldBeEmpty();

        var inTwo = await KeysInAsync(two.Id);
        var continuation = closed.Splits.ShouldHaveSingleItem();
        continuation.Original.ShouldBe(split.Key);
        continuation.HoursDone.ShouldBe(5);
        continuation.HoursLeft.ShouldBe(3);
        inTwo.Order().ShouldBe(new[] { carry.Key, unlisted.Key, continuation.Continuation }.Order());
        (await KeysInAsync(one.Id)).Order().ShouldBe(new[] { split.Key, cancel.Key, done.Key }.Order());

        (await IssueDetailAsync(backlog.Key)).SprintId.ShouldBeNull();
        (await IssueDetailAsync(backlog.Key)).State.ShouldBe(IssueState.InProgress);
        (await IssueDetailAsync(cancel.Key)).State.ShouldBe(IssueState.Canceled);
        (await IssueDetailAsync(done.Key)).State.ShouldBe(IssueState.Done);
    }

    [Fact]
    public async Task Split_finishes_the_original_and_moves_the_rest_to_a_continuation()
    {
        var (one, two) = await TwoSprintsAsync();
        var parent = await CreateAsync(new("SPR", "Parent", IssueType.Feature, Effort: 1));
        var blocker = await CreateAsync(new("SPR", "Open blocker", State: IssueState.Todo));
        var blocked = await CreateAsync(new("SPR", "Open blocked", State: IssueState.Todo));
        var closedBlocked = await CreateAsync(new("SPR", "Closed blocked", State: IssueState.Done));
        var split = await CreateAsync(new("SPR", "Half done", Description: "Body", State: IssueState.InProgress, Priority: IssuePriority.High,
            Assignee: "me", Parent: parent.Key, Effort: 8, StartDate: new(2027, 1, 4), DueDate: new(2027, 2, 1), Labels: ["bug"], Sprint: one.Id.ToString()));
        await RelateAsync(blocker.Key, RelationKind.Blocks, split.Key);
        await RelateAsync(split.Key, RelationKind.Blocks, blocked.Key);
        await RelateAsync(split.Key, RelationKind.Blocks, closedBlocked.Key);
        var parentTotal = (await IssueDetailAsync(parent.Key)).ChildEffort;

        var closed = await CloseAsync(one.Id, new CloseSprintRequest(Items: [new(split.Key, CloseOutcome.Split, HoursLeft: 3)]));

        var original = await IssueDetailAsync(split.Key);
        var next = await IssueDetailAsync(closed.Splits[0].Continuation);
        original.State.ShouldBe(IssueState.Done);
        original.Effort.ShouldBe(5);
        original.SprintId.ShouldBe(one.Id);
        original.CompletedAt.ShouldNotBeNull();
        original.ContinuedIn!.Key.ShouldBe(next.Key);

        next.Effort.ShouldBe(3);
        next.State.ShouldBe(IssueState.InProgress);
        next.SprintId.ShouldBe(two.Id);
        next.Title.ShouldBe("Half done");
        next.Description.ShouldBe("Body");
        next.Priority.ShouldBe(IssuePriority.High);
        next.Assignee!.Id.ShouldBe(original.Assignee!.Id);
        next.Parent!.Key.ShouldBe(parent.Key);
        next.StartDate.ShouldBe(new DateOnly(2027, 1, 4));
        next.DueDate.ShouldBe(new DateOnly(2027, 2, 1));
        next.Labels.ShouldBe(["bug"]);
        next.SplitFrom!.Key.ShouldBe(split.Key);

        next.Relations.ShouldContain(r => r.Type == RelationKind.Relates && r.Issue.Key == split.Key);
        next.Relations.ShouldContain(r => r.Type == RelationKind.BlockedBy && r.Issue.Key == blocker.Key);
        next.Relations.ShouldContain(r => r.Type == RelationKind.Blocks && r.Issue.Key == blocked.Key);
        original.Relations.ShouldNotContain(r => r.Issue.Key == blocker.Key || r.Issue.Key == blocked.Key);
        original.Relations.ShouldContain(r => r.Type == RelationKind.Blocks && r.Issue.Key == closedBlocked.Key, "a closed issue isn't open work to follow");

        (await IssueDetailAsync(parent.Key)).ChildEffort.ShouldBe(parentTotal, "hours done + hours left = the original effort");

        var feed = await GetAsync<IReadOnlyList<FeedItem>>(_client, $"{Ws}/issues/{split.Key}/activity");
        feed.ShouldContain(i => i.Kind == FeedItemKind.SplitInto && i.Changes[0].To == next.Key);
        var closeEntry = feed.Single(i => i.Kind == FeedItemKind.SprintClosed);
        closeEntry.Changes.ShouldContain(c => c.Field == "outcome" && c.To == "split");
        closeEntry.Changes.ShouldContain(c => c.Field == "sprint" && c.From == one.Name);
        (await GetAsync<IReadOnlyList<FeedItem>>(_client, $"{Ws}/issues/{next.Key}/activity"))
            .ShouldContain(i => i.Kind == FeedItemKind.SplitFrom && i.Changes[0].To == split.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(8d)]
    [InlineData(9d)]
    [InlineData(2.555)]
    public async Task Split_rejects_hours_left_outside_the_effort_and_changes_nothing(double? hoursLeft)
    {
        var (one, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Eight hours", 8);

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
            new CloseSprintRequest(Items: [new(issue.Key, CloseOutcome.Split, HoursLeft: (decimal?)hoursLeft)]), Json.Options, Ct),
            HttpStatusCode.BadRequest, "invalid_split");

        (await GetAsync<SprintResponse>(_client, $"{Ws}/sprints/{one.Id}")).State.ShouldBe(SprintState.Active);
        (await IssueDetailAsync(issue.Key)).Effort.ShouldBe(8);
    }

    [Fact]
    public async Task Split_needs_an_effort_and_no_children()
    {
        var (one, _) = await TwoSprintsAsync();
        var noEffort = await CreateAsync(new("SPR", "No effort", State: IssueState.Todo, Sprint: one.Id.ToString()));
        var withChild = await CreateAsync(new("SPR", "With child", IssueType.Feature, State: IssueState.Todo, Effort: 8, Sprint: one.Id.ToString()));
        await CreateAsync(new("SPR", "Child", Parent: withChild.Key));

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
            new CloseSprintRequest(Items: [new(noEffort.Key, CloseOutcome.Split, HoursLeft: 1)]), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_split");
        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
            new CloseSprintRequest(Items: [new(withChild.Key, CloseOutcome.Split, HoursLeft: 1)]), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_split");
    }

    [Fact]
    public async Task Issues_no_longer_unfinished_are_skipped_not_failed()
    {
        var (one, _) = await TwoSprintsAsync();
        var finished = await IssueAsync("Finished meanwhile", 2);
        await _client.PatchAsJsonAsync($"{Ws}/issues/{finished.Key}", new UpdateIssueRequest(State: IssueState.Done), Json.Options, Ct);

        var closed = await CloseAsync(one.Id, new CloseSprintRequest(Items: [new(finished.Key, CloseOutcome.Split, HoursLeft: 1)]));

        closed.Skipped.ShouldBe([finished.Key]);
        closed.Splits.ShouldBeEmpty();
        (await IssueDetailAsync(finished.Key)).Effort.ShouldBe(2);
    }

    [Fact]
    public async Task Targets_must_be_open_sprints_of_the_project_other_than_the_closing_one()
    {
        var (one, _) = await TwoSprintsAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("OTH", "Other"), Json.Options, Ct),
            HttpStatusCode.Created);
        var other = await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("OTH", "Theirs", new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 15)), Json.Options, Ct), HttpStatusCode.Created);

        foreach (var wrong in new[] { other.Id, one.Id })
        {
            await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
                new CloseSprintRequest(Target: wrong), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_sprint");
        }
    }

    [Fact]
    public async Task A_close_with_nothing_to_carry_needs_no_next_sprint()
    {
        var only = await SprintAsync("Only", 0);
        await _client.PostAsync($"{Ws}/sprints/{only.Id}/start", null, Ct);

        (await CloseAsync(only.Id, new CloseSprintRequest())).MovedTo.ShouldBeNull();
    }

    [Fact]
    public async Task Start_next_starts_the_target_with_carried_and_split_work_as_its_baseline()
    {
        var (one, two) = await TwoSprintsAsync();
        var carry = await IssueAsync("Carry", 3);
        var split = await IssueAsync("Split", 8);

        var closed = await CloseAsync(one.Id, new CloseSprintRequest(StartNext: true, Items:
        [
            new(carry.Key, CloseOutcome.Carry),
            new(split.Key, CloseOutcome.Split, HoursLeft: 2),
        ]));

        closed.Started!.Id.ShouldBe(two.Id);
        closed.Started.State.ShouldBe(SprintState.Active);
        (await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{two.Id}/review")).Summary.AtStart.ShouldBe(5);
    }

    [Fact]
    public async Task Closing_twice_fails_the_second_time()
    {
        var (one, _) = await TwoSprintsAsync();
        await CloseAsync(one.Id, new CloseSprintRequest());

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close", new CloseSprintRequest(), Json.Options, Ct),
            HttpStatusCode.Conflict, "sprint_not_active");
    }

    [Fact]
    public async Task Burndown_matches_a_hand_computed_sprint()
    {
        // Monday to Friday. Every step below states the team's scope and remaining after it.
        _clock.Set(new(2027, 1, 4, 8, 0, 0, TimeSpan.Zero));
        var one = await SprintAsync("One", 0, days: 4);
        var two = await SprintAsync("Two", 7, days: 4);
        var a = await CreateAsync(new("SPR", "A", State: IssueState.Todo, Assignee: "me", Effort: 8, Sprint: one.Id.ToString()));
        var b = await CreateAsync(new("SPR", "B", State: IssueState.Todo, Effort: 4, Sprint: one.Id.ToString()));
        var c = await CreateAsync(new("SPR", "C", State: IssueState.Todo, Assignee: "me", Effort: 6, Sprint: one.Id.ToString()));
        await _client.PostAsync($"{Ws}/sprints/{one.Id}/start", null, Ct); // 18 / 18

        _clock.Set(new(2027, 1, 5, 9, 0, 0, TimeSpan.Zero));
        var d = await CreateAsync(new("SPR", "D", State: IssueState.Todo, Assignee: "me", Effort: 2, Sprint: one.Id.ToString())); // 20 / 20

        _clock.Set(new(2027, 1, 6, 9, 0, 0, TimeSpan.Zero));
        await PatchAsync(a.Key, new(Effort: 10)); // 22 / 22
        await PatchAsync(b.Key, new(Assignee: "me")); // no change for the team
        await PatchAsync(c.Key, new(State: IssueState.Done)); // 22 / 16

        _clock.Set(new(2027, 1, 7, 9, 0, 0, TimeSpan.Zero));
        await PatchAsync(c.Key, new(State: IssueState.InProgress)); // 22 / 22
        await PatchAsync(d.Key, new(State: IssueState.Canceled)); // 20 / 20

        // 23:30 UTC on the 7th is already the 8th in Copenhagen.
        _clock.Set(new(2027, 1, 7, 23, 30, 0, TimeSpan.Zero));
        await PatchAsync(c.Key, new(State: IssueState.Done)); // 20 / 14

        _clock.Set(new(2027, 1, 8, 15, 0, 0, TimeSpan.Zero));
        await CloseAsync(one.Id, new CloseSprintRequest(Target: two.Id, Items:
        [
            new(a.Key, CloseOutcome.Split, HoursLeft: 4), // 6 done, so 20 / 8 on the lines
            new(b.Key, CloseOutcome.Carry), // close kinds stay off the lines
        ]));

        var burndown = await GetAsync<SprintBurndown>(_client, $"{Ws}/sprints/{one.Id}/burndown?by=assignee");

        burndown.ClosedOn.ShouldBe(new DateOnly(2027, 1, 8));
        burndown.Points.Select(p => (p.Date.Day, p.Scope, p.Remaining)).ShouldBe(
        [
            (4, 18m, 18m),
            (5, 20m, 20m),
            (6, 22m, 16m),
            (7, 20m, 20m),
            (8, 20m, 8m),
        ]);
        burndown.Ideal.Select(i => i.Hours).ShouldBe([14.4m, 10.8m, 7.2m, 3.6m, 0m]);

        var me = burndown.Assignees!.Single(x => x.Assignee is not null);
        var unassigned = burndown.Assignees!.Single(x => x.Assignee is null);
        me.Points.Select(p => (p.Scope, p.Remaining)).ShouldBe([(14m, 14m), (16m, 16m), (22m, 16m), (20m, 20m), (20m, 8m)]);
        unassigned.Points.Select(p => (p.Scope, p.Remaining)).ShouldBe([(4m, 4m), (4m, 4m), (0m, 0m), (0m, 0m), (0m, 0m)]);
        for (var day = 0; day < burndown.Points.Count; day++)
        {
            (me.Points[day].Scope + unassigned.Points[day].Scope).ShouldBe(burndown.Points[day].Scope);
            (me.Points[day].Remaining + unassigned.Points[day].Remaining).ShouldBe(burndown.Points[day].Remaining);
        }

        me.AtStart.ShouldBe(14);
        me.Completed.ShouldBe(12);
        me.Carried.ShouldBe(8, "split hours left and the reassigned issue carried");
        unassigned.AtStart.ShouldBe(4);

        var report = await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{one.Id}/review");
        report.Summary.ShouldBe(new SprintReviewSummary(
            AtStart: 18, Added: 2, Removed: 0, Reestimated: 2, Completed: 12, Carried: 4, Split: 4, ToBacklog: 0, Canceled: 2, Remaining: 0,
            CompletionPercent: 60));
        report.Unfinished.ShouldHaveSingleItem().Key.ShouldBe(b.Key);
        report.Unfinished[0].Outcome.ShouldBe(CloseOutcome.Carry);
        report.Completed.Select(i => i.Key).Order().ShouldBe(new[] { a.Key, c.Key }.Order());
        report.Completed.Single(i => i.Key == a.Key).ContinuedIn.ShouldNotBeNull();
        report.Targets.ShouldBeEmpty("a closed sprint has nothing left to send");
    }

    [Fact]
    public async Task Days_to_come_have_no_values_yet()
    {
        _clock.Set(new(2027, 1, 5, 9, 0, 0, TimeSpan.Zero));
        var one = await SprintAsync("One", 0, days: 4);
        await IssueInAsync(one.Id, "Work", 4);
        await _client.PostAsync($"{Ws}/sprints/{one.Id}/start", null, Ct);

        var burndown = await GetAsync<SprintBurndown>(_client, $"{Ws}/sprints/{one.Id}/burndown");

        burndown.Points[0].Remaining.ShouldBeNull("started on the second day");
        burndown.Points[1].Remaining.ShouldBe(4);
        burndown.Points[2].Remaining.ShouldBeNull();
        burndown.Assignees.ShouldBeNull();
    }

    [Fact]
    public async Task The_ledger_nets_to_what_is_left_in_the_sprint()
    {
        var (one, _) = await TwoSprintsAsync();
        var keep = await IssueAsync("Keep", 5);
        var moved = await IssueAsync("Moved out", 2);
        await PatchAsync(moved.Key, new(Clear: [IssueField.Sprint]));
        await _client.PostAsJsonAsync($"{Ws}/issues/{keep.Key}/move", new MoveIssueRequest(IssueState.Done), Json.Options, Ct);
        var open = await IssueAsync("Open", 3);
        await _client.PatchAsJsonAsync($"{Ws}/issues/bulk", new UpdateIssuesRequest([new(open.Key, new(Effort: 6))]), Json.Options, Ct);

        var active = await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{one.Id}/review");
        active.Summary.Remaining.ShouldBe(6);
        active.Summary.Completed.ShouldBe(5);
        active.Summary.Removed.ShouldBe(2);
        active.Unfinished.ShouldHaveSingleItem().Key.ShouldBe(open.Key);
        active.Completed.ShouldHaveSingleItem().Key.ShouldBe(keep.Key);
    }

    [Fact]
    public async Task Concurrent_updates_to_one_issue_keep_the_ledger_equal_to_the_issue()
    {
        var (one, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Contested", 1);

        var responses = await Task.WhenAll(Enumerable.Range(2, 8).Select(effort => _client.PatchAsJsonAsync(
                $"{Ws}/issues/{issue.Key}", new UpdateIssueRequest(Effort: effort), Json.Options, Ct))
            .Append(_client.PatchAsJsonAsync($"{Ws}/issues/{issue.Key}", new UpdateIssueRequest(State: IssueState.Done), Json.Options, Ct)));
        responses.ShouldAllBe(r => r.IsSuccessStatusCode);

        var live = await IssueDetailAsync(issue.Key);
        var summary = (await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{one.Id}/review")).Summary;
        live.State.ShouldBe(IssueState.Done);
        summary.Remaining.ShouldBe(0);
        summary.Completed.ShouldBe(live.Effort!.Value);
    }

    [Fact]
    public async Task An_issue_joining_while_the_sprint_starts_is_counted_once()
    {
        var one = await SprintAsync("One", 0);

        var starting = _client.PostAsync($"{Ws}/sprints/{one.Id}/start", null, Ct);
        var joining = Enumerable.Range(0, 5).Select(n => IssueInAsync(one.Id, $"Joining {n}", 2)).ToList();
        await starting;
        await Task.WhenAll(joining);

        var summary = (await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{one.Id}/review")).Summary;
        (summary.AtStart + summary.Added).ShouldBe(10);
        summary.Remaining.ShouldBe(10);
    }

    [Fact]
    public async Task An_update_racing_the_close_keeps_the_ledger_and_the_outcome()
    {
        var (one, two) = await TwoSprintsAsync();
        var issues = new List<IssueDetail>();
        for (var n = 0; n < 5; n++)
        {
            issues.Add(await IssueAsync($"Open {n}", 4));
        }

        var closing = _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close", new CloseSprintRequest(), Json.Options, Ct);
        await Task.WhenAll(issues.Select(i => _client.PatchAsJsonAsync($"{Ws}/issues/{i.Key}", new UpdateIssueRequest(Effort: 6), Json.Options, Ct)));
        (await closing).EnsureSuccessStatusCode();

        (await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint={two.Id}")).Items.Count.ShouldBe(5);
        (await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{one.Id}/review")).Summary.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task An_issue_joining_the_next_sprint_as_the_close_starts_it_is_counted_once()
    {
        var (one, two) = await TwoSprintsAsync();
        await IssueAsync("Carried", 3);

        var closing = _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close", new CloseSprintRequest(StartNext: true), Json.Options, Ct);
        var joining = Enumerable.Range(0, 5).Select(n => IssueInAsync(two.Id, $"Joining {n}", 2)).ToList();
        (await closing).EnsureSuccessStatusCode();
        await Task.WhenAll(joining);

        var summary = (await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{two.Id}/review")).Summary;
        (summary.AtStart + summary.Added).ShouldBe(13);
        summary.Remaining.ShouldBe(13);
    }

    [Fact]
    public async Task An_issue_cannot_end_up_in_a_sprint_that_closed_while_it_was_saving()
    {
        var (one, _) = await TwoSprintsAsync();
        var keys = new List<string>();
        for (var n = 0; n < 5; n++)
        {
            keys.Add((await CreateAsync(new("SPR", $"Backlog {n}", Effort: 2))).Key);
        }

        var closing = _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close", new CloseSprintRequest(), Json.Options, Ct);
        var moves = keys.Select(key => _client.PatchAsJsonAsync($"{Ws}/issues/{key}", new UpdateIssueRequest(Sprint: one.Id.ToString()), Json.Options, Ct)).ToList();
        (await closing).EnsureSuccessStatusCode();

        foreach (var response in await Task.WhenAll(moves))
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        }

        (await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint={one.Id}")).Items.ShouldBeEmpty("open work leaves a closing sprint, or never joins it");
    }

    [Fact]
    public async Task A_stale_item_with_a_stale_target_is_skipped_not_failed()
    {
        var (one, _) = await TwoSprintsAsync();
        var finished = await IssueAsync("Finished meanwhile", 2);
        await PatchAsync(finished.Key, new(State: IssueState.Done));

        var closed = await CloseAsync(one.Id, new CloseSprintRequest(Items: [new(finished.Key, CloseOutcome.Carry, Target: Guid.NewGuid())]));

        closed.Skipped.ShouldBe([finished.Key]);
    }

    [Fact]
    public async Task An_empty_or_unknown_target_is_invalid()
    {
        var (one, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Work", 2);

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
            new CloseSprintRequest(Target: Guid.Empty), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_sprint");
        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
            new CloseSprintRequest(Items: [new(issue.Key, CloseOutcome.Carry, Target: Guid.Empty)]), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_sprint");
    }

    [Fact]
    public async Task An_outcome_that_does_not_exist_is_rejected_and_changes_nothing()
    {
        var (one, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Work", 2);

        var response = await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close", new { items = new[] { new { issue = issue.Key, outcome = 99 } } }, Json.Options, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync<SprintResponse>(_client, $"{Ws}/sprints/{one.Id}")).State.ShouldBe(SprintState.Active);
    }

    [Fact]
    public async Task MovedTo_is_only_set_when_work_went_to_the_default_target()
    {
        var (one, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Later", 2);

        var closed = await CloseAsync(one.Id, new CloseSprintRequest(Items: [new(issue.Key, CloseOutcome.Backlog)]));

        closed.MovedTo.ShouldBeNull();
        closed.Moved.ShouldBe(1);
    }

    [Fact]
    public async Task An_issue_reopened_after_the_close_stays_out_of_the_report_lists()
    {
        var (one, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Reopened later", 2, IssueState.Done);
        await CloseAsync(one.Id, new CloseSprintRequest());
        await PatchAsync(issue.Key, new(State: IssueState.InProgress));

        var report = await GetAsync<SprintReview>(_client, $"{Ws}/sprints/{one.Id}/review");

        report.Unfinished.ShouldBeEmpty();
        report.Completed.ShouldBeEmpty();
        report.Summary.Completed.ShouldBe(2, "the numbers are what happened in the sprint");
    }

    [Fact]
    public async Task A_sprint_cannot_span_more_than_a_year()
    {
        var start = new DateOnly(2027, 1, 4);

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("SPR", "Forever", start, start.AddDays(366)), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_dates");

        var sprint = await SprintAsync("Short", 0);
        await ShouldBeProblemAsync(await _client.PatchAsJsonAsync($"{Ws}/sprints/{sprint.Id}",
            new UpdateSprintRequest(EndDate: new DateOnly(9999, 1, 1)), Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_dates");
    }

    [Fact]
    public async Task The_guard_fails_a_save_that_skips_the_ledger()
    {
        var (_, _) = await TwoSprintsAsync();
        var issue = await IssueAsync("Direct write", 2);
        using var scope = Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        var row = await db.Issues.IgnoreQueryFilters().SingleAsync(i => i.Id == issue.Id, Ct);

        row.Effort = 9;

        await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    private async Task<(SprintResponse One, SprintResponse Two)> TwoSprintsAsync()
    {
        var one = await SprintAsync("One", 0);
        var two = await SprintAsync("Two", 14);
        await ReadAsync<SprintResponse>(await _client.PostAsync($"{Ws}/sprints/{one.Id}/start", null, Ct));
        return (one, two);
    }

    private async Task<SprintResponse> SprintAsync(string name, int offsetDays, int days = 11)
    {
        var start = new DateOnly(2027, 1, 4).AddDays(offsetDays);
        return await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("SPR", name, start, start.AddDays(days)), Json.Options, Ct), HttpStatusCode.Created);
    }

    private Task<IssueDetail> IssueAsync(string title, decimal effort, IssueState state = IssueState.Todo) =>
        CreateAsync(new("SPR", title, State: state, Effort: effort, Sprint: "active"));

    private Task<IssueDetail> IssueInAsync(Guid sprint, string title, decimal effort) =>
        CreateAsync(new("SPR", title, State: IssueState.Todo, Effort: effort, Sprint: sprint.ToString()));

    private async Task<IssueDetail> CreateAsync(CreateIssueRequest request) =>
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", request, Json.Options, Ct), HttpStatusCode.Created);

    private async Task PatchAsync(string key, UpdateIssueRequest request) =>
        await ReadAsync<IssueDetail>(await _client.PatchAsJsonAsync($"{Ws}/issues/{key}", request, Json.Options, Ct));

    private Task<IssueDetail> IssueDetailAsync(string key) => GetAsync<IssueDetail>(_client, $"{Ws}/issues/{key}");

    private async Task RelateAsync(string key, RelationKind kind, string target) =>
        (await _client.PostAsJsonAsync($"{Ws}/issues/{key}/relations", new AddRelationRequest(kind, target), Json.Options, Ct)).EnsureSuccessStatusCode();

    private async Task<IEnumerable<string>> KeysInAsync(Guid sprint) =>
        (await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint={sprint}")).Items.Select(i => i.Key);

    private async Task<CloseSprintResponse> CloseAsync(Guid sprint, CloseSprintRequest request) =>
        await ReadAsync<CloseSprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints/{sprint}/close", request, Json.Options, Ct));

    /// <summary>Set by the tests that need exact days; otherwise the system clock.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset? _now;

        public void Set(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now ?? base.GetUtcNow();
    }
}
