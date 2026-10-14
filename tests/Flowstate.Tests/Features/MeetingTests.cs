using System.Net;
using System.Net.Http.Json;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Meetings;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Sprints;
using Flowstate.Api.Features.Tokens;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flowstate.Tests.Features;

/// <summary>
/// Task 015: the meeting agent. No model is configured in tests, so the offline keyword extractor turns clear commands
/// into proposals; matching, approval, full auto and undo are the real code paths.
/// </summary>
public sealed class MeetingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private SprintResponse _next = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("FLOW", "Flowstate"), Json.Options, Ct),
            HttpStatusCode.Created);
        foreach (var title in new[] { "Render delivery plan", "Login bug on Safari", "Login bug on Firefox" })
        {
            await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("FLOW", title, State: IssueState.Todo), Json.Options, Ct),
                HttpStatusCode.Created);
        }

        _next = await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("FLOW", "Sprint 15", new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30)), Json.Options, Ct), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Suggest_mode_proposes_a_grouped_diff_and_changes_nothing_until_approved()
    {
        var meeting = await StartAsync();

        var heard = await SayAsync(meeting, "Okay, let's move flow 1 to the next sprint, we won't make it this week.");

        var proposal = heard.Proposals.ShouldHaveSingleItem();
        proposal.Issue!.Key.ShouldBe("FLOW-1");
        proposal.Operation.ShouldBe(ProposalOperation.Sprint);
        proposal.ValueDisplay.ShouldBe("Sprint 15");
        proposal.Before.ShouldBe("none");
        proposal.Snippet!.ShouldContain("FLOW-1 to the next sprint");
        proposal.Status.ShouldBe(ProposalStatus.Pending);
        (await IssueAsync("FLOW-1")).SprintId.ShouldBeNull();

        var result = (await ReviewAsync(meeting, new ProposalReview(proposal.Id, ReviewDecision.Approve))).ShouldHaveSingleItem();

        result.Error.ShouldBeNull();
        result.Proposal.Status.ShouldBe(ProposalStatus.Applied);
        (await IssueAsync("FLOW-1")).SprintId.ShouldBe(_next.Id);
    }

    [Fact]
    public async Task Applied_changes_show_the_agent_the_meeting_and_the_snippet_in_the_activity_feed()
    {
        var meeting = await StartAsync();
        var proposal = (await SayAsync(meeting, "FLOW-1 is done.")).Proposals.ShouldHaveSingleItem();
        await ReviewAsync(meeting, new ProposalReview(proposal.Id, ReviewDecision.Approve));

        var feed = await GetAsync<List<FeedItem>>(_client, $"{Ws}/issues/FLOW-1/activity");

        var change = feed.Last();
        change.Actor.Name.ShouldBe(MeetingService.AgentName);
        change.Via.ShouldBe(Surface.Meeting);
        change.Changes.ShouldContain(c => c.Field == "state" && c.To == "done");
        change.Meeting!.MeetingId.ShouldBe(meeting.Id);
        change.Meeting.Snippet.ShouldBe("FLOW-1 is done");
        change.Meeting.ApprovedBy!.Kind.ShouldBe(MemberKind.Human);
    }

    [Fact]
    public async Task Edit_and_reject_work_per_proposal()
    {
        var meeting = await StartAsync();
        var proposals = (await SayAsync(meeting, "Move FLOW-1 to review. Estimate FLOW-2 at 2 days.")).Proposals;
        var state = proposals.Single(p => p.Operation == ProposalOperation.State);
        var estimate = proposals.Single(p => p.Operation == ProposalOperation.Estimate);
        estimate.Value.ShouldBe("16");

        var results = await ReviewAsync(meeting,
            new ProposalReview(state.Id, ReviewDecision.Approve, Value: "inProgress"),
            new ProposalReview(estimate.Id, ReviewDecision.Reject));

        results[0].Proposal.Edited.ShouldBeTrue();
        (await IssueAsync("FLOW-1")).State.ShouldBe(IssueState.InProgress);
        results[1].Proposal.Status.ShouldBe(ProposalStatus.Rejected);
        (await IssueAsync("FLOW-2")).Effort.ShouldBeNull();
    }

    [Fact]
    public async Task A_vague_reference_with_several_matches_asks_which_issue_and_never_auto_applies()
    {
        await EnableFullAutoAsync();
        var meeting = await StartAsync(MeetingMode.FullAuto);

        var proposal = (await SayAsync(meeting, "The login bug is done.")).Proposals.ShouldHaveSingleItem();

        proposal.NeedsIssue.ShouldBeTrue();
        proposal.Status.ShouldBe(ProposalStatus.Pending);
        proposal.Candidates.Select(c => c.Key).ShouldBe(["FLOW-2", "FLOW-3"], ignoreOrder: true);
        (await ReviewAsync(meeting, new ProposalReview(proposal.Id, ReviewDecision.Approve)))[0].Error!.Code.ShouldBe("issue_required");

        var picked = await ReviewAsync(meeting, new ProposalReview(proposal.Id, ReviewDecision.Approve, Issue: "FLOW-3"));

        picked[0].Proposal.Status.ShouldBe(ProposalStatus.Applied);
        (await IssueAsync("FLOW-3")).State.ShouldBe(IssueState.Done);
        (await IssueAsync("FLOW-2")).State.ShouldBe(IssueState.Todo);
    }

    [Fact]
    public async Task Full_auto_applies_safe_proposals_at_once_and_undo_reverts_them()
    {
        await EnableFullAutoAsync();
        var meeting = await StartAsync(MeetingMode.FullAuto);

        var heard = await SayAsync(meeting, "Move FLOW-1 to the next sprint. Create a task for flaky search test. Cancel FLOW-2.");

        var sprint = heard.Proposals.Single(p => p.Operation == ProposalOperation.Sprint);
        sprint.Status.ShouldBe(ProposalStatus.Applied);
        sprint.AutoApplied.ShouldBeTrue();
        (await IssueAsync("FLOW-1")).SprintId.ShouldBe(_next.Id);
        heard.Proposals.Single(p => p.Operation == ProposalOperation.CreateIssue).Status.ShouldBe(ProposalStatus.Pending);
        heard.Proposals.Single(p => p.Value == "canceled").Status.ShouldBe(ProposalStatus.Pending, "destructive changes wait");

        var undone = await ReadAsync<ProposalResponse>(await _client.PostAsync($"{Ws}/meetings/{meeting.Id}/proposals/{sprint.Id}/undo", null, Ct));

        undone.Status.ShouldBe(ProposalStatus.Undone);
        (await IssueAsync("FLOW-1")).SprintId.ShouldBeNull();
    }

    [Fact]
    public async Task Undo_refuses_when_someone_changed_the_field_since_and_undo_meeting_reverts_the_rest()
    {
        var meeting = await StartAsync();
        var proposals = (await SayAsync(meeting, "FLOW-1 is done. Assign FLOW-2 to me.")).Proposals;
        await ReviewAsync(meeting, proposals.Select(p => new ProposalReview(p.Id, ReviewDecision.Approve)).ToArray());
        (await _client.PatchAsJsonAsync($"{Ws}/issues/FLOW-1", new UpdateIssueRequest(State: IssueState.InReview), Json.Options, Ct)).EnsureSuccessStatusCode();

        var state = proposals.Single(p => p.Operation == ProposalOperation.State);
        await ShouldBeProblemAsync(await _client.PostAsync($"{Ws}/meetings/{meeting.Id}/proposals/{state.Id}/undo", null, Ct),
            HttpStatusCode.Conflict, "changed_since_applied");

        var all = await ReadAsync<List<ProposalReviewResult>>(await _client.PostAsync($"{Ws}/meetings/{meeting.Id}/undo", null, Ct));

        all.Single(r => r.Proposal.Operation == ProposalOperation.Assignee).Proposal.Status.ShouldBe(ProposalStatus.Undone);
        all.Single(r => r.Proposal.Operation == ProposalOperation.State).Error!.Code.ShouldBe("changed_since_applied");
        (await IssueAsync("FLOW-2")).Assignee.ShouldBeNull();
        (await IssueAsync("FLOW-1")).State.ShouldBe(IssueState.InReview);
    }

    [Fact]
    public async Task Only_owners_enable_full_auto_and_the_settings_show_suggest_mode_stats()
    {
        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/meetings", new StartMeetingRequest("FLOW", Mode: MeetingMode.FullAuto), Json.Options, Ct),
            HttpStatusCode.Forbidden, "full_auto_disabled");
        var agent = Factory.CreateBearerClient((await NewTokenAsync(_client, new("bot", ApiTokenKind.Agent, "bot", null, _workspace.Slug))).Secret);
        (await agent.PatchAsJsonAsync($"{Ws}/meeting-settings", new UpdateMeetingSettingsRequest(FullAutoEnabled: true), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var meeting = await StartAsync();
        var proposals = (await SayAsync(meeting, "FLOW-1 is done. FLOW-2 is done. Move FLOW-3 to review.")).Proposals;
        await ReviewAsync(meeting,
            new ProposalReview(proposals[0].Id, ReviewDecision.Approve),
            new ProposalReview(proposals[1].Id, ReviewDecision.Approve, Value: "inReview"),
            new ProposalReview(proposals[2].Id, ReviewDecision.Reject));

        var settings = await GetAsync<MeetingSettingsResponse>(_client, $"{Ws}/meeting-settings");

        settings.FullAutoEnabled.ShouldBeFalse();
        settings.Stats.Single(s => s.Operation == ProposalOperation.State).ShouldBe(new OperationStats(ProposalOperation.State, 3, 1, 1, 1));
        (await GetAsync<MeetingResponse>(_client, $"{Ws}/meetings/{meeting.Id}")).Counts.ShouldBe(new MeetingCounts(0, 2, 1, 0));
    }

    [Fact]
    public async Task Two_microphones_hearing_the_same_sentence_make_one_proposal()
    {
        var meeting = await StartAsync();
        const string sentence = "Move FLOW-1 to the next sprint. Create a task for flaky search test.";

        var heard = await Task.WhenAll(SayAsync(meeting, sentence), SayAsync(meeting, sentence));

        heard.Sum(h => h.Proposals.Count).ShouldBe(2);
        (await SayAsync(meeting, "Create a new task for the flaky search test.")).Proposals.ShouldBeEmpty("similar wording");
        (await SayAsync(meeting, "Move FLOW-1 to review.")).Proposals.ShouldHaveSingleItem("a different change");
    }

    [Fact]
    public async Task A_rejected_proposal_can_be_proposed_again()
    {
        var meeting = await StartAsync();
        var first = (await SayAsync(meeting, "FLOW-1 is done.")).Proposals.ShouldHaveSingleItem();
        await ReviewAsync(meeting, new ProposalReview(first.Id, ReviewDecision.Reject));

        (await SayAsync(meeting, "FLOW-1 is done.")).Proposals.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Danish_meetings_work()
    {
        var meeting = await StartAsync(language: "da");

        var proposals = (await SayAsync(meeting, "Vi flytter FLOW-1 til næste sprint, og FLOW-2 er færdig.")).Proposals;

        proposals.Select(p => (p.Issue!.Key, p.Operation, p.ValueDisplay)).ShouldBe(
            [("FLOW-1", ProposalOperation.Sprint, "Sprint 15"), ("FLOW-2", ProposalOperation.State, "done")], ignoreOrder: true);
    }

    [Fact]
    public async Task Paused_and_ended_meetings_stop_listening()
    {
        var meeting = await StartAsync();
        await ReadAsync<MeetingResponse>(await _client.PatchAsJsonAsync($"{Ws}/meetings/{meeting.Id}", new UpdateMeetingRequest(State: MeetingState.Paused), Json.Options, Ct));

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/meetings/{meeting.Id}/transcript", new TranscriptRequest("FLOW-1 is done"), Json.Options, Ct),
            HttpStatusCode.Conflict, "meeting_not_live");

        var ended = await ReadAsync<MeetingResponse>(await _client.PatchAsJsonAsync($"{Ws}/meetings/{meeting.Id}", new UpdateMeetingRequest(State: MeetingState.Ended), Json.Options, Ct));
        ended.EndedAt.ShouldNotBeNull();
        await ShouldBeProblemAsync(await _client.PatchAsJsonAsync($"{Ws}/meetings/{meeting.Id}", new UpdateMeetingRequest(State: MeetingState.Live), Json.Options, Ct),
            HttpStatusCode.Conflict, "meeting_ended");
    }

    [Fact]
    public async Task Other_workspaces_cannot_see_a_meeting()
    {
        var meeting = await StartAsync();
        var (other, otherWorkspace, _) = await NewWorkspaceAsync("Other");

        (await other.GetAsync($"/api/v1/workspaces/{otherWorkspace.Slug}/meetings/{meeting.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"{Ws}/meetings/{meeting.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transcripts_are_deleted_when_the_meeting_ends()
    {
        var meeting = await StartAsync();
        var proposal = (await SayAsync(meeting, "FLOW-1 is done.")).Proposals.ShouldHaveSingleItem();
        (await GetAsync<List<TranscriptSegmentResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/transcript")).ShouldNotBeEmpty();

        await ReadAsync<MeetingResponse>(await _client.PatchAsJsonAsync($"{Ws}/meetings/{meeting.Id}", new UpdateMeetingRequest(State: MeetingState.Ended), Json.Options, Ct));

        (await GetAsync<List<TranscriptSegmentResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/transcript")).ShouldBeEmpty();
        (await GetAsync<List<ProposalResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/proposals")).Single(p => p.Id == proposal.Id).Snippet.ShouldBeNull();
    }

    [Fact]
    public async Task Transcripts_of_a_meeting_nobody_ends_are_deleted_after_a_day()
    {
        var meeting = await StartAsync();
        var proposal = (await SayAsync(meeting, "FLOW-1 is done.")).Proposals.ShouldHaveSingleItem();

        await PurgeAsync(DateTimeOffset.UtcNow.AddHours(23));
        (await GetAsync<List<TranscriptSegmentResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/transcript")).ShouldNotBeEmpty();

        await PurgeAsync(DateTimeOffset.UtcNow.AddDays(2));
        (await GetAsync<List<TranscriptSegmentResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/transcript")).ShouldBeEmpty();
        (await GetAsync<List<ProposalResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/proposals")).Single(p => p.Id == proposal.Id).Snippet.ShouldBeNull();
    }

    [Fact]
    public async Task Transcripts_are_kept_for_the_configured_days()
    {
        (await GetAsync<MeetingSettingsResponse>(_client, $"{Ws}/meeting-settings")).TranscriptRetentionDays.ShouldBe(0);
        await ReadAsync<MeetingSettingsResponse>(await _client.PatchAsJsonAsync($"{Ws}/meeting-settings",
            new UpdateMeetingSettingsRequest(TranscriptRetentionDays: 30), Json.Options, Ct));
        var meeting = await StartAsync();
        var proposal = (await SayAsync(meeting, "FLOW-1 is done.")).Proposals.ShouldHaveSingleItem();
        await ReadAsync<MeetingResponse>(await _client.PatchAsJsonAsync($"{Ws}/meetings/{meeting.Id}", new UpdateMeetingRequest(State: MeetingState.Ended), Json.Options, Ct));

        await PurgeAsync(DateTimeOffset.UtcNow.AddDays(29));
        (await GetAsync<List<TranscriptSegmentResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/transcript")).ShouldNotBeEmpty();

        await PurgeAsync(DateTimeOffset.UtcNow.AddDays(31));
        (await GetAsync<List<TranscriptSegmentResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/transcript")).ShouldBeEmpty();
        (await GetAsync<List<ProposalResponse>>(_client, $"{Ws}/meetings/{meeting.Id}/proposals")).Single(p => p.Id == proposal.Id).Snippet.ShouldBeNull();
    }

    private async Task PurgeAsync(DateTimeOffset now)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowstateDbContext>>().CreateDbContextAsync(Ct);
        await MeetingService.PurgeExpiredTranscriptsAsync(db, now, Ct);
    }

    private async Task<MeetingResponse> StartAsync(MeetingMode? mode = null, string language = "en") =>
        await ReadAsync<MeetingResponse>(await _client.PostAsJsonAsync($"{Ws}/meetings",
            new StartMeetingRequest("FLOW", MeetingType.Standup, mode, language), Json.Options, Ct), HttpStatusCode.Created);

    private async Task<TranscriptResult> SayAsync(MeetingResponse meeting, string text) =>
        await ReadAsync<TranscriptResult>(await _client.PostAsJsonAsync($"{Ws}/meetings/{meeting.Id}/transcript", new TranscriptRequest(text), Json.Options, Ct));

    private async Task<List<ProposalReviewResult>> ReviewAsync(MeetingResponse meeting, params ProposalReview[] items) =>
        await ReadAsync<List<ProposalReviewResult>>(await _client.PostAsJsonAsync($"{Ws}/meetings/{meeting.Id}/proposals/review",
            new ReviewProposalsRequest(items), Json.Options, Ct));

    private async Task EnableFullAutoAsync() =>
        await ReadAsync<MeetingSettingsResponse>(await _client.PatchAsJsonAsync($"{Ws}/meeting-settings",
            new UpdateMeetingSettingsRequest(FullAutoEnabled: true), Json.Options, Ct));

    private Task<IssueDetail> IssueAsync(string key) => GetAsync<IssueDetail>(_client, $"{Ws}/issues/{key}");
}
