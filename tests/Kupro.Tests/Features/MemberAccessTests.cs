using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Billing access and the guest cap (task 018, ADR-013), with hosted Kupro's 5 guests per seat.</summary>
public sealed class MemberAccessTests(PostgresFixture postgres)
    : ApiTestBase(postgres, new Dictionary<string, string?> { ["Members:GuestsPerSeat"] = "5" })
{
    private WebApplicationFactory<Program>? _withProbe;

    [Fact]
    public async Task Owner_grants_and_revokes_billing_access_and_others_cannot()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (member, memberId, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);
        var (guest, guestId, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest);

        await UpdateAsync(owner, workspace.Slug, memberId, new(CanManageBilling: true));
        (await GetAsync<WorkspaceResponse>(member, $"/api/v1/workspaces/{workspace.Slug}")).CanManageBilling.ShouldBeTrue();
        (await MembersAsync(owner, workspace.Slug)).Single(m => m.Id == memberId).CanManageBilling.ShouldBeTrue();

        await UpdateAsync(owner, workspace.Slug, memberId, new(CanManageBilling: false));
        (await GetAsync<WorkspaceResponse>(member, $"/api/v1/workspaces/{workspace.Slug}")).CanManageBilling.ShouldBeFalse();
        (await GetAsync<WorkspaceResponse>(owner, $"/api/v1/workspaces/{workspace.Slug}")).CanManageBilling.ShouldBeTrue();

        foreach (var client in new[] { member, guest })
        {
            await ShouldBeProblemAsync(await client.PatchAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members/{guestId}",
                new UpdateMemberRequest(CanManageBilling: true), Json.Options, Ct), HttpStatusCode.Forbidden, "insufficient_role");
        }
    }

    [Fact]
    public async Task Guest_with_billing_access_passes_the_guard_takes_no_seat_and_cannot_edit_issues()
    {
        var (owner, workspace, ownerUser) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/projects",
            new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct), HttpStatusCode.Created);
        var (guest, _, guestUser) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest, billing: true);
        var (_, _, memberUser) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);

        (await ProbeAsync(KuproApiFactory.CreateJwt(guestUser), workspace.Slug)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ProbeAsync(KuproApiFactory.CreateJwt(ownerUser), workspace.Slug)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldBeProblemAsync(await ProbeAsync(KuproApiFactory.CreateJwt(memberUser), workspace.Slug),
            HttpStatusCode.Forbidden, "billing_access_required");

        var detail = await GetAsync<WorkspaceResponse>(guest, $"/api/v1/workspaces/{workspace.Slug}");
        detail.CanManageBilling.ShouldBeTrue();
        detail.Seats.ShouldBe(2); // owner + member; the billing guest is free
        detail.Guests.ShouldBe(1);
        detail.GuestLimit.ShouldBe(10);
        await ShouldBeProblemAsync(await guest.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/issues",
            new CreateIssueRequest("KUP", "No"), Json.Options, Ct), HttpStatusCode.Forbidden, "insufficient_role");
    }

    [Fact]
    public async Task Agent_tokens_never_pass_billing_access_even_for_an_owner_or_billing_member()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (member, _, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member, billing: true);
        var ownerAgent = await NewTokenAsync(owner, new("bot", ApiTokenKind.Agent, "owner-bot", null, workspace.Slug));
        var memberAgent = await NewTokenAsync(member, new("bot", ApiTokenKind.Agent, "member-bot", null, workspace.Slug));

        foreach (var token in new[] { ownerAgent, memberAgent })
        {
            await ShouldBeProblemAsync(await ProbeAsync(token.Secret, workspace.Slug), HttpStatusCode.Forbidden, "billing_access_required");
        }
    }

    [Fact]
    public async Task Billing_access_on_an_agent_is_refused()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        await NewTokenAsync(owner, new("bot", ApiTokenKind.Agent, "triage-agent", null, workspace.Slug));
        var agent = (await MembersAsync(owner, workspace.Slug)).Single(m => m.Kind == MemberKind.Agent);

        await ShouldBeProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members/{agent.Id}",
            new UpdateMemberRequest(CanManageBilling: true), Json.Options, Ct), HttpStatusCode.BadRequest, "billing_access_humans_only");
    }

    [Fact]
    public async Task Sixth_guest_for_one_seat_is_refused_counting_pending_invites()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest);
        await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest);
        for (var i = 0; i < 3; i++)
        {
            (await InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var refused = await InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest);

        using var problem = await ProblemAsync(refused, HttpStatusCode.Conflict, "guest_limit_reached");
        problem.RootElement.GetProperty("guests").GetInt32().ShouldBe(5);
        problem.RootElement.GetProperty("guestLimit").GetInt32().ShouldBe(5);
        (await InviteAsync(owner, workspace.Slug, WorkspaceRole.Member)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var detail = await GetAsync<WorkspaceResponse>(owner, $"/api/v1/workspaces/{workspace.Slug}");
        (detail.Seats, detail.Guests, detail.GuestLimit).ShouldBe((1, 5, (int?)5));
    }

    [Fact]
    public async Task Parallel_invites_for_the_last_guest_place_let_exactly_one_through()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        for (var i = 0; i < 4; i++)
        {
            (await InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var responses = await Task.WhenAll(
            InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest),
            InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await GetAsync<WorkspaceResponse>(owner, $"/api/v1/workspaces/{workspace.Slug}")).Guests.ShouldBe(5);
    }

    [Fact]
    public async Task Over_the_cap_after_a_seat_goes_existing_guests_stay_and_new_ones_are_refused()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (_, memberId, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);
        var guests = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
        {
            guests.Add((await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest)).Client);
        }

        await owner.PostAsync($"/api/v1/workspaces/{workspace.Slug}/members/{memberId}/deactivate", null, Ct);

        foreach (var guest in guests)
        {
            (await guest.GetAsync($"/api/v1/workspaces/{workspace.Slug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await ShouldBeProblemAsync(await InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest),
            HttpStatusCode.Conflict, "guest_limit_reached");
        var detail = await GetAsync<WorkspaceResponse>(owner, $"/api/v1/workspaces/{workspace.Slug}");
        (detail.Guests, detail.GuestLimit).ShouldBe((6, (int?)5));
    }

    [Fact]
    public async Task Demoting_to_guest_counts_as_a_new_guest()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (_, memberId, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);
        for (var i = 0; i < 5; i++)
        {
            (await InviteAsync(owner, workspace.Slug, WorkspaceRole.Guest)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // Two seats allow 10 guests, but as a guest the member leaves one seat: 6 guests for a cap of 5.
        await ShouldBeProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members/{memberId}",
            new UpdateMemberRequest(WorkspaceRole.Guest), Json.Options, Ct), HttpStatusCode.Conflict, "guest_limit_reached");
        (await MembersAsync(owner, workspace.Slug)).Single(m => m.Id == memberId).Role.ShouldBe(WorkspaceRole.Member);
    }

    [Fact]
    public async Task Pending_guest_invites_past_the_cap_wait_their_turn_to_accept()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (_, memberId, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);
        var invited = new List<(TestUser User, Guid Id)>();
        for (var i = 0; i < 6; i++)
        {
            var user = TestUser.New();
            var invite = await ReadAsync<MemberResponse>(await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members",
                new InviteMemberRequest(user.Email, WorkspaceRole.Guest), Json.Options, Ct), HttpStatusCode.Created);
            invited.Add((user, invite.Id));
        }

        await owner.PostAsync($"/api/v1/workspaces/{workspace.Slug}/members/{memberId}/deactivate", null, Ct);

        using var last = Factory.CreateUserClient(invited[5].User);
        await ShouldBeProblemAsync(await last.PostAsync($"/api/v1/invites/{invited[5].Id}/accept", null, Ct),
            HttpStatusCode.Conflict, "guest_limit_reached");
        using var first = Factory.CreateUserClient(invited[0].User);
        await ReadAsync<WorkspaceSummary>(await first.PostAsync($"/api/v1/invites/{invited[0].Id}/accept", null, Ct));
    }

    private static Task<HttpResponseMessage> InviteAsync(HttpClient owner, string slug, WorkspaceRole role) =>
        owner.PostAsJsonAsync($"/api/v1/workspaces/{slug}/members",
            new InviteMemberRequest(TestUser.New().Email, role), Json.Options, Ct);

    private static async Task UpdateAsync(HttpClient owner, string slug, Guid memberId, UpdateMemberRequest request) =>
        (await owner.PatchAsJsonAsync($"/api/v1/workspaces/{slug}/members/{memberId}", request, Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static Task<List<MemberResponse>> MembersAsync(HttpClient client, string slug) =>
        GetAsync<List<MemberResponse>>(client, $"/api/v1/workspaces/{slug}/members");

    private static async Task<JsonDocument> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(code);
        return doc;
    }

    /// <summary>Calls <see cref="BillingProbeController"/>, which only this class's API instance serves.</summary>
    private async Task<HttpResponseMessage> ProbeAsync(string bearer, string slug)
    {
        _withProbe ??= Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(BillingProbeController).Assembly)));
        using var client = _withProbe.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.GetAsync($"/api/v1/workspaces/{slug}/billing-probe", Ct);
    }

    /// <summary>Invites a new user with the given role and accepts on their behalf.</summary>
    private async Task<(HttpClient Client, Guid MemberId, TestUser User)> JoinAsync(
        HttpClient owner, string slug, WorkspaceRole role, bool billing = false)
    {
        var user = TestUser.New("Joiner");
        var client = Factory.CreateUserClient(user);
        var invited = await ReadAsync<MemberResponse>(await owner.PostAsJsonAsync($"/api/v1/workspaces/{slug}/members",
            new InviteMemberRequest(user.Email, role, billing), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<WorkspaceSummary>(await client.PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));
        return (client, invited.Id, user);
    }
}

/// <summary>Stands in for the billing endpoints a later task adds; guarded by <see cref="BillingAccessAttribute"/>.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/billing-probe")]
public sealed class BillingProbeController : ControllerBase
{
    [HttpGet]
    [BillingAccess]
    public IActionResult Get() => NoContent();
}
