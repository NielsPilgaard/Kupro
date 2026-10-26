using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Workspaces;

namespace Kupro.Api.Tenancy;

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

    /// <summary>Billing access (ADR-013): Owners and people an Owner granted it; never agents.</summary>
    public bool CanManageBilling { get; private set; }

    public string? AgentName { get; private set; }

    /// <summary>Client surface, from the <c>X-Kupro-Client</c> header; MCP tools set it in-process.</summary>
    public Surface Surface { get; set; }

    /// <summary>Meeting proposal being applied while <see cref="ActAs"/> is in effect (task 015).</summary>
    public Guid? ProposalId { get; private set; }

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
        CanManageBilling = access.CanManageBilling;
        AgentName = agentName;
    }

    /// <summary>
    /// Acts as another member of the same workspace until disposed: the meeting agent applying a proposal (task 015),
    /// so activity, realtime events and comments name the agent and link the proposal.
    /// </summary>
    public IDisposable ActAs(Guid memberId, Surface surface, Guid proposalId)
    {
        var (member, via, proposal) = (MemberId, Surface, ProposalId);
        (MemberId, Surface, ProposalId) = (memberId, surface, proposalId);
        return new Restore(() => (MemberId, Surface, ProposalId) = (member, via, proposal));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>For background work and tests that act inside one workspace without an HTTP request.</summary>
    public void SetForSystem(Guid workspaceId)
    {
        if (WorkspaceId is not null)
        {
            throw new InvalidOperationException("Workspace already resolved for this request.");
        }

        WorkspaceId = workspaceId;
    }
}
