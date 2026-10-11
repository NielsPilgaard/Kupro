using Flowstate.Api.Features.Issues;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.SignalR;

namespace Flowstate.Api.Features.Realtime;

/// <summary>
/// Pushes changes to the current workspace's group. Feature services call it after SaveChanges succeeds. Best effort:
/// the change is already committed, so a failed push is logged, never thrown; clients refetch on reconnect.
/// </summary>
public sealed partial class RealtimeEvents(
    IHubContext<WorkspaceHub, IWorkspaceClient> hub, WorkspaceContext workspace, ILogger<RealtimeEvents> logger)
{
    public Task IssuesAsync(IReadOnlyList<IssueSummary> items, bool created = false) =>
        items.Count == 0 ? Task.CompletedTask
        : SendAsync(client => client.Issues(new IssuesEvent(items, created, workspace.MemberId, workspace.Surface)));

    public Task ChangedAsync(ChangeTopic topic, string? issueKey = null) =>
        SendAsync(client => client.Changed(new ChangedEvent(topic, issueKey, workspace.MemberId, workspace.Surface)));

    private async Task SendAsync(Func<IWorkspaceClient, Task> send)
    {
        if (workspace.WorkspaceId is not { } id)
        {
            return;
        }

        try
        {
            await send(hub.Clients.Group(WorkspaceHub.Group(id)));
        }
#pragma warning disable CA1031 // A push failure must not fail a write that has already committed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPushFailed(logger, ex, id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime push to workspace {WorkspaceId} failed")]
    private static partial void LogPushFailed(ILogger logger, Exception exception, Guid workspaceId);
}
