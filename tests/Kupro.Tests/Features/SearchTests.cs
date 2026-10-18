using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>Task 009: full-text and typo-tolerant search with the shared <c>key:value</c> filter syntax.</summary>
public sealed class SearchTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("FLOW", "Kupro"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("WEB", "Website"), Json.Options, Ct),
            HttpStatusCode.Created);
        var response = await _client.PostAsJsonAsync($"{Ws}/issues/bulk", new CreateIssuesRequest(
        [
            new("FLOW", "Render dependency curves", IssueType.Feature, State: IssueState.Todo, Assignee: "me"),                   // FLOW-1
            new("FLOW", "Plan epics", IssueType.Epic, Description: "Rendering the delivery plan for every project."),             // FLOW-2
            new("FLOW", "Kortene flyttes", Description: "Når man trækker kortene, skal de flyttes til den rigtige måned."),     // FLOW-3
            new("FLOW", "Billing export", State: IssueState.Done, Labels: ["bug"]),                                               // FLOW-4
            new("WEB", "Render the landing page", State: IssueState.Todo),                                                       // WEB-1
        ]), Json.Options, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Free_text_matches_stemmed_words_in_title_and_description_best_first()
    {
        var page = await SearchAsync("rendering");

        // Title matches (weight A) outrank the description match (weight B).
        Keys(page).ShouldBe(["FLOW-1", "WEB-1", "FLOW-2"], ignoreOrder: true);
        Keys(page)[^1].ShouldBe("FLOW-2");
    }

    [Fact]
    public async Task Title_match_is_typo_tolerant()
    {
        Keys(await SearchAsync("dependancy")).ShouldBe(["FLOW-1"]);
        Keys(await SearchAsync("biling exprt")).ShouldBe(["FLOW-4"]);
    }

    [Fact]
    public async Task Danish_text_is_stemmed_as_Danish()
    {
        Keys(await SearchAsync("kort måneder")).ShouldBe(["FLOW-3"]);
    }

    [Fact]
    public async Task Issue_key_finds_the_issue()
    {
        Keys(await SearchAsync("flow-4")).ShouldContain("FLOW-4");
        Keys(await SearchAsync("flow-4"))[0].ShouldBe("FLOW-4");
    }

    [Fact]
    public async Task Filters_in_the_query_combine_with_text_and_parameters()
    {
        Keys(await SearchAsync("render state:todo assignee:me")).ShouldBe(["FLOW-1"]);
        Keys(await SearchAsync("render project:web")).ShouldBe(["WEB-1"]);
        Keys(await SearchAsync("type:epic,feature")).ShouldBe(["FLOW-2", "FLOW-1"], ignoreOrder: true);
        Keys(await SearchAsync("label:BUG")).ShouldBe(["FLOW-4"]);
        Keys(await SearchAsync("state:in-progress")).ShouldBeEmpty();
        Keys(await SearchAsync("render project:flow", "&state=todo")).ShouldBe(["FLOW-1"]);
    }

    [Fact]
    public async Task Sprint_current_means_the_active_sprint()
    {
        var sprint = await ReadAsync<SprintResponse>(await _client.PostAsJsonAsync($"{Ws}/sprints",
            new CreateSprintRequest("FLOW", "Sprint 1", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 16)), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<SprintResponse>(await _client.PostAsync($"{Ws}/sprints/{sprint.Id}/start", null, Ct));
        (await _client.PatchAsJsonAsync($"{Ws}/issues/FLOW-3", new UpdateIssueRequest(Sprint: sprint.Id.ToString()), Json.Options, Ct))
            .EnsureSuccessStatusCode();

        Keys(await SearchAsync("sprint:current")).ShouldBe(["FLOW-3"]);
        Keys(await SearchAsync("kort sprint:none")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("state:nope")]
    [InlineData("type:story")]
    [InlineData("assignee:me assignee:none")]
    [InlineData("label:")]
    public async Task Invalid_filters_are_rejected(string q)
    {
        await ShouldBeProblemAsync(await _client.GetAsync($"{Ws}/issues?q={Uri.EscapeDataString(q)}", Ct),
            HttpStatusCode.BadRequest, "invalid_query");
    }

    [Fact]
    public async Task Relevance_pages_cover_every_match_once()
    {
        var first = await SearchAsync("render", "&limit=2");
        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();

        var second = await SearchAsync("render", $"&limit=2&cursor={first.NextCursor}");
        var all = Keys(first).Concat(Keys(second)).ToList();
        all.ShouldBeUnique();
        all.ShouldBe(Keys(await SearchAsync("render", "&limit=50")));
    }

    [Fact]
    public async Task Search_never_crosses_workspaces()
    {
        var (other, otherWorkspace, _) = await NewWorkspaceAsync("Other");
        var page = await GetAsync<IssuePage>(other, $"/api/v1/workspaces/{otherWorkspace.Slug}/issues?q=render");
        page.Items.ShouldBeEmpty();
    }

    private Task<IssuePage> SearchAsync(string q, string extra = "") =>
        GetAsync<IssuePage>(_client, $"{Ws}/issues?q={Uri.EscapeDataString(q)}{extra}");

    private static List<string> Keys(IssuePage page) => page.Items.Select(i => i.Key).ToList();
}
