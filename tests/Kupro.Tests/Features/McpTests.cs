using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kupro.Tests.Features;

/// <summary>Remote MCP server over streamable HTTP, driven by the official MCP client (task 006).</summary>
public sealed class McpTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private string _workspace = null!;
    private string _agentToken = null!;
    private HttpClient _owner = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var (client, workspace, _) = await NewWorkspaceAsync();
        _owner = client;
        _workspace = workspace.Slug;
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{_workspace}/projects",
            new CreateProjectRequest("MCP", "MCP project"), Json.Options, Ct), HttpStatusCode.Created);
        _agentToken = (await NewTokenAsync(client, new("claude", ApiTokenKind.Agent, "claude", null, _workspace))).Secret;
    }

    [Fact]
    public async Task Lists_a_small_coarse_tool_set()
    {
        await using var mcp = await ConnectAsync();

        var tools = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();

        tools.ShouldContain("search_issues");
        tools.ShouldContain("create_issues");
        tools.ShouldContain("update_issues");
        tools.ShouldContain("get_context");
        tools.Count.ShouldBeLessThanOrEqualTo(20, "ADR-001 IMP-003: coarse tools; consolidate before adding more");
    }

    [Fact]
    public async Task Tool_list_is_cacheable_for_an_hour_on_the_current_protocol()
    {
        await using var mcp = await ConnectAsync("2026-07-28");

        var first = await mcp.ListToolsAsync(new ListToolsRequestParams(), Ct);
        var second = await mcp.ListToolsAsync(new ListToolsRequestParams(), Ct);

        first.TimeToLive.ShouldBe(TimeSpan.FromHours(1));
        first.CacheScope.ShouldBe(CacheScope.Private, "the endpoint requires authentication");
        first.Tools.Select(t => t.Name).ShouldBe(second.Tools.Select(t => t.Name));
        first.Tools.Select(t => t.Name).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Tool_list_sends_ttlMs_on_the_wire()
    {
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _agentToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new Dictionary<string, object>
                {
                    ["_meta"] = new Dictionary<string, object>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "test", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { },
                    },
                },
            }),
        };
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/list");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await http.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain("\"ttlMs\":3600000");
        body.ShouldContain("\"cacheScope\":\"private\"");
    }

    [Fact]
    public async Task Tool_list_stays_small_and_works_on_the_older_protocol()
    {
        await using var mcp = await ConnectAsync("2025-11-25");

        var list = await mcp.ListToolsAsync(new ListToolsRequestParams(), Ct);
        var json = JsonSerializer.Serialize(list, McpJsonUtilities.DefaultOptions);
        json.Length.ShouldBeLessThan(19_000, "ADR-001 IMP-003: every session pays for this list");
        json.ShouldNotContain("\"default\":null");
        json.ShouldNotContain("\"null\"");
        (await CallAsync(mcp, "get_context", new())).GetProperty("projects").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Responses_stay_within_their_byte_limits_on_the_reference_dataset()
    {
        // 1,000 dated epics across 3 projects, 300 tasks: what get_plan's full view measured 227 KB on, at a seedable size.
        await using var mcp = await ConnectAsync();
        foreach (var project in new[] { "REF", "OPS" })
        {
            await CallAsync(mcp, "create_project", new() { ["key"] = project, ["name"] = project });
        }

        string[] projects = ["MCP", "REF", "OPS"];
        for (var batch = 0; batch < 10; batch++)
        {
            await CallAsync(mcp, "create_issues", new()
            {
                ["issues"] = Enumerable.Range(0, 100).Select(i => new
                {
                    project = projects[i % 3],
                    title = $"Dependency issue {batch}-{i}",
                    type = "epic",
                    effort = 40,
                    startDate = "2027-01-01",
                    dueDate = "2027-06-30",
                }).ToArray(),
            });
        }

        var tasks = new List<string>();
        for (var batch = 0; batch < 3; batch++)
        {
            var created = await CallAsync(mcp, "create_issues", new()
            {
                ["issues"] = Enumerable.Range(0, 100).Select(i => new
                {
                    project = "MCP",
                    title = $"Dependency issue {batch}-{i}",
                    state = "todo",
                    effort = 2,
                    labels = new[] { "backend", "api" },
                }).ToArray(),
            });
            tasks.AddRange(created.EnumerateArray().Select(i => i.GetProperty("key").GetString()!));
        }

        (await TextAsync(mcp, "get_plan", new())).Length.ShouldBeLessThan(10_000, "ADR-001 IMP-003");
        (await TextAsync(mcp, "search_issues", new())).Length.ShouldBeLessThan(4_000, "ADR-001 IMP-003");
        (await TextAsync(mcp, "update_issues", new()
        {
            ["items"] = tasks.Take(100).Select(key => new { key, changes = new { state = "inProgress" } }).ToArray(),
        })).Length.ShouldBeLessThan(15_000, "ADR-001 IMP-003");
        (await TextAsync(mcp, "get_context", new())).Length.ShouldBeLessThan(6_000, "ADR-001 IMP-003");
        // The full view is still there for who asks, and is what the summary saves.
        (await TextAsync(mcp, "get_plan", new() { ["view"] = "full" })).Length.ShouldBeGreaterThan(100_000);
    }

    [Fact]
    public async Task Whats_on_my_plate_takes_two_calls()
    {
        var token = (await NewTokenAsync(_owner, new("me", ApiTokenKind.Personal))).Secret;
        await using var mcp = await ConnectAsync(token: token);
        await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[]
            {
                new { project = "MCP", title = "Mine", state = "inProgress", assignee = "me", description = "The details" },
                new { project = "MCP", title = "Finished", state = "done", assignee = "me" },
                new { project = "MCP", title = "Someone else's", state = "todo" },
            },
        });

        var context = await CallAsync(mcp, "get_context", new());
        var keys = context.GetProperty("myIssues").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToArray();
        var issues = await CallAsync(mcp, "get_issue", new() { ["keys"] = keys });

        keys.ShouldBe(["MCP-1"]);
        issues.GetProperty("issues").EnumerateArray().ShouldHaveSingleItem().GetProperty("description").GetString().ShouldBe("The details");
    }

    [Fact]
    public async Task Context_includes_only_what_is_asked_and_caps_members_and_labels()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[] { new { project = "MCP", title = "Many labels", labels = Enumerable.Range(0, 60).Select(i => $"label-{i}").ToArray() } },
        });

        var labels = await CallAsync(mcp, "get_context", new() { ["include"] = new[] { "labels" } });
        labels.GetProperty("labels").GetArrayLength().ShouldBe(50);
        labels.GetProperty("labelsTotal").GetInt32().ShouldBe(60);
        labels.GetProperty("me").GetProperty("agent").GetString().ShouldBe("claude");
        labels.GetProperty("workspaces").EnumerateArray().ShouldHaveSingleItem();
        labels.TryGetProperty("projects", out _).ShouldBeFalse();
        labels.TryGetProperty("members", out _).ShouldBeFalse();

        var all = await CallAsync(mcp, "get_context", new());
        all.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("kind").GetString()).ShouldContain("human");
        all.TryGetProperty("membersTotal", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Context_lists_only_workspaces_when_you_are_in_several_and_name_none()
    {
        var slug = (await ReadAsync<WorkspaceSummary>(await _owner.PostAsJsonAsync("/api/v1/workspaces",
            new CreateWorkspaceRequest("Second", "w-" + Guid.NewGuid().ToString("N")[..12]), Json.Options, Ct), HttpStatusCode.Created)).Slug;
        var token = (await NewTokenAsync(_owner, new("me", ApiTokenKind.Personal))).Secret;
        await using var mcp = await ConnectAsync(token: token);

        var context = await CallAsync(mcp, "get_context", new());
        context.GetProperty("workspaces").GetArrayLength().ShouldBe(2);
        context.TryGetProperty("projects", out _).ShouldBeFalse();

        var named = await CallAsync(mcp, "get_context", new() { ["workspace"] = slug });
        named.GetProperty("workspace").GetProperty("slug").GetString().ShouldBe(slug);
    }

    [Fact]
    public async Task Search_rows_are_compact_and_fields_add_the_rest()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "create_issues", new() { ["issues"] = new object[] { new { project = "MCP", title = "Row", dueDate = "2027-03-01" } } });

        var row = (await CallAsync(mcp, "search_issues", new())).GetProperty("items")[0];
        row.TryGetProperty("id", out _).ShouldBeFalse();
        row.TryGetProperty("rank", out _).ShouldBeFalse();
        row.TryGetProperty("updatedAt", out _).ShouldBeFalse();
        row.TryGetProperty("dueDate", out _).ShouldBeFalse();

        var asked = (await CallAsync(mcp, "search_issues", new() { ["fields"] = new[] { "dueDate", "id" } })).GetProperty("items")[0];
        asked.GetProperty("dueDate").GetString().ShouldBe("2027-03-01");
        asked.TryGetProperty("id", out _).ShouldBeTrue();
        asked.TryGetProperty("rank", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Get_issue_reads_many_keys_with_activity_and_reports_missing_ones_per_key()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[] { new { project = "MCP", title = "First" }, new { project = "MCP", title = "Second" } },
        });
        await CallAsync(mcp, "comment", new() { ["action"] = "add", ["key"] = "MCP-2", ["body"] = "A remark" });

        var result = await CallAsync(mcp, "get_issue", new() { ["keys"] = new[] { "mcp-2", "MCP-404", "MCP-1" }, ["include"] = new[] { "activity" } });

        result.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ShouldBe(["MCP-2", "MCP-1"]);
        var error = result.GetProperty("errors").EnumerateArray().ShouldHaveSingleItem();
        error.GetProperty("key").GetString().ShouldBe("MCP-404");
        error.GetProperty("code").GetString().ShouldBe("issue_not_found");
        var activity = result.GetProperty("activity");
        activity.GetProperty("MCP-1").GetArrayLength().ShouldBe(1);
        activity.GetProperty("MCP-2").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ShouldBe(["created", "comment"]);

        var plain = await CallAsync(mcp, "get_issue", new() { ["keys"] = new[] { "MCP-1" } });
        plain.TryGetProperty("activity", out _).ShouldBeFalse();
        plain.TryGetProperty("errors", out _).ShouldBeFalse();
        (await mcp.CallToolAsync("get_issue", new Dictionary<string, object?> { ["keys"] = Enumerable.Range(1, 21).Select(i => $"MCP-{i}").ToArray() }, cancellationToken: Ct))
            .IsError.ShouldBe(true);
    }

    [Fact]
    public async Task Danish_letters_and_plus_signs_come_through_unescaped()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "create_issues", new() { ["issues"] = new object[] { new { project = "MCP", title = "Rød grød med fløde på æbler" } } });

        var text = await TextAsync(mcp, "search_issues", new() { ["fields"] = new[] { "createdAt" } });

        text.ShouldContain("Rød grød med fløde på æbler");
        text.ShouldNotContain("\\u");
    }

    [Fact]
    public async Task Summary_plan_leaves_out_cards_and_names_projects_for_their_months()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[]
            {
                new { project = "MCP", title = "Blocker", type = "feature", effort = 10, startDate = "2027-01-01", dueDate = "2027-03-31" },
                new { project = "MCP", title = "Blocked", type = "feature", effort = 10, startDate = "2027-02-01", dueDate = "2027-03-31" },
            },
        });
        await CallAsync(mcp, "relate_issues", new() { ["key"] = "MCP-1", ["type"] = "blocks", ["target"] = "MCP-2" });
        var horizon = new Dictionary<string, object?> { ["from"] = "2027-01-01", ["to"] = "2027-03-01" };

        var summary = await CallAsync(mcp, "get_plan", horizon);
        summary.GetProperty("totals").GetArrayLength().ShouldBe(3);
        summary.GetProperty("violatedDependencies").GetInt32().ShouldBe(1);
        summary.GetProperty("dependencies").GetArrayLength().ShouldBe(0);
        var project = summary.GetProperty("projects")[0];
        project.GetProperty("cards").GetArrayLength().ShouldBe(0);
        project.GetProperty("months").GetArrayLength().ShouldBe(0);

        var named = await CallAsync(mcp, "get_plan", new(horizon) { ["project"] = new[] { "MCP" } });
        named.GetProperty("projects")[0].GetProperty("months").GetArrayLength().ShouldBe(3);

        var full = await CallAsync(mcp, "get_plan", new(horizon) { ["view"] = "full" });
        full.GetProperty("projects")[0].GetProperty("cards").GetArrayLength().ShouldBe(2);
        full.GetProperty("dependencies").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Agent_manages_issues_through_mcp()
    {
        await using var mcp = await ConnectAsync();

        var created = await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[]
            {
                new { project = "MCP", title = "Epic from agent", type = "epic" },
                new { project = "MCP", title = "Task from agent", parent = "#0", labels = new[] { "bug" } },
            },
        });
        created.EnumerateArray().Select(i => i.GetProperty("key").GetString()).ShouldBe(["MCP-1", "MCP-2"]);

        var updated = await CallAsync(mcp, "update_issues", new()
        {
            ["items"] = new object[] { new { key = "MCP-2", changes = new { state = "done" } } },
        });
        updated[0].GetProperty("state").GetString().ShouldBe("done");

        var page = await CallAsync(mcp, "search_issues", new() { ["label"] = "bug" });
        var only = page.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        (!only.TryGetProperty("assignee", out var assignee) || assignee.ValueKind == System.Text.Json.JsonValueKind.Null).ShouldBeTrue();
    }

    [Fact]
    public async Task Agent_runs_a_sprint_through_mcp()
    {
        await using var mcp = await ConnectAsync();

        var sprint = await CallAsync(mcp, "manage_sprint", new()
        {
            ["action"] = "create",
            ["project"] = "MCP",
            ["name"] = "Agent sprint",
            ["startDate"] = "2027-02-01",
            ["endDate"] = "2027-02-12",
        });
        var id = sprint.GetProperty("id").GetString();
        await CallAsync(mcp, "manage_sprint", new() { ["action"] = "start", ["id"] = id });
        await CallAsync(mcp, "create_issues", new() { ["issues"] = new object[] { new { project = "MCP", title = "In sprint", sprint = "active" } } });

        var sprints = await CallAsync(mcp, "list_sprints", new() { ["project"] = "MCP", ["state"] = new[] { "active" } });
        sprints.EnumerateArray().ShouldHaveSingleItem().GetProperty("issueCount").GetInt32().ShouldBe(1);

        var closed = await CallAsync(mcp, "manage_sprint", new() { ["action"] = "close", ["id"] = id, ["moveUnfinishedTo"] = "backlog" });
        closed.GetProperty("moved").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Agent_splits_what_is_in_review_at_half_its_effort_through_mcp()
    {
        await using var mcp = await ConnectAsync();
        var id = (await CallAsync(mcp, "manage_sprint", new()
        {
            ["action"] = "create",
            ["project"] = "MCP",
            ["name"] = "Fourteen",
            ["startDate"] = "2027-01-04",
            ["endDate"] = "2027-01-15",
        })).GetProperty("id").GetString();
        await CallAsync(mcp, "manage_sprint", new()
        {
            ["action"] = "create",
            ["project"] = "MCP",
            ["name"] = "Fifteen",
            ["startDate"] = "2027-01-18",
            ["endDate"] = "2027-01-29",
        });
        await CallAsync(mcp, "manage_sprint", new() { ["action"] = "start", ["id"] = id });
        await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[]
            {
                new { project = "MCP", title = "In review", state = "inReview", effort = 8, sprint = "active" },
                new { project = "MCP", title = "Not started", state = "todo", effort = 2, sprint = "active" },
            },
        });

        var review = await CallAsync(mcp, "get_sprint_review", new() { ["id"] = id });
        var items = review.GetProperty("unfinished").EnumerateArray()
            .Where(i => i.GetProperty("state").GetString() == "inReview")
            .Select(i => new { issue = i.GetProperty("key").GetString(), outcome = "split", hoursLeft = i.GetProperty("effort").GetDecimal() / 2 })
            .ToArray();
        var closed = await CallAsync(mcp, "manage_sprint", new() { ["action"] = "close", ["id"] = id, ["items"] = items, ["startNext"] = true });

        closed.GetProperty("splits")[0].GetProperty("hoursDone").GetDecimal().ShouldBe(4);
        closed.GetProperty("started").GetProperty("name").GetString().ShouldBe("Fifteen");
        var burndown = await CallAsync(mcp, "get_sprint_burndown", new() { ["id"] = id, ["by"] = "assignee" });
        burndown.GetProperty("assignees").EnumerateArray().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Agent_answers_do_we_need_to_hire_with_get_plan()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "set_capacity", new()
        {
            ["project"] = "MCP",
            ["entries"] = new object[] { new { fromMonth = "2027-04-01", people = 1 } },
        });
        await CallAsync(mcp, "create_issues", new()
        {
            ["issues"] = new object[] { new { project = "MCP", title = "Q2 push", type = "feature", effort = 600, startDate = "2027-04-01", dueDate = "2027-06-30" } },
        });

        var plan = await CallAsync(mcp, "get_plan", new() { ["from"] = "2027-04-01", ["to"] = "2027-06-01", ["project"] = new[] { "MCP" } });

        var april = plan.GetProperty("totals")[0];
        april.GetProperty("gapPeople").GetDecimal().ShouldBeGreaterThan(0m); // 600 h over Q2 is more than one person
        april.GetProperty("supplyPeople").GetDecimal().ShouldBe(1m);
    }

    [Fact]
    public async Task Service_errors_come_back_as_tool_errors_with_the_stable_code()
    {
        await using var mcp = await ConnectAsync();

        var result = await mcp.CallToolAsync("comment", new Dictionary<string, object?> { ["action"] = "add", ["key"] = "MCP-404", ["body"] = "Hello" }, cancellationToken: Ct);

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldContain("issue_not_found");
    }

    [Fact]
    public async Task Unauthenticated_clients_are_pointed_at_protected_resource_metadata()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("resource_metadata=");

        var metadata = JsonElement.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource", Ct));
        metadata.GetProperty("resource").GetString().ShouldEndWith("/mcp");
    }

    [Fact]
    public async Task Agent_runs_a_meeting_and_reviews_its_proposals_through_mcp()
    {
        await using var mcp = await ConnectAsync();
        await CallAsync(mcp, "create_issues", new() { ["issues"] = new[] { new { project = "MCP", title = "Standup item" } } });

        await CallAsync(mcp, "meeting", new() { ["action"] = "start", ["project"] = "MCP" });
        var said = await CallAsync(mcp, "meeting", new() { ["action"] = "say", ["text"] = "MCP-1 is done." });
        var id = said.GetProperty("proposals")[0].GetProperty("id").GetGuid();

        var reviewed = await CallAsync(mcp, "review_proposals", new() { ["items"] = new[] { new { id, decision = "approve" } } });
        reviewed[0].GetProperty("proposal").GetProperty("status").GetString().ShouldBe("applied");

        var undone = await CallAsync(mcp, "review_proposals", new() { ["items"] = new[] { new { id, decision = "undo" } } });
        undone[0].GetProperty("proposal").GetProperty("status").GetString().ShouldBe("undone");
        (await CallAsync(mcp, "get_issue", new() { ["keys"] = new[] { "MCP-1" } })).GetProperty("issues")[0].GetProperty("state").GetString().ShouldBe("backlog");
    }

    [Fact]
    public async Task Agent_cannot_reach_another_workspace()
    {
        var (_, theirs, _) = await NewWorkspaceAsync("Theirs");
        await using var mcp = await ConnectAsync();

        var result = await mcp.CallToolAsync("search_issues", new Dictionary<string, object?> { ["workspace"] = theirs.Slug }, cancellationToken: Ct);

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldContain("workspace_not_found");
    }

    private async Task<McpClient> ConnectAsync(string? protocolVersion = null, string? token = null)
    {
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _agentToken);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: Ct);
    }

    private static async Task<JsonElement> CallAsync(McpClient mcp, string tool, Dictionary<string, object?> args) =>
        JsonElement.Parse(await TextAsync(mcp, tool, args));

    private static async Task<string> TextAsync(McpClient mcp, string tool, Dictionary<string, object?> args)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: Ct);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.ShouldNotBe(true, text);
        return text;
    }
}
