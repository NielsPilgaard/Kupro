using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Tokens;
using Flowstate.Cli;
using Flowstate.Tests.Infrastructure;
using CliApp = Flowstate.Cli.Cli;

namespace Flowstate.Tests.Features;

/// <summary>
/// Runs the real CLI command tree in-process against the test API (task 005 acceptance: an agent can create, update
/// and close an issue using only the CLI).
/// </summary>
public sealed class CliTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private string _token = null!;
    private string _workspace = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var (client, workspace, _) = await NewWorkspaceAsync();
        _workspace = workspace.Slug;
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{_workspace}/projects",
            new CreateProjectRequest("CLI", "CLI project"), Json.Options, Ct), HttpStatusCode.Created);
        _token = (await NewTokenAsync(client, new("cli", ApiTokenKind.Agent, "cli-agent", null, _workspace))).Secret;
    }

    [Fact]
    public async Task Agent_creates_updates_and_closes_an_issue_with_only_the_cli()
    {
        var created = await RunJsonAsync("issue", "create", "--project", "cli", "--title", "Ship the CLI",
            "--type", "task", "--priority", "high", "--label", "cli", "--effort", "3h", "--assignee", "me");
        var key = created.GetProperty("key").GetString()!;
        key.ShouldBe("CLI-1");
        created.GetProperty("assignee").GetProperty("name").GetString().ShouldBe("cli-agent");

        var updated = await RunJsonAsync("issue", "update", key, "--state", "in-progress", "--add-label", "bug", "--effort", "none");
        updated.GetProperty("state").GetString().ShouldBe("inProgress");
        updated.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(["bug", "cli"]);
        (!updated.TryGetProperty("effort", out var effort) || effort.ValueKind == JsonValueKind.Null).ShouldBeTrue();

        var closed = await RunJsonAsync("issue", "close", key);
        closed.GetProperty("state").GetString().ShouldBe("done");

        var list = await RunJsonAsync("issue", "list", "--state", "done");
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ShouldBe([key]);
    }

    [Fact]
    public async Task Create_many_reads_a_json_file_with_batch_parent_references()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, """
            { "issues": [
              { "project": "CLI", "title": "Epic", "type": "epic" },
              { "project": "CLI", "title": "Child", "parent": "#0" } ] }
            """, Ct);

        var (exit, output, error) = await RunAsync(json: true, "issue", "create-many", "--file", file);

        exit.ShouldBe(0, error);
        var created = JsonDocument.Parse(output).RootElement;
        created.GetArrayLength().ShouldBe(2);
        created[1].GetProperty("parentKey").GetString().ShouldBe(created[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Sprint_cycle_with_only_the_cli()
    {
        var one = await RunJsonAsync("sprint", "create", "-p", "CLI", "--name", "One", "--start", "2027-01-04", "--end", "2027-01-15");
        await RunJsonAsync("sprint", "create", "-p", "CLI", "--name", "Two", "--start", "2027-01-18", "--end", "2027-01-29");
        var id = one.GetProperty("id").GetString()!;
        await RunJsonAsync("sprint", "start", id);
        var issue = await RunJsonAsync("issue", "create", "-p", "CLI", "-t", "Sprint work", "--sprint", "active", "--effort", "2");

        var closed = await RunJsonAsync("sprint", "close", id);

        closed.GetProperty("moved").GetInt32().ShouldBe(1);
        closed.GetProperty("movedTo").GetProperty("name").GetString().ShouldBe("Two");
        var list = await RunJsonAsync("issue", "list", "--sprint", closed.GetProperty("movedTo").GetProperty("id").GetString()!);
        list.GetProperty("items")[0].GetProperty("key").GetString().ShouldBe(issue.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Capacity_and_plan_with_only_the_cli()
    {
        var steps = await RunJsonAsync("capacity", "set", "CLI", "3@2027-01", "5@2027-07");
        steps.GetArrayLength().ShouldBe(2);
        await RunJsonAsync("milestone", "create", "--name", "GA", "--date", "2027-06-30");
        await RunJsonAsync("issue", "create", "-p", "CLI", "-t", "Feature A", "--type", "feature", "--effort", "100", "--start", "2027-01-04", "--due", "2027-02-26");

        var plan = await RunJsonAsync("plan", "--from", "2027-01", "--to", "2027-08");

        plan.GetProperty("totals").GetArrayLength().ShouldBe(8);
        plan.GetProperty("totals")[6].GetProperty("supplyPeople").GetDecimal().ShouldBe(5m);
        plan.GetProperty("milestones")[0].GetProperty("name").GetString().ShouldBe("GA");
    }

    [Fact]
    public async Task Table_output_lists_issues_for_people()
    {
        await RunAsync(json: true, "issue", "create", "-p", "CLI", "-t", "Readable row");

        var (exit, output, _) = await RunAsync(json: false, "issue", "list");

        exit.ShouldBe(0);
        output.ShouldContain("KEY");
        output.ShouldContain("Readable row");
    }

    [Fact]
    public async Task Api_errors_print_the_stable_code_and_exit_1()
    {
        var (exit, _, error) = await RunAsync(json: true, "issue", "get", "CLI-999");

        exit.ShouldBe(1);
        error.ShouldContain("issue_not_found");
    }

    [Fact]
    public async Task Bad_enum_values_are_usage_errors()
    {
        var (exit, _, error) = await RunAsync(json: true, "issue", "list", "--state", "sleeping");

        exit.ShouldNotBe(0);
        error.ShouldContain("in-progress");
    }

    [Fact]
    public async Task Mcp_bridge_forwards_stdio_json_rpc_to_the_remote_server()
    {
        var stdin = new StringReader(string.Join(Environment.NewLine,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        var (exit, output, error) = await RunAsync(json: true, stdin, "mcp");

        exit.ShouldBe(0, error);
        var replies = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        replies.Select(r => r.GetProperty("id").GetInt32()).Order().ShouldBe([1, 2]);
        var tools = replies.Single(r => r.GetProperty("id").GetInt32() == 2).GetProperty("result").GetProperty("tools");
        tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ShouldContain("search_issues");
    }

    private async Task<JsonElement> RunJsonAsync(params string[] args)
    {
        var (exit, output, error) = await RunAsync(json: true, args);
        exit.ShouldBe(0, error);
        return JsonDocument.Parse(output).RootElement;
    }

    private Task<(int Exit, string Out, string Error)> RunAsync(bool json, params string[] args) => RunAsync(json, null, args);

    private async Task<(int Exit, string Out, string Error)> RunAsync(bool json, TextReader? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var config = new CliConfig { InMemory = true, Url = "http://localhost", Token = _token, Workspace = _workspace };
        CliApp.Runtime.Value = new CliRuntime(config, stdout, stderr, Factory.Server.CreateHandler(), json, stdin);
        var parse = CliApp.BuildRoot().Parse(args);
        var exit = await parse.InvokeAsync(new System.CommandLine.InvocationConfiguration { Error = stderr, Output = stdout }, Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
