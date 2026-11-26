using System.Diagnostics;
using System.Globalization;
using System.Net;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Time;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kupro.Tests.Features;

/// <summary>
/// ADR-010 latency budget on the ADR's reference dataset (10k issues in one workspace): every endpoint stays under
/// 500 ms p99 and 100 ms p50. Measured in-process against Testcontainers Postgres, so it catches query regressions
/// (missing index, N+1, unbounded reads), not network or Kestrel overhead. Task 014.
/// </summary>
[Trait("Category", "Perf")]
[Collection(nameof(PerfBudgetRunsAlone))]
public sealed class PerfBudgetTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private readonly PostgresFixture _postgres = postgres;
    private const int IssueCount = 10_000;
    private const int Samples = 60;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(3);
    private readonly Stopwatch _retrying = new();
    private static readonly TimeSpan P99Budget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan P50Budget = TimeSpan.FromMilliseconds(100);
    private static readonly string[] Projects = ["CORE", "WEB", "API", "OPS"];
    private static readonly string[] Topics = ["Render dependency curves", "Billing export", "Login with SSO", "Danish copy review",
        "Drag epics between months", "Offline mode", "Audit log", "Notifications digest", "Search ranking", "Webhook retries"];

    [Fact]
    public async Task Endpoints_meet_the_latency_budget_on_10k_issues()
    {
        var (client, workspace, _) = await NewWorkspaceAsync();
        var ws = $"/api/v1/workspaces/{workspace.Slug}";
        await SeedAsync(client, ws);
        await AnalyzeAsync();

        var reads = new[]
        {
            "/api/v1/me", "/api/v1/workspaces", ws, $"{ws}/projects", $"{ws}/members", $"{ws}/labels",
            $"{ws}/issues", $"{ws}/issues?limit=200", $"{ws}/issues?project=CORE&state=todo&sort=rank",
            $"{ws}/issues?label=bug", $"{ws}/issues?assignee=me&sort=priority", $"{ws}/issues?type=epic",
            $"{ws}/issues/CORE-1", $"{ws}/issues/API-2400",
            // Search (task 009): typo, text plus filters, a word in every title (ranks all 10k), filters only.
            $"{ws}/issues?q=dependancy", $"{ws}/issues?q=billing%20state:todo,inProgress", $"{ws}/issues?q=issue",
            $"{ws}/issues?q=type:epic%20assignee:me",
            // Task 030: served from memory, which the next measure doesn't get.
            $"{ws}/plan", $"{ws}/plan?view=summary",
        };

        var results = new List<(string Name, double P50, double P99)>();
        foreach (var url in reads)
        {
            results.Add(await MeasureAsync($"GET {url.Replace(ws, "{ws}", StringComparison.Ordinal)}", async () =>
                (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode()));
        }

        var generations = Factory.Services.GetRequiredService<WorkspaceCacheGenerations>();
        results.Add(await MeasureAsync("GET {ws}/plan, after a write", async () =>
        {
            generations.Advance(workspace.Id);
            (await client.GetAsync($"{ws}/plan", Ct)).EnsureSuccessStatusCode();
        }));

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

        // Task 028: a 300-issue sprint with 5 000 ledger rows.
        var sprintId = await SeedSprintWithLedgerAsync(client, ws);
        await AnalyzeAsync();
        foreach (var url in new[] { $"{ws}/sprints/{sprintId}/review", $"{ws}/sprints/{sprintId}/burndown?by=assignee" })
        {
            results.Add(await MeasureAsync($"GET {url.Replace(ws, "{ws}", StringComparison.Ordinal).Replace(sprintId.ToString(), "{id}", StringComparison.Ordinal)}", async () =>
                (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode()));
        }

        // Task 034: a year of workday entries, three a day, for 50 people (the caller and 49 others).
        await SeedTimeAsync(workspace.Slug);
        await AnalyzeAsync();
        var today = TimeService.LocalDay(DateTimeOffset.UtcNow);
        var weekStart = TimeService.WeekStart(today);
        foreach (var url in new[]
                 {
                     $"{ws}/time/mine", $"{ws}/issues/CORE-1/time",
                     $"{ws}/time/entries?from={weekStart:yyyy-MM-dd}&to={weekStart.AddDays(6):yyyy-MM-dd}",
                 })
        {
            results.Add(await MeasureAsync($"GET {url.Replace(ws, "{ws}", StringComparison.Ordinal)}", async () =>
                (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode()));
        }

        (await client.PatchAsJsonAsync(ws, new UpdateWorkspaceRequest(TimeRegistration: true), Json.Options, Ct)).EnsureSuccessStatusCode();
        var timed = await ReadAsync<IssueDetail>(await client.PostAsJsonAsync($"{ws}/issues", new CreateIssueRequest("WEB", "Timed"), Json.Options, Ct),
            HttpStatusCode.Created);
        results.Add(await MeasureAsync("POST {ws}/time", async () =>
            (await client.PostAsJsonAsync($"{ws}/time", new LogTimeRequest(timed.Key, 1), Json.Options, Ct)).EnsureSuccessStatusCode()));

        // CI sets KUPRO_PERF_REPORT to $GITHUB_STEP_SUMMARY, so every run shows the numbers.
        if (Environment.GetEnvironmentVariable("KUPRO_PERF_REPORT") is { Length: > 0 } report)
        {
            var lines = results.Select(r => string.Create(CultureInfo.InvariantCulture, $"| `{r.Name}` | {r.P50:F1} | {r.P99:F1} |"));
            await File.AppendAllLinesAsync(report,
                [string.Create(CultureInfo.InvariantCulture, $"### Latency budget, {IssueCount:N0} issues (ms)"), "", "| Endpoint | p50 | p99 |", "|---|---:|---:|", .. lines, ""], Ct);
        }

        var overBudget = results
            .Where(r => IsOverBudget(r.P50, r.P99))
            .Select(r => $"{r.Name}: p50 {r.P50:F1} ms, p99 {r.P99:F1} ms")
            .ToList();
        overBudget.ShouldBeEmpty("endpoints over the ADR-010 budget (p50 100 ms, p99 500 ms)");
    }

    /// <summary>Autovacuum does this in production. Without planner stats the numbers describe plans production never runs (ADR-010 IMP-001).</summary>
    private async Task AnalyzeAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var analyze = new NpgsqlCommand("ANALYZE", connection);
        await analyze.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Straight into the tables: through the API this would be 39 000 requests.</summary>
    private async Task SeedTimeAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var seed = new NpgsqlCommand("""
            WITH w AS (SELECT id FROM workspaces WHERE slug = @slug)
            INSERT INTO members (id, workspace_id, kind, role, can_manage_billing, status, display_name, created_at)
            SELECT gen_random_uuid(), w.id, 'Human', 'Member', false, 'Placeholder', 'Person ' || n, now()
            FROM w, generate_series(1, 49) n;

            WITH w AS (SELECT id FROM workspaces WHERE slug = @slug),
                 picks AS (SELECT array_agg(id ORDER BY number) AS ids FROM issues WHERE workspace_id = (SELECT id FROM w) AND number <= 500)
            INSERT INTO time_entries (id, workspace_id, member_id, issue_id, date, minutes, via, created_at, updated_at)
            SELECT gen_random_uuid(), m.workspace_id, m.id, picks.ids[1 + ((extract(doy FROM d)::int * 3 + k) % array_length(picks.ids, 1))],
                   d::date, 150, 'Web', now(), now()
            FROM members m, picks, generate_series(current_date - 365, current_date, interval '1 day') d, generate_series(0, 2) k
            WHERE m.workspace_id = (SELECT id FROM w) AND m.kind = 'Human' AND extract(isodow FROM d) < 6;
            """, connection);
        seed.Parameters.AddWithValue("slug", slug);
        await seed.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Guid> SeedSprintWithLedgerAsync(HttpClient client, string ws)
    {
        var sprint = await ReadAsync<SprintResponse>(await client.PostAsJsonAsync($"{ws}/sprints",
            new CreateSprintRequest("CORE", "Big", DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(13)), Json.Options, Ct),
            HttpStatusCode.Created);
        var keys = new List<string>();
        for (var batch = 0; batch < 3; batch++)
        {
            var created = await ReadAsync<IReadOnlyList<IssueSummary>>(await client.PostAsJsonAsync($"{ws}/issues/bulk", new CreateIssuesRequest(
                Enumerable.Range(0, 100).Select(i => new CreateIssueRequest("CORE", $"Sprint issue {batch}-{i}", State: IssueState.Todo, Effort: 1 + (i % 8),
                    Sprint: sprint.Id.ToString())).ToList()), Json.Options, Ct), HttpStatusCode.Created);
            keys.AddRange(created.Select(i => i.Key));
        }

        (await client.PostAsync($"{ws}/sprints/{sprint.Id}/start", null, Ct)).EnsureSuccessStatusCode();

        // Effort changes write one row per issue: 16 rounds on top of the 300 baseline rows make 5 100.
        for (var round = 0; round < 16; round++)
        {
            foreach (var chunk in keys.Chunk(100))
            {
                var items = chunk.Select(k => new IssueUpdate(k, new UpdateIssueRequest(Effort: 2 + round))).ToList();
                (await client.PatchAsJsonAsync($"{ws}/issues/bulk", new UpdateIssuesRequest(items), Json.Options, Ct)).EnsureSuccessStatusCode();
            }
        }

        return sprint.Id;
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
                $"{Topics[(batch + i) % Topics.Length]} issue {batch}-{i}",
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

    internal static bool IsOverBudget(double p50, double p99) =>
        p99 > P99Budget.TotalMilliseconds || p50 > P50Budget.TotalMilliseconds;

    /// <summary>
    /// A busy machine slows every sample, for as long as a second test suite beside this one runs, so one breach proves
    /// nothing. A query regression is over budget on every attempt. All endpoints share the window, which keeps a real
    /// failure to a few minutes; a machine that stays overloaded past it fails, since it can't measure the budget.
    /// </summary>
    private async Task<(string, double, double)> MeasureAsync(string name, Func<Task> request)
    {
        await request(); // warm up: JIT and query plan caches
        var (p50, p99) = await SampleAsync(request);
        _retrying.Start();
        while (IsOverBudget(p50, p99) && _retrying.Elapsed < RetryWindow)
        {
            await Task.Delay(RetryDelay, Ct);
            (p50, p99) = await SampleAsync(request);
        }

        _retrying.Stop();
        return (name, p50, p99);
    }

    private static async Task<(double P50, double P99)> SampleAsync(Func<Task> request)
    {
        var times = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await request();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        times.Sort();
        return (times[Samples / 2], times[(int)Math.Ceiling(Samples * 0.99) - 1]);
    }
}

/// <summary>Runs alone, after the parallel classes: their load on the CPU and on Postgres would count as latency.</summary>
[CollectionDefinition(nameof(PerfBudgetRunsAlone), DisableParallelization = true)]
public sealed class PerfBudgetRunsAlone;
