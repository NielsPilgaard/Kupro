using System.Globalization;
using System.Text;
using Kupro.Api.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Kupro.Api.Features.Discovery;

/// <summary>
/// How agents find their way into the app origin: an MCP server card and llms.txt (ADR-001). Not API operations, so they
/// stay out of OpenAPI and the parity check. Built from the registered tools, so they can't drift from the server.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
[ResponseCache(Duration = 3600)]
public sealed class DiscoveryController(IOptions<McpServerOptions> mcp) : ControllerBase
{
    private IEnumerable<(string Name, string? Description)> Tools =>
        (mcp.Value.ToolCollection ?? [])
            .Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    private string Origin => $"{Request.Scheme}://{Request.Host}";

    /// <summary>MCP server card (draft <c>/.well-known/mcp.json</c>).</summary>
    [HttpGet("/.well-known/mcp.json")]
    public McpServerCard McpCard() => new(
        "Kupro",
        McpSetup.Version,
        "Issue tracker and planner. Create, search and update issues, sprints, plans and meeting proposals.",
        "http",
        $"{Origin}{McpSetup.Path}",
        new("oauth2", $"{Origin}/.well-known/oauth-protected-resource"),
        new(Tools: true),
        McpSetup.Instructions,
        [.. Tools.Select(t => new McpCardTool(t.Name, t.Description))],
        $"{Origin}/llms.txt");

    /// <summary>llms.txt (llmstxt.org): what an agent can do here and where to start.</summary>
    [HttpGet("/llms.txt")]
    public ContentResult LlmsTxt()
    {
        var md = new StringBuilder()
            .AppendLine("# Kupro")
            .AppendLine()
            .AppendLine("> Issue tracker and planner. Agents get the same API as people: MCP, REST and the `kupro` CLI, with every change recorded under the agent's name.")
            .AppendLine()
            .AppendLine(McpSetup.Instructions)
            .AppendLine()
            .AppendLine("## Connect")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"- [MCP server]({Origin}{McpSetup.Path}): streamable HTTP. Sign in with OAuth (metadata at {Origin}/.well-known/oauth-protected-resource) or send `Authorization: Bearer <token>`. Card: {Origin}/.well-known/mcp.json")
            .AppendLine(CultureInfo.InvariantCulture, $"- [REST API (OpenAPI)]({Origin}/openapi/v1.json): base path `/api/v1`, `Authorization: Bearer <token>`.")
            .AppendLine("- Tokens: `kupro token create --name <label> --agent <agent-name>` creates an agent token (`kp_agent_…`); people use `kupro login`.")
            .AppendLine("- CLI: `kupro install mcp` registers Kupro in every MCP client on the machine.")
            .AppendLine()
            .AppendLine("## MCP tools")
            .AppendLine();
        foreach (var (name, description) in Tools)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- `{name}`: {description?.ReplaceLineEndings(" ")}");
        }

        return Content(md.ToString(), "text/markdown; charset=utf-8");
    }
}

/// <param name="Name">Server name.</param>
/// <param name="Version">Server build version.</param>
/// <param name="Description">What the server is for.</param>
/// <param name="Transport">Always <c>http</c> (streamable HTTP).</param>
/// <param name="Endpoint">MCP endpoint URL.</param>
/// <param name="Authentication">How clients sign in.</param>
/// <param name="Capabilities">MCP capabilities offered.</param>
/// <param name="Instructions">The server instructions clients get on initialize.</param>
/// <param name="Tools">Every tool, by name.</param>
/// <param name="Documentation">llms.txt URL.</param>
public sealed record McpServerCard(
    string Name,
    string Version,
    string Description,
    string Transport,
    string Endpoint,
    McpCardAuthentication Authentication,
    McpCardCapabilities Capabilities,
    string Instructions,
    IReadOnlyList<McpCardTool> Tools,
    string Documentation);

/// <param name="Type">Always <c>oauth2</c>.</param>
/// <param name="ProtectedResourceMetadata">RFC 9728 metadata URL.</param>
public sealed record McpCardAuthentication(string Type, string ProtectedResourceMetadata);

/// <param name="Tools">True: the server offers tools.</param>
public sealed record McpCardCapabilities(bool Tools);

/// <param name="Name">Tool name.</param>
/// <param name="Description">What the tool does.</param>
public sealed record McpCardTool(string Name, string? Description);
