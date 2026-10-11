using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Projects;
using Flowstate.Tests.Infrastructure;

namespace Flowstate.Tests.Features;

/// <summary>
/// ADR-010 latency budget on the ADR's reference dataset (10k issues in one workspace): every endpoint stays under
/// 500 ms p99 and 100 ms p50. Measured in-process against Testcontainers Postgres, so it catches query regressions
/// (missing index, N+1, unbounded reads), not network or Kestrel overhead. Task 014.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PerfBudgetTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const int IssueCount = 10_000;
    private const int Samples = 60;
    private static readonly TimeSpan P99Budget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan P50Budget = TimeSpan.FromMilliseconds(100);
    private static readonly string[] Projects = ["CORE", "WEB", "API", "OPS"];

    [Fact]
    public async Task Endpoints_meet_the_latency_budget_on_10k_issues()
    {
        var (client, workspace, _) = await NewWorkspaceAsync();
        var ws = $"/api/v1/workspaces/{workspace.Slug}";
        await SeedAsync(client, ws);

        var reads = new[]
        {
            "/api/v1/me", "/api/v1/workspaces", ws, $"{ws}/projects", $"{ws}/members", $"{ws}/labels",
            $"{ws}/issues", $"{ws}/issues?limit=200", $"{ws}/issues?project=CORE&state=todo&sort=rank",
            $"{ws}/issues?label=bug", $"{ws}/issues?assignee=me&sort=priority", $"{ws}/issues?type=epic",
            $"{ws}/issues/CORE-1", $"{ws}/issues/API-2400",
        };

        var results = new List<(string Name, double P50, double P99)>();
        foreach (var url in reads)
        {
            results.Add(await MeasureAsync($"GET {url.Replace(ws, "{ws}", StringComparison.Ordinal)}", async () =>
                (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode()));
        }

        var n = 0;
        results.Add(await MeasureAsync("POST {ws}/issues", async () =>
            (await client.PostAsJsonAsync($"{ws}/issues", new CreateIssueRequest("WEB", $"Perf {n++}", Labels: ["perf"]), Json.Options, Ct))
            .EnsureSuccessStatusCode()));
        results.Add(await MeasureAsync("PATCH {ws}/issues/{key}", async () =>
            (await client.PatchAsJsonAsync($"{ws}/issues/WEB-{(n++ % 2000) + 1}", new UpdateIssueRequest(Priority: IssuePriority.High), Json.Options, Ct))
            .EnsureSuccessStatusCode()));
        results.Add(await MeasureAsync("POST {ws}/issues/{key}/move", async () =>
            (await client.PostAsJsonAsync($"{ws}/issues/OPS-{(n++ % 2000) + 1}/move", new MoveIssueRequest(IssueState.InProgress), Json.Options, Ct))
            .EnsureSuccessStatusCode()));

        // CI sets FLOWSTATE_PERF_REPORT to $GITHUB_STEP_SUMMARY, so every run shows the numbers.
        if (Environment.GetEnvironmentVariable("FLOWSTATE_PERF_REPORT") is { Length: > 0 } report)
        {
            var lines = results.Select(r => string.Create(CultureInfo.InvariantCulture, $"| `{r.Name}` | {r.P50:F1} | {r.P99:F1} |"));
            await File.AppendAllLinesAsync(report,
                [string.Create(CultureInfo.InvariantCulture, $"### Latency budget, {IssueCount:N0} issues (ms)"), "", "| Endpoint | p50 | p99 |", "|---|---:|---:|", .. lines, ""], Ct);
        }

        var overBudget = results
            .Where(r => r.P99 > P99Budget.TotalMilliseconds || r.P50 > P50Budget.TotalMilliseconds)
            .Select(r => $"{r.Name}: p50 {r.P50:F1} ms, p99 {r.P99:F1} ms")
            .ToList();
        overBudget.ShouldBeEmpty("endpoints over the ADR-010 budget (p50 100 ms, p99 500 ms)");
    }

    private static async Task SeedAsync(HttpClient client, string ws)
    {
        foreach (var key in Projects)
        {
            (await client.PostAsJsonAsync($"{ws}/projects", new CreateProjectRequest(key, key), Json.Options, Ct)).EnsureSuccessStatusCode();
        }

        var states = Enum.GetValues<IssueState>();
        var priorities = Enum.GetValues<IssuePriority>();
        for (var batch = 0; batch < IssueCount / 100; batch++)
        {
            var items = Enumerable.Range(0, 100).Select(i => new CreateIssueRequest(
                Projects[i % Projects.Length],
                $"Seeded issue {batch}-{i}",
                Type: i % 50 == 0 ? IssueType.Epic : IssueType.Task,
                State: states[(batch + i) % states.Length],
                Priority: priorities[i % priorities.Length],
                Assignee: i % 3 == 0 ? "me" : null,
                Effort: i % 8,
                Labels: i % 7 == 0 ? ["bug"] : i % 5 == 0 ? ["ui", "api"] : null)).ToList();
            var response = await client.PostAsJsonAsync($"{ws}/issues/bulk", new CreateIssuesRequest(items), Json.Options, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
    }

    private static async Task<(string, double, double)> MeasureAsync(string name, Func<Task> request)
    {
        await request(); // warm up: JIT and query plan caches
        var times = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await request();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        times.Sort();
        return (name, times[Samples / 2], times[(int)Math.Ceiling(Samples * 0.99) - 1]);
    }
}
