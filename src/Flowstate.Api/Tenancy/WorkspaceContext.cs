using Flowstate.Api.Features.Workspaces;

namespace Flowstate.Api.Tenancy;

/// <summary>Marks an entity as tenant-owned: it gets the global workspace query filter (ADR-009).</summary>
public interface ITenantOwned
{
    Guid WorkspaceId { get; }
}

/// <summary>
/// The workspace and acting member of the current request. Set once by <see cref="WorkspaceAccessFilter"/> for routes
/// with a <c>{workspace}</c> segment; read by services and by the DbContext's tenant filter.
/// </summary>
public sealed class WorkspaceContext
{
    public Guid? WorkspaceId { get; private set; }
    public string Slug { get; private set; } = "";

    /// <summary>Acting member: the agent member for agent tokens, otherwise the user's own membership.</summary>
    public Guid MemberId { get; private set; }

    public Guid UserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
    public string? AgentName { get; private set; }

    public Guid RequireWorkspaceId() =>
        WorkspaceId ?? throw new InvalidOperationException("No workspace resolved for this request.");

    internal void Set(WorkspaceAccess access, string slug, Guid userId, string? agentName)
    {
        if (WorkspaceId is not null)
        {
            throw new InvalidOperationException("Workspace already resolved for this request.");
        }

        WorkspaceId = access.WorkspaceId;
        Slug = slug;
        MemberId = access.MemberId;
        UserId = userId;
        Role = access.Role;
        AgentName = agentName;
    }

    /// <summary>For background work and tests that act inside one workspace without an HTTP request.</summary>
    public void SetForSystem(Guid workspaceId) => WorkspaceId = workspaceId;
}
