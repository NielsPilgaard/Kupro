using Kupro.Api.Auth;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace Kupro.Api.Mcp;

/// <summary>
/// The MCP equivalent of <see cref="WorkspaceAccessFilter"/>: tools aren't controller actions, so each tool enters its
/// workspace explicitly. Same rules: active members only, agents capped at Member, actions recorded "via MCP".
/// </summary>
public sealed class McpWorkspace(
    IHttpContextAccessor http,
    WorkspaceService workspaces,
    WorkspaceContext context,
    IDbContextFactory<KuproDbContext> dbFactory)
{
    public Guid UserId => (http.HttpContext?.User ?? throw new McpException("No authenticated user.")).GetUserId();

    public string? AgentName => http.HttpContext?.User.GetAgentName();

    /// <summary>
    /// Resolves the workspace (argument, else the token's workspace, else the user's only workspace) and enforces the
    /// minimum role. Throws an <see cref="McpException"/> the client sees as a tool error.
    /// </summary>
    public async Task EnterAsync(string? slug, WorkspaceRole minimum, CancellationToken ct)
    {
        var user = http.HttpContext?.User ?? throw new McpException("No authenticated user.");
        slug ??= await DefaultSlugAsync(user.GetUserId(), user.GetTokenWorkspaceId(), ct);

        var access = await workspaces.ResolveAccessAsync(slug, user.GetUserId(), user.GetAgentName(), user.GetTokenWorkspaceId(), ct);
        switch (access.Outcome)
        {
            case WorkspaceAccessOutcome.NotFound:
                throw new McpException($"workspace_not_found: No workspace '{slug}' you can access. Call list_workspaces.");
            case WorkspaceAccessOutcome.Deactivated:
                throw new McpException("member_deactivated: Your membership in this workspace is deactivated.");
        }

        if (access.Role < minimum)
        {
            throw new McpException($"insufficient_role: Requires the {minimum} role.");
        }

        context.Set(access, slug, user.GetUserId(), user.GetAgentName());
        context.Surface = Surface.Mcp;
    }

    private async Task<string> DefaultSlugAsync(Guid userId, Guid? tokenWorkspaceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (tokenWorkspaceId is { } id)
        {
            return await db.Workspaces.Where(w => w.Id == id).Select(w => w.Slug).SingleAsync(ct);
        }

        var mine = await workspaces.ListMineAsync(userId, ct);
        return mine.Count == 1
            ? mine[0].Slug
            : throw new McpException(mine.Count == 0
                ? "workspace_not_found: You're not in any workspace."
                : "workspace_required: You're in several workspaces; pass `workspace` (see list_workspaces).");
    }
}
