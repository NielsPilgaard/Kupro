using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Tokens;
using Flowstate.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Flowstate.Tests.Features;

/// <summary>Remote MCP server over streamable HTTP, driven by the official MCP client (task 006).</summary>
public sealed class McpTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private string _workspace = null!;
    private string _agentToken = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var (client, workspace, _) = await NewWorkspaceAsync();
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
        tools.Count.ShouldBeLessThanOrEqualTo(20);
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
                new { project = "MCP", title = "Task from agent", parent = "#0", labels = new[] { "bug" }, assignee = "me" },
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
        only.GetProperty("assignee").GetProperty("name").GetString().ShouldBe("claude");
    }

    [Fact]
    public async Task Service_errors_come_back_as_tool_errors_with_the_stable_code()
    {
        await using var mcp = await ConnectAsync();

        var result = await mcp.CallToolAsync("get_issue", new Dictionary<string, object?> { ["key"] = "MCP-404" }, cancellationToken: Ct);

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

        var metadata = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource", Ct)).RootElement;
        metadata.GetProperty("resource").GetString().ShouldEndWith("/mcp");
    }

    private async Task<McpClient> ConnectAsync()
    {
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _agentToken);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<JsonElement> CallAsync(McpClient mcp, string tool, Dictionary<string, object?> args)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: Ct);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.ShouldNotBe(true, text);
        return JsonDocument.Parse(text).RootElement;
    }
}
