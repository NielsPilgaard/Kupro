using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kupro.Tests.Features;

/// <summary>The plan cache (task 030, ADR-010 IMP-003): served from memory, never stale after a write, never across workspaces.</summary>
public sealed class PlanCacheTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly DateOnly Jan = new(2027, 1, 1);
    private static readonly DateOnly Jun = new(2027, 6, 30);
    private const string Horizon = "?from=2027-01-01&to=2027-12-01";
    private readonly PostgresFixture _postgres = postgres;
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await CreateProjectAsync(_client, Ws, "ATL");
    }

    [Fact]
    public async Task A_second_read_comes_from_the_cache()
    {
        await PlanJsonAsync();
        await ChangeBehindTheApiAsync(_workspace.Id, 12m);

        // The database says 12 now. Seeing 37 proves this read didn't query; the write below proves it can be invalidated.
        (await PlanAsync()).HoursPerWeek.ShouldBe(37m);

        await ReadAsync<WorkspaceResponse>(await _client.PatchAsJsonAsync(Ws, new UpdateWorkspaceRequest(Name: "Renamed"), Json.Options, Ct));
        (await PlanAsync()).HoursPerWeek.ShouldBe(12m);
    }

    [Fact]
    public async Task Every_plan_affecting_write_shows_on_the_very_next_read()
    {
        var epic = await CreateAsync(new("ATL", "Epic", IssueType.Epic, Effort: 10, StartDate: Jan, DueDate: Jun));
        var feature = await CreateAsync(new("ATL", "Feature", IssueType.Feature, Effort: 20, StartDate: Jan, DueDate: Jun));
        var relationId = Guid.Empty;
        var milestoneId = Guid.Empty;
        var sprint = await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("ATL", "One", Jan, Jan.AddDays(13)), Json.Options, Ct), HttpStatusCode.Created);
        var task = await CreateAsync(new("ATL", "Child", Parent: feature.Key, State: IssueState.Todo, Effort: 3, Sprint: sprint.Id.ToString()));
        (await _client.PostAsync($"{Ws}/sprints/{sprint.Id}/start", null, Ct)).EnsureSuccessStatusCode();

        var writes = new (string Name, Func<Task<HttpResponseMessage>> Write)[]
        {
            ("create issue", () => _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("ATL", "New epic", IssueType.Epic), Json.Options, Ct)),
            ("create issues in bulk", () => _client.PostAsJsonAsync($"{Ws}/issues/bulk", new CreateIssuesRequest([new("ATL", "Bulk epic", IssueType.Epic)]), Json.Options, Ct)),
            ("change effort", () => PatchIssueAsync(epic.Key, new(Effort: 11))),
            ("change dates", () => PatchIssueAsync(epic.Key, new(DueDate: Jun.AddDays(-1)))),
            ("change state", () => PatchIssueAsync(epic.Key, new(State: IssueState.Todo))),
            ("move to another state", () => _client.PostAsJsonAsync($"{Ws}/issues/{epic.Key}/move", new MoveIssueRequest(IssueState.InProgress), Json.Options, Ct)),
            ("update issues in bulk", () => _client.PatchAsJsonAsync($"{Ws}/issues/bulk",
                new UpdateIssuesRequest([new(epic.Key, new UpdateIssueRequest(Effort: 12))]), Json.Options, Ct)),
            ("add a relation", async () =>
            {
                var response = await _client.PostAsJsonAsync($"{Ws}/issues/{epic.Key}/relations", new AddRelationRequest(RelationKind.Blocks, feature.Key), Json.Options, Ct);
                relationId = (await ReadAsync<IssueRelationResponse>(response, HttpStatusCode.Created)).Id;
                return response;
            }),
            ("remove a relation", () => _client.DeleteAsync($"{Ws}/issues/{epic.Key}/relations/{relationId}", Ct)),
            ("set capacity", () => _client.PutAsJsonAsync($"{Ws}/projects/ATL/capacity", new SetCapacityRequest([new(Jan, 2)]), Json.Options, Ct)),
            ("create a milestone", async () =>
            {
                var response = await _client.PostAsJsonAsync($"{Ws}/milestones", new CreateMilestoneRequest("Launch", Jun), Json.Options, Ct);
                milestoneId = (await ReadAsync<MilestoneResponse>(response, HttpStatusCode.Created)).Id;
                return response;
            }),
            ("update a milestone", () => _client.PatchAsJsonAsync($"{Ws}/milestones/{milestoneId}", new UpdateMilestoneRequest(Name: "Launch 2"), Json.Options, Ct)),
            ("delete a milestone", () => _client.DeleteAsync($"{Ws}/milestones/{milestoneId}", Ct)),
            ("close a sprint", () => _client.PostAsJsonAsync($"{Ws}/sprints/{sprint.Id}/close",
                new CloseSprintRequest(Items: [new(task.Key, CloseOutcome.Done)]), Json.Options, Ct)),
            ("change hours per week", () => _client.PatchAsJsonAsync(Ws, new UpdateWorkspaceRequest(HoursPerWeek: 30), Json.Options, Ct)),
            ("rename a project", () => _client.PatchAsJsonAsync($"{Ws}/projects/ATL", new UpdateProjectRequest(Name: "Atlas"), Json.Options, Ct)),
            ("recolour a project", () => _client.PatchAsJsonAsync($"{Ws}/projects/ATL", new UpdateProjectRequest(Color: ProjectColor.Plum), Json.Options, Ct)),
            ("create a project", () => _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("NEW", "New"), Json.Options, Ct)),
        };

        foreach (var (name, write) in writes)
        {
            var before = await PlanJsonAsync();
            (await write()).IsSuccessStatusCode.ShouldBeTrue(name);
            (await PlanJsonAsync()).ShouldNotBe(before, name);
        }
    }

    [Fact]
    public async Task Views_share_a_cached_plan_without_changing_it()
    {
        await CreateAsync(new("ATL", "Epic", IssueType.Epic, Effort: 10, StartDate: Jan, DueDate: Jun));

        var full = await PlanJsonAsync();
        var summary = await GetAsync<PlanResponse>(_client, $"{Ws}/plan{Horizon}&view=summary");

        summary.Projects.ShouldHaveSingleItem().Cards.ShouldBeEmpty();
        summary.Projects[0].Months.ShouldBeEmpty();
        (await GetAsync<PlanResponse>(_client, $"{Ws}/plan{Horizon}&view=summary&project=ATL")).Projects[0].Months.ShouldNotBeEmpty();
        (await PlanJsonAsync()).ShouldBe(full);
    }

    [Fact]
    public async Task Project_keys_in_any_order_or_case_use_one_entry()
    {
        await CreateProjectAsync(_client, Ws, "BOR");
        await PlanJsonAsync("&project=ATL&project=BOR");
        await ChangeBehindTheApiAsync(_workspace.Id, 12m);

        (await GetAsync<PlanResponse>(_client, $"{Ws}/plan{Horizon}&project=bor&project=atl")).HoursPerWeek.ShouldBe(37m);
        // Another selection is another entry, so it reads the database.
        (await GetAsync<PlanResponse>(_client, $"{Ws}/plan{Horizon}&project=ATL")).HoursPerWeek.ShouldBe(12m);
    }

    [Fact]
    public async Task A_cached_plan_is_never_served_to_another_workspace()
    {
        var (theirClient, theirWorkspace, _) = await NewWorkspaceAsync();
        var theirs = $"/api/v1/workspaces/{theirWorkspace.Slug}";
        await CreateProjectAsync(theirClient, theirs, "ATL", "Their atlas");

        var mine = await GetAsync<PlanResponse>(_client, $"{Ws}/plan{Horizon}");
        var other = await GetAsync<PlanResponse>(theirClient, $"{theirs}/plan{Horizon}");

        mine.Projects.ShouldHaveSingleItem().Name.ShouldBe("ATL");
        other.Projects.ShouldHaveSingleItem().Name.ShouldBe("Their atlas");

        // And a write in one workspace leaves the other's entry alone.
        await ChangeBehindTheApiAsync(theirWorkspace.Id, 12m);
        await ReadAsync<ProjectResponse>(await _client.PatchAsJsonAsync($"{Ws}/projects/ATL", new UpdateProjectRequest(Name: "Mine"), Json.Options, Ct));
        (await GetAsync<PlanResponse>(theirClient, $"{theirs}/plan{Horizon}")).HoursPerWeek.ShouldBe(37m);
    }

    [Fact]
    public async Task A_deactivated_member_is_refused_while_the_plan_is_cached()
    {
        var (member, memberId) = await JoinAsMemberAsync();
        await GetAsync<PlanResponse>(member, $"{Ws}/plan{Horizon}");

        (await _client.PostAsync($"{Ws}/members/{memberId}/deactivate", null, Ct)).EnsureSuccessStatusCode();

        await ShouldBeProblemAsync(await member.GetAsync($"{Ws}/plan{Horizon}", Ct), HttpStatusCode.Forbidden, "member_deactivated");
    }

    [Fact]
    public async Task A_revoked_token_is_refused_while_the_plan_is_cached()
    {
        var token = await NewTokenAsync(_client, new("cli", ApiTokenKind.Personal));
        using var viaToken = Factory.CreateBearerClient(token.Secret);
        await GetAsync<PlanResponse>(viaToken, $"{Ws}/plan{Horizon}");

        (await _client.DeleteAsync($"/api/v1/tokens/{token.Token.Id}", Ct)).EnsureSuccessStatusCode();

        (await viaToken.GetAsync($"{Ws}/plan{Horizon}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_write_during_a_refresh_is_not_overwritten_by_old_data()
    {
        await using var scope = RequestScope();
        var cache = scope.ServiceProvider.GetRequiredService<WorkspaceCache>();
        var gate = new Gate();

        var refresh = cache.GetOrSetAsync("key", gate, static async (_, gate, _) =>
        {
            gate.Started.SetResult();
            await gate.Release.Task;
            return "read before the write";
        }, static _ => 1, Ct);
        await gate.Started.Task;

        cache.Invalidate();
        gate.Release.SetResult();

        (await refresh).ShouldBe("read before the write"); // That request began before the write: fine for it to see old data.
        (await cache.GetOrSetAsync("key", 0, static (_, _, _) => Task.FromResult("read after the write"), static _ => 1, Ct))
            .ShouldBe("read after the write");
    }

    [Fact]
    public async Task A_factory_runs_correctly_after_the_requests_scope_is_disposed()
    {
        await CreateAsync(new("ATL", "Epic", IssueType.Epic, Effort: 10, StartDate: Jan, DueDate: Jun));
        var gate = new Gate();
        var request = RequestScope();
        var cache = request.ServiceProvider.GetRequiredService<WorkspaceCache>();

        var pending = cache.GetOrSetAsync("plan-after-request", gate, static async (services, gate, ct) =>
        {
            gate.Started.SetResult();
            await gate.Release.Task;
            return await services.GetRequiredService<PlanService>().BuildPlanAsync(Jan, new DateOnly(2027, 12, 1), [], ct);
        }, static _ => 1, Ct);
        await gate.Started.Task;

        await request.DisposeAsync();
        gate.Release.SetResult();

        (await pending).Projects.ShouldHaveSingleItem().Cards.ShouldHaveSingleItem().Title.ShouldBe("Epic");
    }

    [Fact]
    public async Task A_factory_sees_a_pinned_scope_of_its_own_and_no_request_state()
    {
        await using var scope = RequestScope();
        var requestContext = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
        var cache = scope.ServiceProvider.GetRequiredService<WorkspaceCache>();
        RequestState.Value = "from the request";

        var (context, state) = await cache.GetOrSetAsync("detached", 0,
            static (services, _, _) => Task.FromResult((services.GetRequiredService<WorkspaceContext>(), RequestState.Value)), static _ => 1, Ct);

        context.ShouldNotBeSameAs(requestContext);
        context.WorkspaceId.ShouldBe(_workspace.Id);
        state.ShouldBeNull();
    }

    private static readonly AsyncLocal<string?> RequestState = new();

    private sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private AsyncServiceScope RequestScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        return scope;
    }

    private async Task<(HttpClient Client, Guid MemberId)> JoinAsMemberAsync()
    {
        var user = TestUser.New();
        var client = Factory.CreateUserClient(user);
        await client.GetAsync("/api/v1/me", Ct);
        var invited = await ReadAsync<MemberResponse>(await _client.PostAsJsonAsync($"{Ws}/members",
            new InviteMemberRequest(user.Email, WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<WorkspaceSummary>(await client.PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));
        return (client, invited.Id);
    }

    /// <summary>Changes what the plan reads without going through the API, so nothing invalidates.</summary>
    private async Task ChangeBehindTheApiAsync(Guid workspaceId, decimal hoursPerWeek)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand("UPDATE workspaces SET hours_per_week = @hours WHERE id = @id", connection);
        update.Parameters.AddWithValue("hours", hoursPerWeek);
        update.Parameters.AddWithValue("id", workspaceId);
        (await update.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static async Task CreateProjectAsync(HttpClient client, string ws, string key, string? name = null) =>
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"{ws}/projects", new CreateProjectRequest(key, name ?? key), Json.Options, Ct),
            HttpStatusCode.Created);

    private async Task<IssueSummary> CreateAsync(CreateIssueRequest issue) =>
        await ReadAsync<IssueSummary>(await _client.PostAsJsonAsync($"{Ws}/issues", issue, Json.Options, Ct), HttpStatusCode.Created);

    private Task<HttpResponseMessage> PatchIssueAsync(string key, UpdateIssueRequest changes) =>
        _client.PatchAsJsonAsync($"{Ws}/issues/{key}", changes, Json.Options, Ct);

    private Task<PlanResponse> PlanAsync() => GetAsync<PlanResponse>(_client, $"{Ws}/plan{Horizon}");

    private async Task<string> PlanJsonAsync(string query = "") =>
        JsonSerializer.Serialize(await GetAsync<JsonElement>(_client, $"{Ws}/plan{Horizon}{query}"));
}
