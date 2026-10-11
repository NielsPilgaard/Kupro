using System.Net;
using System.Net.Http.Json;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Plan;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Tests.Infrastructure;

namespace Flowstate.Tests.Features;

public sealed class PlanTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly DateOnly Jan = new(2027, 1, 1);
    private static readonly DateOnly Feb = new(2027, 2, 1);
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        foreach (var key in new[] { "ATL", "BOR" })
        {
            await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest(key, key), Json.Options, Ct),
                HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Demand_is_spread_evenly_over_days_and_never_double_counted()
    {
        await CreateAsync(
            new("ATL", "Atlas 1.0", IssueType.Epic, Effort: 500, StartDate: Jan, DueDate: new DateOnly(2027, 2, 28)),
            new("ATL", "Search", IssueType.Feature, Parent: "#0", Effort: 59, StartDate: Jan, DueDate: new DateOnly(2027, 2, 28)));

        var plan = await PlanAsync("?from=2027-01-01&to=2027-02-01&project=ATL");

        var atlas = plan.Projects.ShouldHaveSingleItem();
        atlas.Cards.Single(c => c.Type == IssueType.Epic).CountedEffort.ShouldBe(0m); // its feature carries the estimate
        atlas.Months[0].DemandHours.ShouldBe(31m);                                   // 59 h over 59 days: 31 days in Jan
        atlas.Months[1].DemandHours.ShouldBe(28m);
        atlas.Health.CardsWithoutEffort.ShouldBe(0);
    }

    [Fact]
    public async Task Supply_follows_the_capacity_steps_and_the_gap_is_in_people()
    {
        await ReadAsync<List<CapacityEntry>>(await _client.PutAsJsonAsync($"{Ws}/projects/ATL/capacity",
            new SetCapacityRequest([new(new DateOnly(2027, 1, 15), 2), new(Feb, 3)]), Json.Options, Ct));
        await CreateAsync(new CreateIssueRequest("ATL", "Big feature", IssueType.Feature, Effort: 740, StartDate: Jan, DueDate: new DateOnly(2027, 1, 31)));

        var plan = await PlanAsync("?from=2027-01-01&to=2027-02-01&project=ATL");

        var (jan, feb) = (plan.Projects[0].Months[0], plan.Projects[0].Months[1]);
        jan.SupplyPeople.ShouldBe(2m);                  // 15 Jan normalises to the month
        jan.SupplyHours.ShouldBe(327.71m);              // 2 x 37 h x 31/7 weeks
        jan.DemandPeople.ShouldBe(4.52m);               // 740 h / 163.86 h per person
        jan.GapPeople.ShouldBe(2.52m);
        feb.SupplyPeople.ShouldBe(3m);
        plan.Totals[0].DemandHours.ShouldBe(740m);
    }

    [Fact]
    public async Task Flags_projects_without_dated_work_and_violated_dependencies()
    {
        var created = await CreateAsync(
            new("ATL", "API", IssueType.Feature, Effort: 10, StartDate: Jan, DueDate: new DateOnly(2027, 3, 31)),
            new("BOR", "Client", IssueType.Feature, StartDate: Feb, DueDate: new DateOnly(2027, 4, 30)));
        await _client.PostAsJsonAsync($"{Ws}/issues/{created[0].Key}/relations", new AddRelationRequest(RelationKind.Blocks, created[1].Key), Json.Options, Ct);
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("EMPTY", "Empty"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<MilestoneResponse>(await _client.PostAsJsonAsync($"{Ws}/milestones", new CreateMilestoneRequest("Launch", new DateOnly(2027, 5, 1)), Json.Options, Ct),
            HttpStatusCode.Created);

        var plan = await PlanAsync("?from=2027-01-01&to=2027-12-01");

        plan.Projects.Single(p => p.Key == "EMPTY").Health.NoDatedWork.ShouldBeTrue();
        plan.Projects.Single(p => p.Key == "BOR").Health.CardsWithoutEffort.ShouldBe(1);
        plan.Dependencies.ShouldHaveSingleItem().Violated.ShouldBeTrue(); // API due 31 Mar, client starts 1 Feb
        plan.Milestones.ShouldHaveSingleItem().Name.ShouldBe("Launch");
    }

    [Fact]
    public async Task Horizon_must_run_forward()
    {
        await ShouldBeProblemAsync(await _client.GetAsync($"{Ws}/plan?from=2027-05-01&to=2027-01-01", Ct), HttpStatusCode.BadRequest, "invalid_horizon");
    }

    private async Task<List<IssueSummary>> CreateAsync(params CreateIssueRequest[] issues) =>
        await ReadAsync<List<IssueSummary>>(await _client.PostAsJsonAsync($"{Ws}/issues/bulk", new CreateIssuesRequest(issues), Json.Options, Ct),
            HttpStatusCode.Created);

    private Task<PlanResponse> PlanAsync(string query) => GetAsync<PlanResponse>(_client, $"{Ws}/plan{query}");
}
