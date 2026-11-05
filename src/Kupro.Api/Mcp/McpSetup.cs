using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Kupro.Api.Mcp;

/// <summary>Remote MCP server in the API process: streamable HTTP at <c>/mcp</c>, OAuth via Keycloak (ADR-001, ADR-005).</summary>
public static class McpSetup
{
    public const string Path = "/mcp";

    private const string Instructions =
        "Kupro is an issue tracker. Work happens in a workspace; issues live in projects and are addressed by key " +
        "(KUP-12). Types: epic > feature > task. States: backlog, todo, inProgress, inReview, done, canceled. " +
        "Effort is in hours. Start with get_context; use search_issues before creating duplicates. " +
        "Create an epic with its children in one create_issues call (parent: \"#0\" points at the first item). " +
        "Descriptions and comments are Markdown. " +
        "Tools take a workspace slug, which you can leave out when your token is limited to one workspace or you are in only one.";

    /// <summary>The tool list only changes with a deploy. A hint for clients on protocol 2026-07-28; older ones ignore the field (ADR-001 IMP-004).</summary>
    private static readonly TimeSpan ToolListTimeToLive = TimeSpan.FromHours(1);

    public static IHostApplicationBuilder AddKuproMcp(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<McpWorkspace>();

        var json = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        // First, so it wins over the default (PascalCase) enum converter: same wire format as the REST API.
        json.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        // Output is JSON in a text block, never HTML, and the default encoder turns æ, ø, å and + into costly \uXXXX.
        json.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "kupro", Version = Version };
                options.ServerInstructions = Instructions;
            })
            // Stateless: every JSON-RPC request is a plain authenticated HTTP request; nothing to pin to one instance.
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<KuproTools>(json)
            .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                result.TimeToLive = ToolListTimeToLive;
                // Sorted, so the list is byte-identical across instances and restarts and client prompt caches keep hitting.
                result.Tools = [.. result.Tools.OrderBy(t => t.Name, StringComparer.Ordinal)];
                foreach (var tool in result.Tools)
                {
                    var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!;
                    CompactSchema(schema);
                    tool.InputSchema = JsonSerializer.SerializeToElement(schema, McpJsonUtilities.DefaultOptions);
                }

                return result;
            }));

        return builder;
    }

    /// <summary>
    /// Every session pays for the tool list, and an optional parameter already says it can be left out: drops
    /// <c>"default": null</c> and the <c>null</c> in <c>"type": ["string", "null"]</c> and in enums. Explicit nulls still bind.
    /// </summary>
    private static void CompactSchema(JsonNode? node)
    {
        if (node is JsonArray list)
        {
            foreach (var item in list)
            {
                CompactSchema(item);
            }
        }

        if (node is not JsonObject schema)
        {
            return;
        }

        if (schema.TryGetPropertyValue("default", out var value) && value is null)
        {
            schema.Remove("default");
        }

        if (schema["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null"))
        {
            var named = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").ToList();
            schema["type"] = named.Count == 1 ? named[0] : new JsonArray([.. named.Select(t => JsonValue.Create(t))]);
        }

        if (schema["enum"] is JsonArray values)
        {
            values.Remove(null);
        }

        foreach (var (_, child) in schema)
        {
            CompactSchema(child);
        }
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
                ResourceName = "Kupro",
            };
            return Task.CompletedTask;
        };
    }

    public static void MapKuproMcp(this WebApplication app) => app.MapMcp(Path).RequireAuthorization();

    private static string Version =>
        typeof(McpSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
