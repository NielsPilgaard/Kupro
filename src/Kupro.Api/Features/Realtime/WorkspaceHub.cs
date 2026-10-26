using Kupro.Api.Auth;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Kupro.Api.Features.Realtime;

/// <summary>Issues created or changed: full rows, so open lists patch in place.</summary>
/// <param name="ActorId">Member who made the change.</param>
public sealed record IssuesEvent(IReadOnlyList<IssueSummary> Items, bool Created, Guid ActorId, Surface Via);

public enum ChangeTopic
{
    /// <summary>One issue's detail and activity (comments, relations).</summary>
    Issue,
    Sprints,
    Plan,

    /// <summary>Meetings and their proposals (task 015).</summary>
    Meetings,
}

/// <summary>Something clients should refetch; <c>IssueKey</c> is set for <see cref="ChangeTopic.Issue"/>.</summary>
public sealed record ChangedEvent(ChangeTopic Topic, string? IssueKey, Guid ActorId, Surface Via);

/// <summary>Server-to-client calls; the JS client registers handlers by these names (case-insensitive).</summary>
public interface IWorkspaceClient
{
    Task Issues(IssuesEvent change);

    Task Changed(ChangedEvent change);
}

/// <summary>
/// Realtime updates (task 012, ADR-002): one SignalR group per workspace. A connection joins one workspace after the
/// same membership check the REST routes use. Single instance for now; fan-out across instances would go through
/// Postgres LISTEN/NOTIFY.
/// </summary>
[Authorize]
public sealed class WorkspaceHub(WorkspaceService workspaces, WorkspaceConnections connections) : Hub<IWorkspaceClient>
{
    public const string Path = "/api/v1/realtime";
    private const string JoinedKey = "workspace";

    public static string Group(Guid workspaceId) => $"ws:{workspaceId}";

    /// <summary>Joins the workspace's group, leaving any previous one. False when the caller isn't a member.</summary>
    public async Task<bool> Join(string workspace)
    {
        var user = Context.User!;
        var access = await workspaces.ResolveAccessAsync(
            workspace, user.GetUserId(), user.GetAgentName(), user.GetTokenWorkspaceId(), Context.ConnectionAborted);
        if (access.Outcome != WorkspaceAccessOutcome.Allowed)
        {
            return false;
        }

        if (Context.Items.TryGetValue(JoinedKey, out var previous) && previous is Guid old && old != access.WorkspaceId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(old), Context.ConnectionAborted);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(access.WorkspaceId), Context.ConnectionAborted);
        Context.Items[JoinedKey] = access.WorkspaceId;
        connections.Joined(Context.ConnectionId, access.WorkspaceId, user.GetUserId(), user.GetAgentName());
        return true;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Left(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
