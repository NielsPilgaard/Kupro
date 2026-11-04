using Kupro.Api.Features.Issues;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.SignalR;

namespace Kupro.Api.Features.Realtime;

/// <summary>
/// Pushes changes to the current workspace's group. Feature services call it after SaveChanges succeeds. Best effort:
/// the change is already committed, so a failed push is logged, never thrown; clients refetch on reconnect. Also where
/// cached plans are invalidated, before the push so a failed push can't skip it and a client told to refetch reads fresh.
/// </summary>
public sealed partial class RealtimeEvents(
    IHubContext<WorkspaceHub, IWorkspaceClient> hub, WorkspaceContext workspace, WorkspaceCache cache, ILogger<RealtimeEvents> logger)
{
    public Task IssuesAsync(IReadOnlyList<IssueSummary> items, bool created = false)
    {
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        cache.Invalidate();
        return SendAsync(client => client.Issues(new IssuesEvent(items, created, workspace.MemberId, workspace.Surface)));
    }

    public Task ChangedAsync(ChangeTopic topic, string? issueKey = null)
    {
        // Issue is here because relations feed the plan's dependencies. Meetings change issues through IssueService.
        if (topic is ChangeTopic.Issue or ChangeTopic.Sprints or ChangeTopic.Plan)
        {
            cache.Invalidate();
        }

        return SendAsync(client => client.Changed(new ChangedEvent(topic, issueKey, workspace.MemberId, workspace.Surface)));
    }

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
