using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Kupro.Api.Features.Realtime;

/// <summary>
/// Who joined which workspace group, so a deactivated member's open connections stop hearing that workspace at once
/// instead of at their next reconnect. In memory, like the groups themselves (single instance, see
/// <see cref="WorkspaceHub"/>).
/// </summary>
public sealed class WorkspaceConnections(IHubContext<WorkspaceHub, IWorkspaceClient> hub)
{
    private sealed record Membership(Guid WorkspaceId, Guid UserId, string? AgentName);

    private readonly ConcurrentDictionary<string, Membership> _joined = new();

    public void Joined(string connectionId, Guid workspaceId, Guid userId, string? agentName) =>
        _joined[connectionId] = new Membership(workspaceId, userId, agentName);

    public void Left(string connectionId) => _joined.TryRemove(connectionId, out _);

    /// <summary>
    /// Removes the user's connections from the workspace group: all of them for a person (their agents go with them),
    /// or only the agent's when <paramref name="agentName"/> is set.
    /// </summary>
    public async Task EvictAsync(Guid workspaceId, Guid userId, string? agentName)
    {
        foreach (var (connectionId, joined) in _joined)
        {
            if (joined.WorkspaceId == workspaceId && joined.UserId == userId
                && (agentName is null || joined.AgentName == agentName)
                && _joined.TryRemove(new KeyValuePair<string, Membership>(connectionId, joined)))
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, WorkspaceHub.Group(workspaceId));
            }
        }
    }
}
