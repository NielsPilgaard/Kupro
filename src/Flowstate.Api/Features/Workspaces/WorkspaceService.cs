using System.ComponentModel.DataAnnotations;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Email;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowstate.Api.Features.Workspaces;

/// <param name="Name">Display name, e.g. "Acme".</param>
/// <param name="Slug">URL id: lowercase letters, digits and dashes, e.g. <c>acme</c>. Immutable.</param>
public sealed record CreateWorkspaceRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [Required, StringLength(40, MinimumLength = 2), RegularExpression("^[a-z0-9][a-z0-9-]*[a-z0-9]$")] string Slug);

/// <param name="Name">New display name.</param>
/// <param name="HoursPerWeek">Capacity per person per week used by the delivery plan.</param>
public sealed record UpdateWorkspaceRequest(
    [StringLength(100, MinimumLength = 1)] string? Name = null,
    [Range(1, 80)] decimal? HoursPerWeek = null);

/// <param name="Id">Workspace id.</param>
/// <param name="Slug">URL id used in every workspace route.</param>
/// <param name="Name">Display name.</param>
/// <param name="Role">Your role in this workspace.</param>
public sealed record WorkspaceSummary(Guid Id, string Slug, string Name, WorkspaceRole Role);

/// <param name="Id">Workspace id.</param>
/// <param name="Slug">URL id.</param>
/// <param name="Name">Display name.</param>
/// <param name="HoursPerWeek">Capacity per person per week.</param>
/// <param name="Role">Your role.</param>
/// <param name="Seats">Active human owners and members (ADR-013).</param>
public sealed record WorkspaceResponse(Guid Id, string Slug, string Name, decimal HoursPerWeek, WorkspaceRole Role, int Seats);

/// <param name="Email">Who to invite.</param>
/// <param name="Role">Role they get on accepting.</param>
public sealed record InviteMemberRequest([Required, EmailAddress, StringLength(320)] string Email, WorkspaceRole Role);

/// <param name="Role">New role.</param>
public sealed record UpdateMemberRequest(WorkspaceRole Role);

/// <param name="Id">Member id.</param>
/// <param name="Kind">Human or agent.</param>
/// <param name="Name">Display name; the agent name for agents, the email for pending invites.</param>
/// <param name="Email">Email for humans and invites.</param>
/// <param name="Role">Workspace role.</param>
/// <param name="Status">Invited, active or deactivated.</param>
/// <param name="OwnerName">For agents: the person the agent acts for.</param>
/// <param name="JoinedAt">When the member became active.</param>
public sealed record MemberResponse(
    Guid Id,
    MemberKind Kind,
    string Name,
    string? Email,
    WorkspaceRole Role,
    MemberStatus Status,
    string? OwnerName,
    DateTimeOffset? JoinedAt);

/// <param name="MemberId">Id to accept the invite with.</param>
/// <param name="WorkspaceSlug">Workspace URL id.</param>
/// <param name="WorkspaceName">Workspace name.</param>
/// <param name="Role">Role you get on accepting.</param>
/// <param name="InvitedAt">When the invite was sent.</param>
public sealed record InviteResponse(Guid MemberId, string WorkspaceSlug, string WorkspaceName, WorkspaceRole Role, DateTimeOffset InvitedAt);

public enum WorkspaceAccessOutcome
{
    Allowed,
    NotFound,
    Deactivated,
}

public sealed record WorkspaceAccess(WorkspaceAccessOutcome Outcome, Guid WorkspaceId, Guid MemberId, WorkspaceRole Role)
{
    public static readonly WorkspaceAccess NotFound = new(WorkspaceAccessOutcome.NotFound, Guid.Empty, Guid.Empty, default);
    public static readonly WorkspaceAccess Deactivated = new(WorkspaceAccessOutcome.Deactivated, Guid.Empty, Guid.Empty, default);
}

public enum MemberChangeResult
{
    Ok,
    NotFound,
    LastOwner,
    AlreadyMember,
}

/// <summary>Workspaces, members, invites and seats (task 003). Owns the Workspace and Member entities.</summary>
public sealed class WorkspaceService(
    IDbContextFactory<FlowstateDbContext> dbFactory,
    WorkspaceContext workspace,
    IEmailSender email,
    TimeProvider clock)
{
    public const decimal DefaultHoursPerWeek = 37m;

    /// <summary>Decides whether the caller may act in the workspace and as whom. One query per request.</summary>
    public async Task<WorkspaceAccess> ResolveAccessAsync(
        string slug, Guid userId, string? agentName, Guid? tokenWorkspaceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // The tenant is being resolved here, so no workspace filter can apply yet.
        var members = db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]);
        var row = await db.Workspaces.AsNoTracking()
            .Where(w => w.Slug == slug)
            .Select(w => new
            {
                w.Id,
                Human = members
                    .Where(m => m.WorkspaceId == w.Id && m.Kind == MemberKind.Human && m.UserId == userId)
                    .Select(m => new { m.Id, m.Role, m.Status })
                    .FirstOrDefault(),
                Agent = members
                    .Where(m => m.WorkspaceId == w.Id && m.Kind == MemberKind.Agent && m.AgentName == agentName
                                && m.UserId == userId)
                    .Select(m => new { m.Id, m.Status })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null || (tokenWorkspaceId is not null && tokenWorkspaceId != row.Id)
            || row.Human is null || row.Human.Status == MemberStatus.Invited)
        {
            return WorkspaceAccess.NotFound;
        }

        if (row.Human.Status == MemberStatus.Deactivated)
        {
            return WorkspaceAccess.Deactivated;
        }

        if (agentName is null)
        {
            return new WorkspaceAccess(WorkspaceAccessOutcome.Allowed, row.Id, row.Human.Id, row.Human.Role);
        }

        if (row.Agent is null)
        {
            return WorkspaceAccess.NotFound;
        }

        // Agents never administer the workspace: they get their owner's role, capped at Member.
        var agentRole = row.Human.Role > WorkspaceRole.Member ? WorkspaceRole.Member : row.Human.Role;
        return row.Agent.Status == MemberStatus.Active
            ? new WorkspaceAccess(WorkspaceAccessOutcome.Allowed, row.Id, row.Agent.Id, agentRole)
            : WorkspaceAccess.Deactivated;
    }

    /// <summary>Creates a workspace with the caller as Owner. Null if the slug is taken.</summary>
    public async Task<WorkspaceSummary?> CreateAsync(Guid userId, CreateWorkspaceRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var created = new Workspace
        {
            Id = Guid.CreateVersion7(),
            Slug = request.Slug,
            Name = request.Name.Trim(),
            HoursPerWeek = DefaultHoursPerWeek,
            CreatedAt = now,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Workspaces.AnyAsync(w => w.Slug == request.Slug, ct))
        {
            return null;
        }

        db.Workspaces.Add(created);
        db.Members.Add(NewMember(created.Id, MemberKind.Human, userId, WorkspaceRole.Owner, MemberStatus.Active, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return null;
        }

        return new WorkspaceSummary(created.Id, created.Slug, created.Name, WorkspaceRole.Owner);
    }

    /// <summary>Workspaces the user is an active member of.</summary>
    public async Task<IReadOnlyList<WorkspaceSummary>> ListMineAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Spans workspaces by design: the list a user picks a workspace from. Restricted to the user's own memberships.
        return await (
                from m in db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]).AsNoTracking()
                where m.UserId == userId && m.Kind == MemberKind.Human && m.Status == MemberStatus.Active
                join w in db.Workspaces on m.WorkspaceId equals w.Id
                orderby w.Name
                select new WorkspaceSummary(w.Id, w.Slug, w.Name, m.Role))
            .ToListAsync(ct);
    }

    public async Task<WorkspaceResponse> GetAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = workspace.RequireWorkspaceId();
        return await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new WorkspaceResponse(w.Id, w.Slug, w.Name, w.HoursPerWeek, workspace.Role, db.Members.Count(m =>
                m.Kind == MemberKind.Human && m.Status == MemberStatus.Active
                && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Member))))
            .SingleAsync(ct);
    }

    public async Task<WorkspaceResponse> UpdateAsync(UpdateWorkspaceRequest request, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var current = await db.Workspaces.SingleAsync(w => w.Id == workspace.RequireWorkspaceId(), ct);
            current.Name = request.Name?.Trim() ?? current.Name;
            current.HoursPerWeek = request.HoursPerWeek ?? current.HoursPerWeek;
            await db.SaveChangesAsync(ct);
        }

        return await GetAsync(ct);
    }

    /// <summary>Seats as defined in ADR-013: active human Owners and Members.</summary>
    public async Task<int> CountSeatsAsync(CancellationToken ct) => (await GetAsync(ct)).Seats;

    public async Task<IReadOnlyList<MemberResponse>> ListMembersAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Members.AsNoTracking()
            .OrderBy(m => m.Status).ThenBy(m => m.Kind).ThenBy(m => m.CreatedAt)
            .Select(m => new
            {
                Member = m,
                User = db.Users.Where(u => u.Id == m.UserId).Select(u => new { u.DisplayName, u.Email }).FirstOrDefault(),
            })
            .Select(x => new MemberResponse(
                x.Member.Id,
                x.Member.Kind,
                x.Member.Kind == MemberKind.Agent ? x.Member.AgentName!
                    : x.User != null ? x.User.DisplayName : x.Member.InviteEmail!,
                x.Member.Kind == MemberKind.Agent ? null : x.User != null ? x.User.Email : x.Member.InviteEmail,
                x.Member.Role,
                x.Member.Status,
                x.Member.Kind == MemberKind.Agent && x.User != null ? x.User.DisplayName : null,
                x.Member.JoinedAt))
            .ToListAsync(ct);
    }

    /// <summary>Invites by email; the invitee joins when they accept. Sends the invite email after saving.</summary>
    public async Task<(MemberChangeResult Result, MemberResponse? Member)> InviteAsync(
        InviteMemberRequest request, CancellationToken ct)
    {
        var inviteEmail = request.Email.Trim().ToLowerInvariant();
        var workspaceId = workspace.RequireWorkspaceId();
        var now = clock.GetUtcNow();
        string workspaceName;
        Member invite;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var taken = await db.Members.AnyAsync(m =>
                (m.Status == MemberStatus.Invited && m.InviteEmail == inviteEmail)
                || (m.Kind == MemberKind.Human && m.Status != MemberStatus.Invited
                    && db.Users.Any(u => u.Id == m.UserId && u.Email == inviteEmail)), ct);
            if (taken)
            {
                return (MemberChangeResult.AlreadyMember, null);
            }

            invite = NewMember(workspaceId, MemberKind.Human, null, request.Role, MemberStatus.Invited, now);
            invite.InviteEmail = inviteEmail;
            db.Members.Add(invite);
            await db.SaveChangesAsync(ct);
            workspaceName = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.Name).SingleAsync(ct);
        }

        await email.SendAsync(new EmailMessage(
            inviteEmail,
            $"You're invited to {workspaceName} on Flowstate",
            $"Sign in to Flowstate with {inviteEmail} to accept. Workspace: {workspace.Slug}."), ct);

        return (MemberChangeResult.Ok, new MemberResponse(
            invite.Id, invite.Kind, inviteEmail, inviteEmail, invite.Role, invite.Status, null, null));
    }

    public async Task<MemberChangeResult> UpdateMemberAsync(Guid memberId, UpdateMemberRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.Members.SingleOrDefaultAsync(m => m.Id == memberId && m.Kind == MemberKind.Human, ct);
        if (member is null)
        {
            return MemberChangeResult.NotFound;
        }

        if (member.Role == WorkspaceRole.Owner && request.Role != WorkspaceRole.Owner && await IsLastOwnerAsync(db, member, ct))
        {
            return MemberChangeResult.LastOwner;
        }

        member.Role = request.Role;
        await db.SaveChangesAsync(ct);
        return MemberChangeResult.Ok;
    }

    /// <summary>
    /// Deactivates a member (never deletes). A human's agents in this workspace are deactivated with them, and their
    /// tokens for this workspace are revoked in the same transaction (ADR-013 IMP-002).
    /// </summary>
    public async Task<MemberChangeResult> DeactivateMemberAsync(Guid memberId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.Members.SingleOrDefaultAsync(m => m.Id == memberId, ct);
        if (member is null)
        {
            return MemberChangeResult.NotFound;
        }

        if (member.Status == MemberStatus.Deactivated)
        {
            return MemberChangeResult.Ok;
        }

        if (member.Role == WorkspaceRole.Owner && member.Kind == MemberKind.Human && await IsLastOwnerAsync(db, member, ct))
        {
            return MemberChangeResult.LastOwner;
        }

        var now = clock.GetUtcNow();
        // Retrying execution strategy: the transaction must run inside it as one retriable unit.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DeactivateWithTokensAsync(db, member, now, ct);
            await tx.CommitAsync(ct);
        });
        return MemberChangeResult.Ok;
    }

    private static async Task DeactivateWithTokensAsync(
        FlowstateDbContext db, Member member, DateTimeOffset now, CancellationToken ct)
    {
        var affected = db.Members.Where(m => m.Id == member.Id
            || (member.Kind == MemberKind.Human && m.Kind == MemberKind.Agent && m.UserId == member.UserId));
        await affected.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MemberStatus.Deactivated)
            .SetProperty(m => m.DeactivatedAt, now), ct);

        if (member.UserId is { } userId)
        {
            var tokens = db.ApiTokens.Where(t => t.WorkspaceId == member.WorkspaceId && t.UserId == userId && t.RevokedAt == null);
            if (member.Kind == MemberKind.Agent)
            {
                tokens = tokens.Where(t => t.AgentName == member.AgentName);
            }

            await tokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        }
    }

    /// <summary>Pending invites addressed to the user's verified email, across workspaces.</summary>
    public async Task<IReadOnlyList<InviteResponse>> ListInvitesAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var email = await VerifiedEmailAsync(db, userId, ct);
        if (email is null)
        {
            return [];
        }

        // Invites are addressed to a person, not a workspace the caller is in yet: cross-tenant by design,
        // restricted to invites for the caller's own verified email.
        return await db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]).AsNoTracking()
            .Where(m => m.Status == MemberStatus.Invited && m.InviteEmail == email)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id,
                (m, w) => new InviteResponse(m.Id, w.Slug, w.Name, m.Role, m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<WorkspaceSummary?> AcceptInviteAsync(Guid userId, Guid memberId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var email = await VerifiedEmailAsync(db, userId, ct);
        // Same cross-tenant reasoning as ListInvitesAsync.
        var invite = email is null ? null : await db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter])
            .SingleOrDefaultAsync(m => m.Id == memberId && m.Status == MemberStatus.Invited && m.InviteEmail == email, ct);
        if (invite is null)
        {
            return null;
        }

        var alreadyMember = await db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter])
            .AnyAsync(m => m.WorkspaceId == invite.WorkspaceId && m.Kind == MemberKind.Human && m.UserId == userId, ct);
        if (alreadyMember)
        {
            return null;
        }

        invite.UserId = userId;
        invite.Status = MemberStatus.Active;
        invite.JoinedAt = clock.GetUtcNow();
        invite.InviteEmail = null;
        await db.SaveChangesAsync(ct);

        return await db.Workspaces.Where(w => w.Id == invite.WorkspaceId)
            .Select(w => new WorkspaceSummary(w.Id, w.Slug, w.Name, invite.Role))
            .SingleAsync(ct);
    }

    /// <summary>
    /// For agent tokens: the workspace id if the user may create an agent there (active Member or Owner), creating
    /// or reusing the agent member. Null if not allowed or the name belongs to another user's agent.
    /// </summary>
    public async Task<Guid?> EnsureAgentMemberAsync(string slug, Guid userId, string agentName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Called outside a workspace route (token creation), so the tenant is resolved explicitly from the slug.
        var members = db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]);
        var workspaceId = await WorkspaceIdForActiveMemberAsync(db, slug, userId, WorkspaceRole.Member, ct);
        if (workspaceId is null)
        {
            return null;
        }

        var agent = await members.SingleOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.Kind == MemberKind.Agent && m.AgentName == agentName, ct);
        if (agent is not null)
        {
            return agent.UserId == userId && agent.Status == MemberStatus.Active ? workspaceId : null;
        }

        var created = NewMember(workspaceId.Value, MemberKind.Agent, userId, WorkspaceRole.Member, MemberStatus.Active,
            clock.GetUtcNow());
        created.AgentName = agentName;
        db.Members.Add(created);
        await db.SaveChangesAsync(ct);
        return workspaceId;
    }

    /// <summary>Workspace id for a personal token scoped to a workspace the user is active in.</summary>
    public async Task<Guid?> WorkspaceIdForMemberAsync(string slug, Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WorkspaceIdForActiveMemberAsync(db, slug, userId, WorkspaceRole.Guest, ct);
    }

    private static Task<Guid?> WorkspaceIdForActiveMemberAsync(
        FlowstateDbContext db, string slug, Guid userId, WorkspaceRole minimum, CancellationToken ct) =>
        // Resolving a tenant from its slug for the user's own membership.
        db.Members.IgnoreQueryFilters([FlowstateDbContext.TenantFilter])
            .Where(m => m.Kind == MemberKind.Human && m.UserId == userId && m.Status == MemberStatus.Active && m.Role >= minimum)
            .Join(db.Workspaces.Where(w => w.Slug == slug), m => m.WorkspaceId, w => w.Id, (m, w) => (Guid?)w.Id)
            .SingleOrDefaultAsync(ct);

    private static Task<string?> VerifiedEmailAsync(FlowstateDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId && u.EmailVerified).Select(u => u.Email).SingleOrDefaultAsync(ct);

    private static async Task<bool> IsLastOwnerAsync(FlowstateDbContext db, Member member, CancellationToken ct) =>
        !await db.Members.AnyAsync(m => m.Id != member.Id && m.Kind == MemberKind.Human
                                        && m.Role == WorkspaceRole.Owner && m.Status == MemberStatus.Active, ct);

    private static Member NewMember(
        Guid workspaceId, MemberKind kind, Guid? userId, WorkspaceRole role, MemberStatus status, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            Kind = kind,
            UserId = userId,
            Role = role,
            Status = status,
            CreatedAt = now,
            JoinedAt = status == MemberStatus.Active ? now : null,
        };
}
