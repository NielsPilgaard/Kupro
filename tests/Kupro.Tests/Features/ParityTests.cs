using System.Reflection;
using System.Text.Json;
using Kupro.Api.Common;
using Kupro.Api.Mcp;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using CliApp = Kupro.Cli.Cli;

namespace Kupro.Tests.Features;

/// <summary>
/// ADR-001 parity: every REST operation has a CLI command and an MCP tool, or an explicit
/// <see cref="NoAgentSurfaceAttribute"/> with a reason. Adding an endpoint without a surface fails here.
/// </summary>
public sealed class ParityTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Every_operation_has_a_cli_command_and_an_mcp_tool_or_an_opt_out()
    {
        var operations = (await OperationsAsync()).Keys.ToHashSet();
        var optOuts = OptOuts();
        var cli = CliApp.CoveredOperations();
        var mcp = typeof(KuproTools).GetMethods()
            .SelectMany(m => m.GetCustomAttribute<CoversOperationsAttribute>()?.OperationIds ?? []).ToHashSet();

        var missingCli = operations.Where(op => !cli.Contains(op) && !OptedOut(op, AgentSurface.Cli)).Order().ToList();
        var missingMcp = operations.Where(op => !mcp.Contains(op) && !OptedOut(op, AgentSurface.Mcp)).Order().ToList();
        var unknown = cli.Concat(mcp).Where(op => !operations.Contains(op)).Distinct().Order().ToList();
        var redundant = optOuts.Where(o => (o.Value.HasFlag(AgentSurface.Mcp) && mcp.Contains(o.Key))
                                           || (o.Value.HasFlag(AgentSurface.Cli) && cli.Contains(o.Key)))
            .Select(o => o.Key).ToList();

        missingCli.ShouldBeEmpty("operations without a CLI command (add an ApiCommand or [NoAgentSurface(AgentSurface.Cli, reason)])");
        missingMcp.ShouldBeEmpty("operations without an MCP tool (add [CoversOperations] to a tool or [NoAgentSurface(AgentSurface.Mcp, reason)])");
        unknown.ShouldBeEmpty("CLI/MCP claim operation ids that don't exist");
        redundant.ShouldBeEmpty("operations opted out of a surface that covers them anyway");

        bool OptedOut(string op, AgentSurface surface) => optOuts.TryGetValue(op, out var s) && s.HasFlag(surface);
    }

    [Fact]
    public async Task Every_operation_has_a_typed_success_response()
    {
        var untyped = (await OperationsAsync())
            .Where(op =>
            {
                var success = op.Value.GetProperty("responses").EnumerateObject()
                    .Where(r => r.Name.StartsWith('2') && r.Name != "204").ToList();
                return success.Count > 0 && success.All(r => !r.Value.TryGetProperty("content", out _));
            })
            .Select(op => op.Key)
            .ToList();

        untyped.ShouldBeEmpty("generated clients can't type these responses; add [ProducesResponseType]");
    }

    private async Task<Dictionary<string, JsonElement>> OperationsAsync()
    {
        using var client = Factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", Ct));
        return doc.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .ToDictionary(op => op.Value.GetProperty("operationId").GetString()!, op => op.Value.Clone());
    }

    private static Dictionary<string, AgentSurface> OptOuts() =>
        typeof(McpSetup).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Select(m => (Route: m.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault(), OptOut: m.GetCustomAttribute<NoAgentSurfaceAttribute>()))
            .Where(x => x.Route?.Name is not null && x.OptOut is not null)
            .ToDictionary(x => x.Route!.Name!, x => x.OptOut!.Surfaces);
}
