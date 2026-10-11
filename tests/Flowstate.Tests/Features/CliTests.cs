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

    private async Task<JsonElement> RunJsonAsync(params string[] args)
    {
        var (exit, output, error) = await RunAsync(json: true, args);
        exit.ShouldBe(0, error);
        return JsonDocument.Parse(output).RootElement;
    }

    private async Task<(int Exit, string Out, string Error)> RunAsync(bool json, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var config = new CliConfig { InMemory = true, Url = "http://localhost", Token = _token, Workspace = _workspace };
        CliApp.Runtime.Value = new CliRuntime(config, stdout, stderr, Factory.Server.CreateHandler(), json);
        var parse = CliApp.BuildRoot().Parse(args);
        var exit = await parse.InvokeAsync(new System.CommandLine.InvocationConfiguration { Error = stderr, Output = stdout }, Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
