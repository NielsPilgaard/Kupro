using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Tests.Infrastructure;
using Npgsql;

namespace Kupro.Tests.Features;

/// <summary>
/// Search on ten times the ADR-010 reference dataset, with descriptions, since its cost grows with the workspace
/// (ADR-010 IMP-004). Takes a minute and needs a quiet machine, so it runs only when asked for:
/// <c>Kupro.Tests.exe -explicit only -class Kupro.Tests.Features.SearchScaleTests -showLiveOutput</c>
/// </summary>
[Trait("Category", "Perf")]
[Collection(nameof(PerfBudgetRunsAlone))]
public sealed class SearchScaleTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const int Copies = 9;
    private const int Samples = 21;
    private static readonly string[] Projects = ["CORE", "WEB", "API", "OPS"];
    private static readonly string[] Topics = ["Render dependency curves", "Billing export", "Login with SSO", "Danish copy review",
        "Drag epics between months", "Offline mode", "Audit log", "Notifications digest", "Search ranking", "Webhook retries"];

    // Typo, no match, text plus filters, a word in every title, a word in a tenth, several words, a key.
    private static readonly string[] Queries = ["dependancy", "zzzqqqxx", "billing state:todo,inProgress", "issue", "render", "login with sso", "CORE-1"];

    private readonly PostgresFixture _postgres = postgres;

    [Fact(Explicit = true)]
    public async Task Search_stays_within_the_latency_budget_on_100k_issues()
    {
        var (client, workspace, _) = await NewWorkspaceAsync();
        var ws = $"/api/v1/workspaces/{workspace.Slug}";
        await SeedAsync(client, ws);
        await AnalyzeAsync();
        await MeasureAsync(client, ws, "10k");

        await CopyIssuesAsync(workspace.Id);
        await AnalyzeAsync();
        var results = await MeasureAsync(client, ws, "100k");

        results.Where(r => PerfBudgetTests.IsOverBudget(r.P50, r.P99)).Select(r => r.Line).ToList()
            .ShouldBeEmpty("search over the ADR-010 budget (p50 100 ms, p99 500 ms)");
    }

    private static async Task<List<(string Line, double P50, double P99)>> MeasureAsync(HttpClient client, string ws, string size)
    {
        var results = new List<(string, double, double)>();
        foreach (var q in Queries)
        {
            var url = $"{ws}/issues?q={Uri.EscapeDataString(q)}";
            (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode(); // warm up: JIT and query plan caches
            var times = new List<double>(Samples);
            for (var i = 0; i < Samples; i++)
            {
                var started = Stopwatch.GetTimestamp();
                (await client.GetAsync(url, Ct)).EnsureSuccessStatusCode();
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            times.Sort();
            var line = string.Create(CultureInfo.InvariantCulture, $"{size} q={q}: p50 {times[Samples / 2]:F1} ms, max {times[^1]:F1} ms");
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            results.Add((line, times[Samples / 2], times[^1]));
        }

        return results;
    }

    private static async Task SeedAsync(HttpClient client, string ws)
    {
        foreach (var key in Projects)
        {
            (await client.PostAsJsonAsync($"{ws}/projects", new CreateProjectRequest(key, key), Json.Options, Ct)).EnsureSuccessStatusCode();
        }

        var states = Enum.GetValues<IssueState>();
        for (var batch = 0; batch < 100; batch++)
        {
            var items = Enumerable.Range(0, 100).Select(i => new CreateIssueRequest(
                Projects[i % Projects.Length],
                $"{Topics[(batch + i) % Topics.Length]} issue {batch}-{i}",
                State: states[(batch + i) % states.Length],
                Description: $"When someone opens {Topics[(batch + 3) % Topics.Length]} the page should explain what changed since last week. "
                    + $"Når man trækker kortene skal de flyttes til den rigtige måned. See {Topics[(i + 5) % Topics.Length]} for the background, "
                    + "and keep the copy short because customers read this on their phones while they are in a meeting.")).ToList();
            (await client.PostAsJsonAsync($"{ws}/issues/bulk", new CreateIssuesRequest(items), Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }
    }

    private async Task AnalyzeAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var analyze = new NpgsqlCommand("VACUUM ANALYZE", connection);
        await analyze.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Through the API this would take minutes, and only the row count matters here.</summary>
    private async Task CopyIssuesAsync(Guid workspaceId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        var columns = new List<string>();
        await using (var read = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'issues' AND is_generated = 'NEVER' ORDER BY ordinal_position", connection))
        await using (var reader = await read.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                columns.Add(reader.GetString(0));
            }
        }

        var values = columns.Select(c => c switch
        {
            "id" => "gen_random_uuid()",
            "key" => "split_part(i.key, '-', 1) || '-' || (i.number + 100000 * n)",
            "number" => "i.number + 100000 * n",
            "updated_at" => "i.updated_at - make_interval(days => n)",
            _ => "i." + c,
        });
#pragma warning disable CA2100 // Column names come from the catalog, the id from the API.
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO issues ({string.Join(", ", columns)}) SELECT {string.Join(", ", values)} " +
            $"FROM issues i CROSS JOIN generate_series(1, {Copies}) n WHERE i.workspace_id = '{workspaceId}'", connection);
#pragma warning restore CA2100
        insert.CommandTimeout = 300;
        await insert.ExecuteNonQueryAsync(Ct);
    }
}
