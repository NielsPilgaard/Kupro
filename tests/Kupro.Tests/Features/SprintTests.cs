using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

public sealed class SprintTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
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
    public async Task Full_cycle_plan_start_fill_close_moves_unfinished_to_next_sprint()
    {
        var one = await CreateSprintAsync("Sprint 1", 0);
        var two = await CreateSprintAsync("Sprint 2", 14);
        await ReadAsync<SprintResponse>(await _client.PostAsync($"{Ws}/sprints/{one.Id}/start", null, Ct));

        var done = await CreateIssueAsync(new("SPR", "Done work", State: IssueState.Done, Effort: 5, Sprint: "active"));
        var open = await CreateIssueAsync(new("SPR", "Open work", State: IssueState.InProgress, Effort: 3, Sprint: "active"));
        await CreateIssueAsync(new("SPR", "Not in a sprint"));

        var active = await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint=active");
        active.Items.Select(i => i.Key).Order().ShouldBe(new[] { done.Key, open.Key }.Order());
        var running = await GetAsync<SprintResponse>(_client, $"{Ws}/sprints/{one.Id}");
        running.Effort.ShouldBe(8m);
        running.DoneEffort.ShouldBe(5m);

        var closed = await ReadAsync<CloseSprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints/{one.Id}/close",
            new CloseSprintRequest(UnfinishedDestination.Next), Json.Options, Ct));

        closed.Moved.ShouldBe(1);
        closed.MovedTo!.Id.ShouldBe(two.Id);
        closed.Sprint.State.ShouldBe(SprintState.Closed);
        (await GetAsync<IssueDetail>(_client, $"{Ws}/issues/{open.Key}")).Id.ShouldBe(open.Id);
        (await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint={two.Id}")).Items.ShouldHaveSingleItem().Key.ShouldBe(open.Key);
        (await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint={one.Id}")).Items.ShouldHaveSingleItem().Key.ShouldBe(done.Key);
    }

    [Fact]
    public async Task Only_one_sprint_per_project_is_active()
    {
        var one = await CreateSprintAsync("A", 0);
        var two = await CreateSprintAsync("B", 14);
        await _client.PostAsync($"{Ws}/sprints/{one.Id}/start", null, Ct);

        await ShouldBeProblemAsync(await _client.PostAsync($"{Ws}/sprints/{two.Id}/start", null, Ct),
            HttpStatusCode.Conflict, "sprint_already_active");
    }

    [Fact]
    public async Task Closing_without_a_next_sprint_needs_the_backlog_option()
    {
        var only = await CreateSprintAsync("Last", 0);
        await _client.PostAsync($"{Ws}/sprints/{only.Id}/start", null, Ct);
        var open = await CreateIssueAsync(new("SPR", "Leftover", Sprint: only.Id.ToString()));

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/sprints/{only.Id}/close",
            new CloseSprintRequest(UnfinishedDestination.Next), Json.Options, Ct), HttpStatusCode.Conflict, "no_next_sprint");

        var closed = await ReadAsync<CloseSprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints/{only.Id}/close",
            new CloseSprintRequest(UnfinishedDestination.Backlog), Json.Options, Ct));
        closed.MovedTo.ShouldBeNull();
        (await GetAsync<IssuePage>(_client, $"{Ws}/issues?sprint=none")).Items.ShouldContain(i => i.Key == open.Key);
    }

    [Fact]
    public async Task Issues_cannot_join_closed_sprints_or_other_projects_sprints()
    {
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("OTH", "Other"), Json.Options, Ct),
            HttpStatusCode.Created);
        var other = await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("OTH", "Theirs", new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 15)), Json.Options, Ct), HttpStatusCode.Created);

        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("SPR", "Wrong sprint", Sprint: other.Id.ToString()), Json.Options, Ct),
            HttpStatusCode.BadRequest, "invalid_sprint");
        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("SPR", "No active", Sprint: "active"), Json.Options, Ct),
            HttpStatusCode.BadRequest, "invalid_sprint");
    }

    private async Task<SprintResponse> CreateSprintAsync(string name, int offsetDays)
    {
        var start = new DateOnly(2027, 1, 4).AddDays(offsetDays);
        return await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("SPR", name, start, start.AddDays(11)), Json.Options, Ct), HttpStatusCode.Created);
    }

    private async Task<IssueDetail> CreateIssueAsync(CreateIssueRequest request) =>
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", request, Json.Options, Ct), HttpStatusCode.Created);
}
