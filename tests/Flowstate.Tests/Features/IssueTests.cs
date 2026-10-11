using System.Net;
using System.Net.Http.Json;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Tokens;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Flowstate.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flowstate.Tests.Features;

public sealed class IssueTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Issues => $"/api/v1/workspaces/{_workspace.Slug}/issues";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_workspace.Slug}/projects", new CreateProjectRequest("FLOW", "Flowstate"), Json.Options, Ct),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_numbers_issues_per_project_and_resolves_parent_labels_and_assignee()
    {
        var epic = await CreateAsync(new("FLOW", "Launch", IssueType.Epic));
        var feature = await CreateAsync(new("FLOW", "Login", IssueType.Feature, Parent: epic.Key, Effort: 40));
        var task = await CreateAsync(new("FLOW", "Wire Keycloak", Parent: feature.Key, Assignee: "me",
            Labels: ["Bug", "auth"], Effort: 6, State: IssueState.Todo));

        epic.Key.ShouldBe("FLOW-1");
        task.Key.ShouldBe("FLOW-3");
        task.Parent!.Key.ShouldBe("FLOW-2");
        task.Labels.ShouldBe(["auth", "bug"]);
        task.Assignee!.Name.ShouldBe("Test User");
        task.CreatedBy.Name.ShouldBe("Test User");

        var featureDetail = await GetIssueAsync(feature.Key);
        featureDetail.Children.ShouldHaveSingleItem().Key.ShouldBe("FLOW-3");
        featureDetail.ChildEffort.ShouldBe(6m);
        featureDetail.Effort.ShouldBe(40m);
    }

    [Theory]
    [InlineData(IssueType.Epic, IssueType.Epic, "invalid_hierarchy")]
    [InlineData(IssueType.Feature, IssueType.Feature, "invalid_hierarchy")]
    [InlineData(IssueType.Task, IssueType.Task, "invalid_hierarchy")]
    public async Task Hierarchy_is_enforced(IssueType parentType, IssueType childType, string code)
    {
        var parent = await CreateAsync(new("FLOW", "Parent", parentType));

        var response = await _client.PostAsJsonAsync(Issues, new CreateIssueRequest("FLOW", "Child", childType, Parent: parent.Key), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task Missing_parent_and_project_are_validation_errors()
    {
        await ShouldBeProblemAsync(
            await _client.PostAsJsonAsync(Issues, new CreateIssueRequest("FLOW", "x", Parent: "FLOW-999"), Json.Options, Ct),
            HttpStatusCode.BadRequest, "invalid_parent");
        await ShouldBeProblemAsync(
            await _client.PostAsJsonAsync(Issues, new CreateIssueRequest("NOPE", "x"), Json.Options, Ct),
            HttpStatusCode.BadRequest, "project_not_found");
    }

    [Fact]
    public async Task Bulk_create_is_all_or_nothing()
    {
        var bad = await _client.PostAsJsonAsync($"{Issues}/bulk", new CreateIssuesRequest(
        [
            new("FLOW", "Fine"),
            new("FLOW", "Broken", Parent: "FLOW-404"),
        ]), Json.Options, Ct);
        await ShouldBeProblemAsync(bad, HttpStatusCode.BadRequest, "invalid_parent");
        (await ListAsync("")).Items.ShouldBeEmpty();

        var created = await ReadAsync<List<IssueSummary>>(await _client.PostAsJsonAsync($"{Issues}/bulk",
            new CreateIssuesRequest([new("FLOW", "One"), new("FLOW", "Two"), new("FLOW", "Three")]), Json.Options, Ct),
            HttpStatusCode.Created);
        created.Select(i => i.Title).ShouldBe(["One", "Two", "Three"]);
        created.Select(i => i.Key).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task List_filters_and_pages_with_a_cursor()
    {
        var bug = await CreateAsync(new("FLOW", "Crash", State: IssueState.Todo, Labels: ["bug"], Assignee: "me"));
        await CreateAsync(new("FLOW", "Idea"));
        var epic = await CreateAsync(new("FLOW", "Big", IssueType.Epic));
        await CreateAsync(new("FLOW", "Child", Parent: epic.Key, State: IssueState.Todo));
        await CreateAsync(new("FLOW", "Another"));

        (await ListAsync("?label=BUG")).Items.ShouldHaveSingleItem().Key.ShouldBe(bug.Key);
        (await ListAsync("?state=todo")).Items.Count.ShouldBe(2);
        (await ListAsync("?state=todo&state=backlog&type=epic")).Items.ShouldHaveSingleItem().Key.ShouldBe(epic.Key);
        (await ListAsync("?assignee=me")).Items.ShouldHaveSingleItem().Key.ShouldBe(bug.Key);
        (await ListAsync("?assignee=none")).Items.Count.ShouldBe(4);
        (await ListAsync($"?parent={epic.Key}")).Items.ShouldHaveSingleItem().Title.ShouldBe("Child");
        (await ListAsync("?project=flow")).Items.Count.ShouldBe(5);

        foreach (var sort in new[] { "updated", "created", "priority", "rank" })
        {
            var seen = new List<string>();
            string? cursor = null;
            do
            {
                var page = await ListAsync($"?sort={sort}&limit=2" + (cursor is null ? "" : $"&cursor={cursor}"));
                page.Items.Count.ShouldBeLessThanOrEqualTo(2);
                seen.AddRange(page.Items.Select(i => i.Key));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            seen.Count.ShouldBe(5, sort);
            seen.Distinct().Count().ShouldBe(5, sort);
        }

        await ShouldBeProblemAsync(await _client.GetAsync($"{Issues}?cursor=garbage", Ct), HttpStatusCode.BadRequest, "invalid_cursor");
    }

    [Fact]
    public async Task Update_changes_only_sent_fields_clears_listed_fields_and_logs_activity()
    {
        var issue = await CreateAsync(new("FLOW", "Draft", Assignee: "me", Labels: ["api"], Effort: 3, Description: "Body"));

        var updated = await ReadAsync<IssueDetail>(await _client.PatchAsJsonAsync($"{Issues}/{issue.Key}", new UpdateIssueRequest(
            State: IssueState.Done, AddLabels: ["bug"], RemoveLabels: ["api"], Clear: [IssueField.Assignee, IssueField.Effort]),
            Json.Options, Ct));

        updated.Title.ShouldBe("Draft");
        updated.Description.ShouldBe("Body");
        updated.State.ShouldBe(IssueState.Done);
        updated.CompletedAt.ShouldNotBeNull();
        updated.Assignee.ShouldBeNull();
        updated.Effort.ShouldBeNull();
        updated.Labels.ShouldBe(["bug"]);

        var entries = await ActivityAsync(issue.Id);
        entries.Select(e => e.Kind).ShouldBe(["created", "updated"]);
        entries[1].Data.ShouldNotBeNull().ShouldContain("\"state\"");
        entries[1].Data!.ShouldContain("\"labels\"");
    }

    [Fact]
    public async Task Changing_type_must_keep_children_valid()
    {
        var feature = await CreateAsync(new("FLOW", "Feature", IssueType.Feature));
        await CreateAsync(new("FLOW", "Task", Parent: feature.Key));

        var response = await _client.PatchAsJsonAsync($"{Issues}/{feature.Key}", new UpdateIssueRequest(Type: IssueType.Task), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "invalid_hierarchy");
    }

    [Fact]
    public async Task Bulk_update_is_all_or_nothing()
    {
        var a = await CreateAsync(new("FLOW", "A"));
        var b = await CreateAsync(new("FLOW", "B"));

        var bad = await _client.PatchAsJsonAsync($"{Issues}/bulk", new UpdateIssuesRequest(
        [
            new(a.Key, new UpdateIssueRequest(State: IssueState.Done)),
            new("FLOW-999", new UpdateIssueRequest(State: IssueState.Done)),
        ]), Json.Options, Ct);
        await ShouldBeProblemAsync(bad, HttpStatusCode.NotFound, "issue_not_found");
        (await GetIssueAsync(a.Key)).State.ShouldBe(IssueState.Backlog);

        var done = await ReadAsync<List<IssueSummary>>(await _client.PatchAsJsonAsync($"{Issues}/bulk", new UpdateIssuesRequest(
        [
            new(a.Key, new UpdateIssueRequest(State: IssueState.Done)),
            new(b.Key, new UpdateIssueRequest(Priority: IssuePriority.High)),
        ]), Json.Options, Ct));
        done[0].State.ShouldBe(IssueState.Done);
        done[1].Priority.ShouldBe(IssuePriority.High);
    }

    [Fact]
    public async Task Move_places_issue_between_neighbours_and_changes_state()
    {
        var first = await CreateAsync(new("FLOW", "First", State: IssueState.Todo));
        var second = await CreateAsync(new("FLOW", "Second", State: IssueState.Todo));
        var moving = await CreateAsync(new("FLOW", "Moving"));

        var moved = await ReadAsync<IssueSummary>(await _client.PostAsJsonAsync($"{Issues}/{moving.Key}/move",
            new MoveIssueRequest(IssueState.Todo, After: first.Key, Before: second.Key), Json.Options, Ct));

        moved.State.ShouldBe(IssueState.Todo);
        var column = await ListAsync("?state=todo&sort=rank");
        column.Items.Select(i => i.Key).ShouldBe([first.Key, moving.Key, second.Key]);
    }

    [Fact]
    public async Task Relations_are_visible_from_both_sides_and_unique()
    {
        var api = await CreateAsync(new("FLOW", "API"));
        var ui = await CreateAsync(new("FLOW", "UI"));

        var relation = await ReadAsync<IssueRelationResponse>(await _client.PostAsJsonAsync($"{Issues}/{api.Key}/relations",
            new AddRelationRequest(RelationKind.Blocks, ui.Key), Json.Options, Ct), HttpStatusCode.Created);

        (await GetIssueAsync(ui.Key)).Relations.ShouldHaveSingleItem().Type.ShouldBe(RelationKind.BlockedBy);
        await ShouldBeProblemAsync(await _client.PostAsJsonAsync($"{Issues}/{ui.Key}/relations",
            new AddRelationRequest(RelationKind.BlockedBy, api.Key), Json.Options, Ct), HttpStatusCode.Conflict, "relation_exists");

        (await _client.DeleteAsync($"{Issues}/{ui.Key}/relations/{relation.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetIssueAsync(api.Key)).Relations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Idempotency_key_replays_the_first_response()
    {
        var request = new CreateIssueRequest("FLOW", "Exactly once");

        async Task<HttpResponseMessage> SendAsync(CreateIssueRequest body)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Issues) { Content = JsonContent.Create(body, options: Json.Options) };
            message.Headers.Add("Idempotency-Key", "create-once");
            return await _client.SendAsync(message, Ct);
        }

        var first = await ReadAsync<IssueDetail>(await SendAsync(request), HttpStatusCode.Created);
        var replay = await SendAsync(request);
        var second = await ReadAsync<IssueDetail>(replay, HttpStatusCode.Created);

        second.Key.ShouldBe(first.Key);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await ListAsync("")).Items.Count.ShouldBe(1);
        await ShouldBeProblemAsync(await SendAsync(request with { Title = "Different" }),
            HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
    }

    [Fact]
    public async Task Project_key_is_locked_once_issues_exist()
    {
        await CreateAsync(new("FLOW", "First"));

        var response = await _client.PatchAsJsonAsync($"/api/v1/workspaces/{_workspace.Slug}/projects/FLOW",
            new UpdateProjectRequest(Key: "FLW"), Json.Options, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict, "project_key_locked");
    }

    [Fact]
    public async Task Issues_are_isolated_between_workspaces_with_the_same_project_key()
    {
        var mine = await CreateAsync(new("FLOW", "Mine"));
        var (other, otherWorkspace, _) = await NewWorkspaceAsync();
        await other.PostAsJsonAsync($"/api/v1/workspaces/{otherWorkspace.Slug}/projects", new CreateProjectRequest("FLOW", "Theirs"), Json.Options, Ct);

        await ShouldBeProblemAsync(await other.GetAsync($"{Issues}/{mine.Key}", Ct), HttpStatusCode.NotFound, "workspace_not_found");
        await ShouldBeProblemAsync(await other.GetAsync($"/api/v1/workspaces/{otherWorkspace.Slug}/issues/{mine.Key}", Ct),
            HttpStatusCode.NotFound, "issue_not_found");
    }

    [Fact]
    public async Task Agent_changes_are_attributed_to_the_agent_and_its_surface()
    {
        var token = await NewTokenAsync(_client, new("bot", ApiTokenKind.Agent, "triage-agent", null, _workspace.Slug));
        using var agent = Factory.CreateBearerClient(token.Secret);
        agent.DefaultRequestHeaders.Add("X-Flowstate-Client", "mcp");

        var issue = await ReadAsync<IssueDetail>(await agent.PostAsJsonAsync(Issues, new CreateIssueRequest("FLOW", "Triaged", Assignee: "me"), Json.Options, Ct),
            HttpStatusCode.Created);

        issue.CreatedBy.Kind.ShouldBe(MemberKind.Agent);
        issue.CreatedBy.Name.ShouldBe("triage-agent");
        issue.Assignee!.Name.ShouldBe("triage-agent");
        (await ActivityAsync(issue.Id)).ShouldHaveSingleItem().Via.ShouldBe(Surface.Mcp);
    }

    [Fact]
    public async Task Guest_can_read_but_not_create_issues()
    {
        await CreateAsync(new("FLOW", "Visible"));
        var guestUser = TestUser.New("Gus");
        using var guest = Factory.CreateUserClient(guestUser);
        var invite = await ReadAsync<MemberResponse>(await _client.PostAsJsonAsync($"/api/v1/workspaces/{_workspace.Slug}/members",
            new InviteMemberRequest(guestUser.Email, WorkspaceRole.Guest), Json.Options, Ct), HttpStatusCode.Created);
        await guest.PostAsync($"/api/v1/invites/{invite.Id}/accept", null, Ct);

        (await GetAsync<IssuePage>(guest, Issues)).Items.ShouldHaveSingleItem();
        await ShouldBeProblemAsync(await guest.PostAsJsonAsync(Issues, new CreateIssueRequest("FLOW", "No"), Json.Options, Ct),
            HttpStatusCode.Forbidden, "insufficient_role");
    }

    private async Task<IssueDetail> CreateAsync(CreateIssueRequest request) =>
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync(Issues, request, Json.Options, Ct), HttpStatusCode.Created);

    private Task<IssueDetail> GetIssueAsync(string key) => GetAsync<IssueDetail>(_client, $"{Issues}/{key}");

    private Task<IssuePage> ListAsync(string query) => GetAsync<IssuePage>(_client, Issues + query);

    private async Task<List<ActivityEntry>> ActivityAsync(Guid issueId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowstateDbContext>>().CreateDbContextAsync(Ct);
        return await db.Activity.AsNoTracking().Where(a => a.IssueId == issueId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(Ct);
    }
}
