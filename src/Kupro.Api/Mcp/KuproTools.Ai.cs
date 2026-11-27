using System.ComponentModel;
using Kupro.Api.Common;
using Kupro.Api.Features.Ai;
using Kupro.Api.Features.Workspaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kupro.Api.Mcp;

/// <summary>
/// The workspace's AI setup, read-only (ADR-022 decision 9): agents see what runs where and whether it works, so they can
/// explain a stopped microphone. Changing connections is people's work, over the web or the CLI: keys never pass
/// through a model's context.
/// </summary>
public sealed partial class KuproTools
{
    [McpServerTool(Name = "ai", ReadOnly = true), Description(
        "The workspace's AI setup, read-only. Actions: status: connections (never keys) with health and last error, " +
        "what runs each capability (transcription, meetingProposals, voiceCommands), who pays (kupro: within the " +
        "monthly hours; workspace: its own key, no limit) and this month's usage | providers: provider types that can be " +
        "connected. A rejected connection means the microphone is off and text uses keyword matching until a person " +
        "with billing access replaces the key.")]
    [CoversOperations("getAiStatus", "listAiProviders")]
    public async Task<object> Ai(
        [Description("status or providers.")] string action = "status",
        string? workspace = null,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case "status":
                await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
                return await ai.StatusAsync(ct);
            case "providers":
                await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
                return AiConnectionService.Providers();
            default:
                throw new McpException("invalid_arguments: action is status or providers.");
        }
    }
}
