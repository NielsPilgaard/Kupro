using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Users;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

public sealed class AuthTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Anonymous_request_is_rejected()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task First_jwt_request_provisions_the_user_once()
    {
        var user = TestUser.New("Ada Lovelace");
        using var client = Factory.CreateUserClient(user);

        var first = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);
        var second = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);

        first.ShouldNotBeNull();
        first.Email.ShouldBe(user.Email);
        first.DisplayName.ShouldBe("Ada Lovelace");
        first.Locale.ShouldBe("en");
        first.Agent.ShouldBeNull();
        second!.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task New_user_gets_the_browser_language()
    {
        using var client = Factory.CreateUserClient(TestUser.New());
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("da-DK,da;q=0.9,en;q=0.8");

        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);

        me!.Locale.ShouldBe("da");
    }

    [Fact]
    public async Task Personal_token_authenticates_as_the_user()
    {
        using var human = Factory.CreateUserClient(TestUser.New());
        var me = await human.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);
        var created = await NewTokenAsync(human, new("laptop", ApiTokenKind.Personal, null, null));

        created.Secret.ShouldStartWith("kp_pat_");
        created.Token.Hint.ShouldEndWith(created.Secret[^4..]);

        using var viaToken = Factory.CreateBearerClient(created.Secret);
        var meViaToken = await viaToken.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);
        meViaToken!.Id.ShouldBe(me!.Id);
        meViaToken.Agent.ShouldBeNull();
    }

    [Fact]
    public async Task Revoked_token_is_rejected_immediately()
    {
        using var human = Factory.CreateUserClient(TestUser.New());
        var created = await NewTokenAsync(human, new("ci", ApiTokenKind.Personal, null, null));
        using var viaToken = Factory.CreateBearerClient(created.Secret);
        (await viaToken.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var revoke = await human.DeleteAsync($"/api/v1/tokens/{created.Token.Id}", Ct);

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await viaToken.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var remaining = await human.GetFromJsonAsync<List<ApiTokenResponse>>("/api/v1/tokens", Json.Options, Ct);
        remaining.ShouldNotBeNull().ShouldNotContain(t => t.Id == created.Token.Id);
    }

    [Fact]
    public async Task Agent_token_acts_on_behalf_of_user_but_cannot_manage_tokens()
    {
        var (human, workspace, _) = await NewWorkspaceAsync();
        var created = await NewTokenAsync(human, new("triage", ApiTokenKind.Agent, "triage-agent", 30, workspace.Slug));
        created.Secret.ShouldStartWith("kp_agent_");

        using var agent = Factory.CreateBearerClient(created.Secret);
        var me = await agent.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);
        me!.Agent.ShouldBe("triage-agent");

        var response = await agent.GetAsync("/api/v1/tokens", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Workspace_scoped_token_cannot_act_for_the_whole_account()
    {
        var (human, workspace, _) = await NewWorkspaceAsync();
        await NewWorkspaceInAsync(human);
        var scoped = await NewTokenAsync(human, new("ci", ApiTokenKind.Personal, null, 7, workspace.Slug));
        using var viaToken = Factory.CreateBearerClient(scoped.Secret);

        var mine = await viaToken.GetFromJsonAsync<List<WorkspaceSummary>>("/api/v1/workspaces", Json.Options, Ct);
        mine.ShouldNotBeNull().ShouldHaveSingleItem().Slug.ShouldBe(workspace.Slug);
        (await viaToken.PostAsJsonAsync("/api/v1/tokens", new CreateApiTokenRequest("x", ApiTokenKind.Personal), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viaToken.GetAsync("/api/v1/tokens", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viaToken.GetAsync("/api/v1/invites", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viaToken.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest("x", "w-" + Guid.NewGuid().ToString("N")[..12]), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_cannot_create_a_token_that_outlives_it()
    {
        using var human = Factory.CreateUserClient(TestUser.New());
        var expiring = await NewTokenAsync(human, new("ci", ApiTokenKind.Personal, null, 7));
        using var viaToken = Factory.CreateBearerClient(expiring.Secret);

        await ShouldBeProblemAsync(await viaToken.PostAsJsonAsync(
                "/api/v1/tokens", new CreateApiTokenRequest("forever", ApiTokenKind.Personal), Json.Options, Ct),
            HttpStatusCode.Forbidden, "token_outlives_caller");
        await ShouldBeProblemAsync(await viaToken.PostAsJsonAsync(
                "/api/v1/tokens", new CreateApiTokenRequest("longer", ApiTokenKind.Personal, null, 30), Json.Options, Ct),
            HttpStatusCode.Forbidden, "token_outlives_caller");
        await NewTokenAsync(viaToken, new("shorter", ApiTokenKind.Personal, null, 1));
    }

    [Fact]
    public async Task Email_from_a_client_that_is_not_ours_does_not_open_invites()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var invitee = TestUser.New("Ida");
        var invite = await ReadAsync<MemberResponse>(await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members",
            new InviteMemberRequest(invitee.Email, WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.Created);

        // Another account whose self-registered client claims Ida's email as verified.
        using var forger = Factory.CreateUserClient(TestUser.New() with { Email = invitee.Email }, clientId: "dcr-client");

        (await forger.GetFromJsonAsync<List<InviteResponse>>("/api/v1/invites", Json.Options, Ct)).ShouldNotBeNull().ShouldBeEmpty();
        await ShouldBeProblemAsync(await forger.PostAsync($"/api/v1/invites/{invite.Id}/accept", null, Ct),
            HttpStatusCode.NotFound, "invite_not_found");
    }

    [Fact]
    public async Task Cannot_revoke_another_users_token()
    {
        using var owner = Factory.CreateUserClient(TestUser.New());
        using var other = Factory.CreateUserClient(TestUser.New());
        var created = await NewTokenAsync(owner, new("mine", ApiTokenKind.Personal, null, null));

        var response = await other.DeleteAsync($"/api/v1/tokens/{created.Token.Id}", Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.NotFound, "token_not_found");
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        using var client = Factory.CreateBearerClient("kp_pat_doesnotexist");

        var response = await client.GetAsync("/api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agent_token_without_agent_name_is_a_validation_problem()
    {
        using var human = Factory.CreateUserClient(TestUser.New());

        var response = await human.PostAsJsonAsync(
            "/api/v1/tokens", new CreateApiTokenRequest("x", ApiTokenKind.Agent, null, null), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task User_can_change_language_and_time_zone()
    {
        using var client = Factory.CreateUserClient(TestUser.New());

        var response = await client.PatchAsJsonAsync(
            "/api/v1/me", new UpdateMeRequest(null, "da", "Europe/Copenhagen"), Json.Options, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(Json.Options, Ct);
        me!.Locale.ShouldBe("da");
    }

    private static async Task NewWorkspaceInAsync(HttpClient client) =>
        (await client.PostAsJsonAsync("/api/v1/workspaces",
            new CreateWorkspaceRequest("Other", "w-" + Guid.NewGuid().ToString("N")[..12]), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
}
