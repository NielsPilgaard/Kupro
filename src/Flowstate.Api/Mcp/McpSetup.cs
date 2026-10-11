using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Flowstate.Api.Mcp;

/// <summary>Remote MCP server in the API process: streamable HTTP at <c>/mcp</c>, OAuth via Keycloak (ADR-001, ADR-005).</summary>
public static class McpSetup
{
    public const string Path = "/mcp";

    private const string Instructions =
        "Flowstate is an issue tracker. Work happens in a workspace; issues live in projects and are addressed by key " +
        "(FLOW-12). Types: epic > feature > task. States: backlog, todo, inProgress, inReview, done, canceled. " +
        "Effort is in hours. Start with list_workspaces and list_projects; use search_issues before creating duplicates.";

    public static IHostApplicationBuilder AddFlowstateMcp(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<McpWorkspace>();

        var json = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        // First, so it wins over the default (PascalCase) enum converter: same wire format as the REST API.
        json.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "flowstate", Version = Version };
                options.ServerInstructions = Instructions;
            })
            // Stateless: every JSON-RPC request is a plain authenticated HTTP request; nothing to pin to one instance.
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<FlowstateTools>(json);

        return builder;
    }

    /// <summary>
    /// Protected Resource Metadata (RFC 9728) for MCP clients, served by the API itself (ADR-005 IMP-004). The resource
    /// URL follows the request host so it is right behind the reverse proxy and in tests.
    /// </summary>
    public static void ConfigureResourceMetadata(McpAuthenticationOptions options, IConfiguration configuration)
    {
        options.Events.OnResourceMetadataRequest = context =>
        {
            var request = context.HttpContext.Request;
            var authority = configuration["Auth:PublicAuthority"];
            context.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = $"{request.Scheme}://{request.Host}{Path}",
                AuthorizationServers = authority is null ? [] : [authority],
                BearerMethodsSupported = ["header"],
                ScopesSupported = ["openid", "profile", "email"],
                ResourceName = "Flowstate",
            };
            return Task.CompletedTask;
        };
    }

    public static void MapFlowstateMcp(this WebApplication app) => app.MapMcp(Path).RequireAuthorization();

    private static string Version =>
        typeof(McpSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
