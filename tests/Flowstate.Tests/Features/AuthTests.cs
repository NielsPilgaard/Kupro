using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowstate.Api.Features.Tokens;
using Flowstate.Api.Features.Users;
using Flowstate.Tests.Infrastructure;

namespace Flowstate.Tests.Features;

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
        var created = await CreateTokenAsync(human, new("laptop", ApiTokenKind.Personal, null, null));

        created.Secret.ShouldStartWith("fs_pat_");
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
        var created = await CreateTokenAsync(human, new("ci", ApiTokenKind.Personal, null, null));
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
        using var human = Factory.CreateUserClient(TestUser.New());
        var created = await CreateTokenAsync(human, new("triage", ApiTokenKind.Agent, "triage-agent", 30));
        created.Secret.ShouldStartWith("fs_agent_");

        using var agent = Factory.CreateBearerClient(created.Secret);
        var me = await agent.GetFromJsonAsync<MeResponse>("/api/v1/me", Json.Options, Ct);
        me!.Agent.ShouldBe("triage-agent");

        var response = await agent.GetAsync("/api/v1/tokens", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cannot_revoke_another_users_token()
    {
        using var owner = Factory.CreateUserClient(TestUser.New());
        using var other = Factory.CreateUserClient(TestUser.New());
        var created = await CreateTokenAsync(owner, new("mine", ApiTokenKind.Personal, null, null));

        var response = await other.DeleteAsync($"/api/v1/tokens/{created.Token.Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemCodeAsync(response)).ShouldBe("token_not_found");
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        using var client = Factory.CreateBearerClient("fs_pat_doesnotexist");

        var response = await client.GetAsync("/api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agent_token_without_agent_name_is_a_validation_problem()
    {
        using var human = Factory.CreateUserClient(TestUser.New());

        var response = await human.PostAsJsonAsync(
            "/api/v1/tokens", new CreateApiTokenRequest("x", ApiTokenKind.Agent, null, null), Json.Options, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemCodeAsync(response)).ShouldBe("validation");
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

    private static async Task<CreatedApiTokenResponse> CreateTokenAsync(HttpClient client, CreateApiTokenRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tokens", request, Json.Options, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(Json.Options, Ct))!;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("code").GetString();
    }
}
