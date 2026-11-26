using System.Net;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Time;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Task 034, ADR-021: time entries, locking, close-and-log, who sees what, history, export and retention.</summary>
public sealed class TimeTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private HttpClient _owner = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";
    private static DateOnly Today => TimeService.LocalDay(DateTimeOffset.UtcNow);

    // A past day: a test that fills one can't be split by Copenhagen midnight between its requests.
    private static DateOnly Yesterday => Today.AddDays(-1);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_owner, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _owner.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct),
            HttpStatusCode.Created);
        foreach (var title in new[] { "Build the grid", "Meetings" })
        {
            await ReadAsync<IssueDetail>(await _owner.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("KUP", title), Json.Options, Ct),
                HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Time_registration_is_off_until_an_owner_turns_it_on()
    {
        (await GetAsync<WorkspaceResponse>(_owner, Ws)).TimeRegistration.ShouldBeFalse();
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-1", 60)), HttpStatusCode.Conflict, "time_registration_off");

        var on = await ReadAsync<WorkspaceResponse>(await _owner.PatchAsJsonAsync(Ws, new UpdateWorkspaceRequest(TimeRegistration: true), Json.Options, Ct));
        on.TimeRegistration.ShouldBeTrue();
        var entry = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("kup-1", 90, Note: " Grid layout ")), HttpStatusCode.Created);

        entry.Issue.Key.ShouldBe("KUP-1");
        entry.ProjectKey.ShouldBe("KUP");
        entry.Date.ShouldBe(Today);
        entry.Minutes.ShouldBe(90);
        entry.Note.ShouldBe("Grid layout");
        entry.Locked.ShouldBeFalse();
        entry.Member.Name.ShouldBe("Test User");

        // Off again: what was logged stays readable, but nothing changes.
        await _owner.PatchAsJsonAsync(Ws, new UpdateWorkspaceRequest(TimeRegistration: false), Json.Options, Ct);
        (await GetAsync<MyTime>(_owner, $"{Ws}/time/mine")).TotalMinutes.ShouldBe(90);
        await ShouldBeProblemAsync(await _owner.DeleteAsync($"{Ws}/time/{entry.Id}", Ct), HttpStatusCode.Conflict, "time_registration_off");
    }

    [Fact]
    public async Task A_week_has_every_day_with_its_total_and_edits_keep_history()
    {
        await TurnOnAsync();
        var monday = TimeService.WeekStart(Today);
        var first = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 120, monday)), HttpStatusCode.Created);
        await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-2", 30, monday, "Standup")), HttpStatusCode.Created);

        var edited = await ReadAsync<TimeEntryResponse>(await _owner.PatchAsJsonAsync($"{Ws}/time/{first.Id}",
            new UpdateTimeEntryRequest(Minutes: 150, Note: "Grid"), Json.Options, Ct));
        edited.Minutes.ShouldBe(150);

        var week = await GetAsync<MyTime>(_owner, $"{Ws}/time/mine?to={Today:yyyy-MM-dd}");
        week.From.ShouldBe(monday);
        week.Days.Count.ShouldBe(7);
        week.Days[0].ShouldBe(new TimeDay(monday, 180));
        week.Days.Skip(1).ShouldAllBe(d => d.Minutes == 0);
        week.TotalMinutes.ShouldBe(180);

        (await _owner.DeleteAsync($"{Ws}/time/{first.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var history = await GetAsync<List<TimeEntryChangeResponse>>(_owner, $"{Ws}/time/changes");
        history.Where(c => c.EntryId == first.Id).Select(c => c.Kind).ShouldBe([TimeChangeKind.Deleted, TimeChangeKind.Updated, TimeChangeKind.Created]);
        var update = history.Single(c => c.EntryId == first.Id && c.Kind == TimeChangeKind.Updated);
        (update.OldMinutes, update.NewMinutes, update.OldNote, update.NewNote).ShouldBe((120, 150, null, "Grid"));

        var older = await GetAsync<List<TimeEntryChangeResponse>>(_owner, $"{Ws}/time/changes?before={history[1].Id}");
        older.Select(c => c.Id).ShouldBe(history.Skip(2).Select(c => c.Id));
    }

    [Fact]
    public async Task A_day_holds_at_most_24_hours_and_no_future_days()
    {
        await TurnOnAsync();
        await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 1000, Yesterday)), HttpStatusCode.Created);

        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-2", 500, Yesterday)), HttpStatusCode.Conflict, "day_full");
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-2", 60, Today.AddDays(2))), HttpStatusCode.BadRequest, "future_date");
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-2", 0)), HttpStatusCode.BadRequest, "validation");
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-2", 60, State: IssueState.InProgress)), HttpStatusCode.BadRequest, "invalid_state");
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-99", 60)), HttpStatusCode.NotFound, "issue_not_found");
        await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-2", 440, Yesterday)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Time_on_a_resolved_issue_is_locked_until_it_is_reopened()
    {
        await TurnOnAsync();
        var entry = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 60)), HttpStatusCode.Created);
        await SetStateAsync("KUP-1", IssueState.Done);

        (await GetAsync<IssueTime>(_owner, $"{Ws}/issues/KUP-1/time")).Locked.ShouldBeTrue();
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-1", 30)), HttpStatusCode.Conflict, "time_locked");
        await ShouldBeProblemAsync(await _owner.PatchAsJsonAsync($"{Ws}/time/{entry.Id}", new UpdateTimeEntryRequest(Minutes: 45), Json.Options, Ct),
            HttpStatusCode.Conflict, "time_locked");
        await ShouldBeProblemAsync(await _owner.DeleteAsync($"{Ws}/time/{entry.Id}", Ct), HttpStatusCode.Conflict, "time_locked");

        await SetStateAsync("KUP-1", IssueState.InProgress);
        (await _owner.DeleteAsync($"{Ws}/time/{entry.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task One_save_logs_time_and_closes_the_issue_or_does_neither()
    {
        await TurnOnAsync();
        var entry = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 45, Yesterday, State: IssueState.Done)),
            HttpStatusCode.Created);
        entry.Locked.ShouldBeTrue();
        entry.Issue.State.ShouldBe(IssueState.Done);
        var feed = await GetAsync<List<FeedItem>>(_owner, $"{Ws}/issues/KUP-1/activity");
        feed.ShouldContain(i => i.Kind == FeedItemKind.Updated);

        // The day is full, so the entry fails, and the issue stays open.
        await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-2", 1395, Yesterday)), HttpStatusCode.Created);
        await ShouldBeProblemAsync(await LogAsync(_owner, new LogTimeRequest("KUP-2", 60, Yesterday, State: IssueState.Canceled)), HttpStatusCode.Conflict, "day_full");
        (await GetAsync<IssueDetail>(_owner, $"{Ws}/issues/KUP-2")).State.ShouldBe(IssueState.Backlog);
    }

    [Fact]
    public async Task Members_see_their_own_time_and_owners_see_everyones()
    {
        await TurnOnAsync();
        var (member, memberId) = await JoinAsync(WorkspaceRole.Member);
        var mine = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 60)), HttpStatusCode.Created);
        var theirs = await ReadAsync<TimeEntryResponse>(await LogAsync(member, new LogTimeRequest("KUP-1", 90)), HttpStatusCode.Created);

        var asMember = await GetAsync<IssueTime>(member, $"{Ws}/issues/KUP-1/time");
        (asMember.MyMinutes, asMember.TotalMinutes, asMember.People).ShouldBe((90, null, null));
        (await GetAsync<MyTime>(member, $"{Ws}/time/mine")).Entries.ShouldHaveSingleItem().Id.ShouldBe(theirs.Id);
        await ShouldBeProblemAsync(await member.DeleteAsync($"{Ws}/time/{mine.Id}", Ct), HttpStatusCode.NotFound, "time_entry_not_found");
        await ShouldBeProblemAsync(await member.GetAsync($"{Ws}/time/entries?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Ct),
            HttpStatusCode.Forbidden, "insufficient_role");
        await ShouldBeProblemAsync(await member.GetAsync($"{Ws}/time/changes?member={mine.Member.Id}", Ct), HttpStatusCode.Forbidden, "insufficient_role");

        var asOwner = await GetAsync<IssueTime>(_owner, $"{Ws}/issues/KUP-1/time");
        (asOwner.MyMinutes, asOwner.TotalMinutes).ShouldBe((60, 150));
        asOwner.People!.Select(p => (p.Member.Id, p.Minutes)).ShouldBe([(memberId, 90), (mine.Member.Id, 60)]);
        var all = await GetAsync<TimeEntryList>(_owner, $"{Ws}/time/entries?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        all.TotalMinutes.ShouldBe(150);
        (await GetAsync<List<TimeEntryChangeResponse>>(_owner, $"{Ws}/time/changes?member={memberId}")).ShouldHaveSingleItem();

        // Owners read the employee's record but don't change it.
        await ShouldBeProblemAsync(await _owner.DeleteAsync($"{Ws}/time/{theirs.Id}", Ct), HttpStatusCode.Forbidden, "not_your_entry");
    }

    [Fact]
    public async Task Guests_have_no_time()
    {
        await TurnOnAsync();
        var (guest, _) = await JoinAsync(WorkspaceRole.Guest);

        await ShouldBeProblemAsync(await LogAsync(guest, new LogTimeRequest("KUP-1", 60)), HttpStatusCode.Forbidden, "insufficient_role");
        await ShouldBeProblemAsync(await guest.GetAsync($"{Ws}/issues/KUP-1/time", Ct), HttpStatusCode.Forbidden, "insufficient_role");
    }

    [Fact]
    public async Task An_agent_logs_time_as_its_person_and_the_history_names_the_agent()
    {
        await TurnOnAsync();
        var agent = Factory.CreateBearerClient((await NewTokenAsync(_owner,
            new("bot", ApiTokenKind.Agent, "time-bot", null, _workspace.Slug))).Secret);

        var entry = await ReadAsync<TimeEntryResponse>(await LogAsync(agent, new LogTimeRequest("KUP-1", 30), Surface.Mcp), HttpStatusCode.Created);

        entry.Member.Kind.ShouldBe(MemberKind.Human);
        entry.Member.Name.ShouldBe("Test User");
        entry.Via.ShouldBe(Surface.Mcp);
        (await GetAsync<MyTime>(_owner, $"{Ws}/time/mine")).Entries.ShouldHaveSingleItem().Id.ShouldBe(entry.Id);
        var change = (await GetAsync<List<TimeEntryChangeResponse>>(_owner, $"{Ws}/time/changes")).ShouldHaveSingleItem();
        change.ChangedBy.Kind.ShouldBe(MemberKind.Agent);
        change.ChangedBy.Name.ShouldBe("time-bot");
    }

    [Fact]
    public async Task Owners_export_everyones_time_including_people_who_left()
    {
        await TurnOnAsync();
        var (member, memberId) = await JoinAsync(WorkspaceRole.Member);
        await ReadAsync<TimeEntryResponse>(await LogAsync(member, new LogTimeRequest("KUP-2", 75, Note: "Weekly, \"planning\"")), HttpStatusCode.Created);
        await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 60, Note: "=SUM(A1)")), HttpStatusCode.Created);
        (await _owner.PostAsync($"{Ws}/members/{memberId}/deactivate", null, Ct)).EnsureSuccessStatusCode();
        var range = $"from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";

        var csv = await _owner.GetAsync($"{Ws}/time/export?{range}", Ct);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var lines = (await csv.Content.ReadAsStringAsync(Ct)).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Trim().ShouldBe("date,member_id,member,email,issue,issue_title,minutes,hours,note,deactivated");
        lines.Length.ShouldBe(3);
        lines.ShouldContain(l => l.Contains("KUP-2,Meetings,75,1.25,\"Weekly, \"\"planning\"\"\",true", StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains(",'=SUM(A1),false", StringComparison.Ordinal));

        var json = await GetAsync<List<TimeExportRow>>(_owner, $"{Ws}/time/export?{range}&format=json&member={memberId}");
        json.ShouldHaveSingleItem().Deactivated.ShouldBeTrue();

        await ShouldBeProblemAsync(await _owner.GetAsync($"{Ws}/time/export?from=2025-01-01&to=2026-12-31", Ct), HttpStatusCode.BadRequest, "invalid_range");
    }

    [Fact]
    public async Task Entries_older_than_five_years_are_purged_by_the_day_they_record()
    {
        await TurnOnAsync();
        var old = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 60, Today.AddYears(-5).AddDays(-1))),
            HttpStatusCode.Created);
        var kept = await ReadAsync<TimeEntryResponse>(await LogAsync(_owner, new LogTimeRequest("KUP-1", 60, Today.AddYears(-5))),
            HttpStatusCode.Created);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
            (await TimeService.PurgeExpiredAsync(db, Today, Ct)).ShouldBeGreaterThanOrEqualTo(1);
        }

        var history = await GetAsync<List<TimeEntryChangeResponse>>(_owner, $"{Ws}/time/changes");
        history.ShouldNotContain(c => c.EntryId == old.Id);
        history.ShouldContain(c => c.EntryId == kept.Id);
    }

    private async Task TurnOnAsync() =>
        (await _owner.PatchAsJsonAsync(Ws, new UpdateWorkspaceRequest(TimeRegistration: true), Json.Options, Ct)).EnsureSuccessStatusCode();

    private async Task<HttpResponseMessage> LogAsync(HttpClient client, LogTimeRequest request, Surface? via = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Ws}/time") { Content = JsonContent.Create(request, options: Json.Options) };
        if (via is { } surface)
        {
            message.Headers.Add("X-Kupro-Client", surface.ToString().ToLowerInvariant());
        }

        return await client.SendAsync(message, Ct);
    }

    private async Task SetStateAsync(string key, IssueState state) =>
        (await _owner.PatchAsJsonAsync($"{Ws}/issues/{key}", new UpdateIssueRequest(State: state), Json.Options, Ct)).EnsureSuccessStatusCode();

    private async Task<(HttpClient Client, Guid MemberId)> JoinAsync(WorkspaceRole role)
    {
        var user = TestUser.New("Joiner");
        var client = Factory.CreateUserClient(user);
        var invited = await ReadAsync<MemberResponse>(await _owner.PostAsJsonAsync($"{Ws}/members",
            new InviteMemberRequest(user.Email, role), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<WorkspaceSummary>(await client.PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));
        return (client, invited.Id);
    }
}
