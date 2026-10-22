using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

public sealed class WorkspaceTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Creator_owns_the_workspace_and_takes_one_seat()
    {
        var (client, workspace, _) = await NewWorkspaceAsync("Acme");

        var mine = await GetAsync<List<WorkspaceSummary>>(client, "/api/v1/workspaces");
        var detail = await GetAsync<WorkspaceResponse>(client, $"/api/v1/workspaces/{workspace.Slug}");

        mine.ShouldHaveSingleItem().Role.ShouldBe(WorkspaceRole.Owner);
        detail.Name.ShouldBe("Acme");
        detail.HoursPerWeek.ShouldBe(37m);
        detail.Seats.ShouldBe(1);
    }

    [Fact]
    public async Task Slug_must_be_unique()
    {
        var (client, workspace, _) = await NewWorkspaceAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest("Again", workspace.Slug), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict, "slug_taken");
    }

    [Fact]
    public async Task User_in_workspace_A_cannot_read_workspace_B()
    {
        var (alice, a, _) = await NewWorkspaceAsync("A");
        var (bob, b, _) = await NewWorkspaceAsync("B");
        await CreateProjectAsync(alice, a.Slug, "KUP", "Alice's flow");
        await CreateProjectAsync(bob, b.Slug, "KUP", "Bob's flow");

        await ShouldBeProblemAsync(await bob.GetAsync($"/api/v1/workspaces/{a.Slug}", Ct), HttpStatusCode.NotFound, "workspace_not_found");
        await ShouldBeProblemAsync(await bob.GetAsync($"/api/v1/workspaces/{a.Slug}/projects", Ct), HttpStatusCode.NotFound, "workspace_not_found");
        await ShouldBeProblemAsync(await bob.GetAsync($"/api/v1/workspaces/{a.Slug}/projects/KUP", Ct), HttpStatusCode.NotFound, "workspace_not_found");
        await ShouldBeProblemAsync(await bob.GetAsync($"/api/v1/workspaces/{a.Slug}/members", Ct), HttpStatusCode.NotFound, "workspace_not_found");

        var aliceProjects = await GetAsync<List<ProjectResponse>>(alice, $"/api/v1/workspaces/{a.Slug}/projects");
        aliceProjects.ShouldHaveSingleItem().Name.ShouldBe("Alice's flow");
    }

    [Fact]
    public async Task Tenant_filter_hides_other_workspaces_and_everything_without_a_workspace()
    {
        var (alice, a, _) = await NewWorkspaceAsync("A");
        var (bob, b, _) = await NewWorkspaceAsync("B");
        await CreateProjectAsync(alice, a.Slug, "ONE", "In A");
        await CreateProjectAsync(bob, b.Slug, "TWO", "In B");

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(a.Id);
            var projects = await scope.ServiceProvider.GetRequiredService<ProjectService>().ListAsync(Ct);
            projects.ShouldHaveSingleItem().Key.ShouldBe("ONE");
        }

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var projects = await scope.ServiceProvider.GetRequiredService<ProjectService>().ListAsync(Ct);
            projects.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Project_key_is_unique_per_workspace()
    {
        var (client, workspace, _) = await NewWorkspaceAsync();
        await CreateProjectAsync(client, workspace.Slug, "KUP", "First");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/projects", new CreateProjectRequest("KUP", "Second"), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict, "project_key_taken");
    }

    [Fact]
    public async Task Project_key_can_change_while_project_has_no_issues()
    {
        var (client, workspace, _) = await NewWorkspaceAsync();
        await CreateProjectAsync(client, workspace.Slug, "KPR", "Kupro");

        var updated = await ReadAsync<ProjectResponse>(await client.PatchAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/projects/kpr", new UpdateProjectRequest(Key: "KUP"), Json.Options, Ct));

        updated.Key.ShouldBe("KUP");
    }

    [Fact]
    public async Task Invited_user_joins_on_accept_and_takes_a_seat()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var invitee = TestUser.New("Ida");
        using var ida = Factory.CreateUserClient(invitee);

        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members",
            new InviteMemberRequest(invitee.Email.ToUpperInvariant(), WorkspaceRole.Member), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await ShouldBeProblemAsync(await ida.GetAsync($"/api/v1/workspaces/{workspace.Slug}", Ct),
            HttpStatusCode.NotFound, "workspace_not_found");

        var invite = (await GetAsync<List<InviteResponse>>(ida, "/api/v1/invites")).ShouldHaveSingleItem();
        await ReadAsync<WorkspaceSummary>(await ida.PostAsync($"/api/v1/invites/{invite.MemberId}/accept", null, Ct));

        var detail = await GetAsync<WorkspaceResponse>(ida, $"/api/v1/workspaces/{workspace.Slug}");
        detail.Role.ShouldBe(WorkspaceRole.Member);
        detail.Seats.ShouldBe(2);
    }

    [Fact]
    public async Task Seats_count_only_active_human_owners_and_members()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest);
        var (memberClient, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);
        await NewTokenAsync(owner, new("bot", ApiTokenKind.Agent, "seat-bot", null, workspace.Slug));
        var leaving = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member);
        await owner.PostAsync($"/api/v1/workspaces/{workspace.Slug}/members/{leaving.MemberId}/deactivate", null, Ct);
        await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members",
            new InviteMemberRequest("pending@test.kupro", WorkspaceRole.Member), Json.Options, Ct);

        var detail = await GetAsync<WorkspaceResponse>(memberClient, $"/api/v1/workspaces/{workspace.Slug}");

        detail.Seats.ShouldBe(2); // owner + one active member; guest, agent, deactivated and invited don't count
    }

    [Fact]
    public async Task Guests_are_uncapped_without_GuestsPerSeat()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        for (var i = 0; i < 6; i++)
        {
            (await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members",
                new InviteMemberRequest(TestUser.New().Email, WorkspaceRole.Guest), Json.Options, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var detail = await GetAsync<WorkspaceResponse>(owner, $"/api/v1/workspaces/{workspace.Slug}");

        (detail.Seats, detail.Guests, detail.GuestLimit).ShouldBe((1, 6, (int?)null));
    }

    [Fact]
    public async Task Deactivated_member_loses_access_and_tokens_but_keeps_their_name()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (member, memberId) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Member, "Mona");
        var token = await NewTokenAsync(member, new("ci", ApiTokenKind.Personal, null, null, workspace.Slug));
        using var viaToken = Factory.CreateBearerClient(token.Secret);
        (await viaToken.GetAsync($"/api/v1/workspaces/{workspace.Slug}/projects", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await owner.PostAsync($"/api/v1/workspaces/{workspace.Slug}/members/{memberId}/deactivate", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldBeProblemAsync(await member.GetAsync($"/api/v1/workspaces/{workspace.Slug}/projects", Ct),
            HttpStatusCode.Forbidden, "member_deactivated");
        (await viaToken.GetAsync($"/api/v1/workspaces/{workspace.Slug}/projects", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var members = await GetAsync<List<MemberResponse>>(owner, $"/api/v1/workspaces/{workspace.Slug}/members");
        var mona = members.Single(m => m.Id == memberId);
        mona.Name.ShouldBe("Mona");
        mona.Status.ShouldBe(MemberStatus.Deactivated);
    }

    [Fact]
    public async Task Last_owner_cannot_be_deactivated_or_demoted()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var me = (await GetAsync<List<MemberResponse>>(owner, $"/api/v1/workspaces/{workspace.Slug}/members")).Single();

        await ShouldBeProblemAsync(
            await owner.PostAsync($"/api/v1/workspaces/{workspace.Slug}/members/{me.Id}/deactivate", null, Ct),
            HttpStatusCode.Conflict, "last_owner");
        await ShouldBeProblemAsync(
            await owner.PatchAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members/{me.Id}",
                new UpdateMemberRequest(WorkspaceRole.Member), Json.Options, Ct),
            HttpStatusCode.Conflict, "last_owner");
    }

    [Fact]
    public async Task Guest_cannot_create_projects()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (guest, _) = await JoinAsync(owner, workspace.Slug, WorkspaceRole.Guest);

        var response = await guest.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/projects", new CreateProjectRequest("NOPE", "Nope"), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "insufficient_role");
    }

    [Fact]
    public async Task Agent_works_in_its_workspace_as_a_member_but_cannot_administer_or_leave_it()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var (_, other, _) = await NewWorkspaceAsync();
        var token = await NewTokenAsync(owner, new("triage", ApiTokenKind.Agent, "triage-agent", null, workspace.Slug));
        using var agent = Factory.CreateBearerClient(token.Secret);

        await CreateProjectAsync(agent, workspace.Slug, "AGT", "Made by agent");
        await ShouldBeProblemAsync(
            await agent.PatchAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}", new UpdateWorkspaceRequest("Hacked"), Json.Options, Ct),
            HttpStatusCode.Forbidden, "insufficient_role");
        await ShouldBeProblemAsync(await agent.GetAsync($"/api/v1/workspaces/{other.Slug}", Ct),
            HttpStatusCode.NotFound, "workspace_not_found");

        var members = await GetAsync<List<MemberResponse>>(owner, $"/api/v1/workspaces/{workspace.Slug}/members");
        var agentMember = members.Single(m => m.Kind == MemberKind.Agent);
        agentMember.Name.ShouldBe("triage-agent");
        agentMember.OwnerName.ShouldBe("Test User");
    }

    [Fact]
    public async Task Workspace_scoped_token_only_works_in_that_workspace()
    {
        var (client, first, user) = await NewWorkspaceAsync();
        var slug = "w-" + Guid.NewGuid().ToString("N")[..12];
        await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest("Second", slug), Json.Options, Ct);
        var token = await NewTokenAsync(client, new("scoped", ApiTokenKind.Personal, null, null, first.Slug));
        using var viaToken = Factory.CreateBearerClient(token.Secret);

        (await viaToken.GetAsync($"/api/v1/workspaces/{first.Slug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeProblemAsync(await viaToken.GetAsync($"/api/v1/workspaces/{slug}", Ct),
            HttpStatusCode.NotFound, "workspace_not_found");
        user.ShouldNotBeNull();
    }

    private static async Task CreateProjectAsync(HttpClient client, string slug, string key, string name) =>
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{slug}/projects", new CreateProjectRequest(key, name), Json.Options, Ct), HttpStatusCode.Created);

    /// <summary>Invites a new user with the given role and accepts on their behalf.</summary>
    private async Task<(HttpClient Client, Guid MemberId)> JoinAsync(
        HttpClient owner, string slug, WorkspaceRole role, string name = "Joiner")
    {
        var user = TestUser.New(name);
        var client = Factory.CreateUserClient(user);
        var invited = await ReadAsync<MemberResponse>(await owner.PostAsJsonAsync(
            $"/api/v1/workspaces/{slug}/members", new InviteMemberRequest(user.Email, role), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<WorkspaceSummary>(await client.PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));
        return (client, invited.Id);
    }
}
