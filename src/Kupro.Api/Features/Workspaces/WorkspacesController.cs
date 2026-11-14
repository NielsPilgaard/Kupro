using Kupro.Api.Auth;
using Kupro.Api.Common;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Workspaces;

/// <summary>Workspaces you belong to.</summary>
[ApiController]
[Route("api/v1/workspaces")]
public sealed class WorkspacesController(WorkspaceService workspaces) : ControllerBase
{
    /// <summary>List workspaces you are an active member of.</summary>
    [HttpGet(Name = "listWorkspaces")]
    public async Task<IReadOnlyList<WorkspaceSummary>> List(CancellationToken ct) =>
        await workspaces.ListMineAsync(User.GetUserId(), User.GetTokenWorkspaceId(), ct);

    /// <summary>Create a workspace. You become its Owner.</summary>
    [HttpPost(Name = "createWorkspace")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [Authorize(Policy = AuthExtensions.Policies.Human)]
    [ProducesResponseType<WorkspaceSummary>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkspaceSummary>> Create(CreateWorkspaceRequest request, CancellationToken ct) =>
        await workspaces.CreateAsync(User.GetUserId(), request, ct) is { } created
            ? StatusCode(StatusCodes.Status201Created, created)
            : this.Problem(StatusCodes.Status409Conflict, "slug_taken", $"The slug '{request.Slug}' is taken.");

    /// <summary>Get a workspace, your role and billing access in it, its seat count and its guests against the cap.</summary>
    [HttpGet("{workspace}", Name = "getWorkspace")]
    public Task<WorkspaceResponse> Get(CancellationToken ct) => workspaces.GetAsync(ct);

    /// <summary>Rename a workspace or change its hours per person per week.</summary>
    [HttpPatch("{workspace}", Name = "updateWorkspace")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    public Task<WorkspaceResponse> Update(UpdateWorkspaceRequest request, CancellationToken ct) =>
        workspaces.UpdateAsync(request, ct);
}

/// <summary>People and agents in a workspace.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/members")]
public sealed class MembersController(WorkspaceService workspaces) : ControllerBase
{
    /// <summary>List members, including pending invites, deactivated members and placeholders from imports.</summary>
    [HttpGet(Name = "listMembers")]
    public Task<IReadOnlyList<MemberResponse>> List(CancellationToken ct) => workspaces.ListMembersAsync(ct);

    /// <summary>Invite someone by email, optionally with billing access. They join when they accept.</summary>
    [HttpPost(Name = "inviteMember")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType<MemberResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberResponse>> Invite(InviteMemberRequest request, CancellationToken ct)
    {
        var (result, member, limit) = await workspaces.InviteAsync(request, ct);
        return result switch
        {
            MemberChangeResult.Ok => StatusCode(StatusCodes.Status201Created, member),
            MemberChangeResult.GuestLimitReached => this.GuestLimitProblem(limit!),
            _ => this.Problem(StatusCodes.Status409Conflict, "already_member", "That email is already a member or invited."),
        };
    }

    /// <summary>Change a member's role or billing access. Billing access is for people, not agents.</summary>
    [HttpPatch("{memberId:guid}", Name = "updateMember")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid memberId, UpdateMemberRequest request, CancellationToken ct)
    {
        var (result, limit) = await workspaces.UpdateMemberAsync(memberId, request, ct);
        return result == MemberChangeResult.GuestLimitReached ? this.GuestLimitProblem(limit!) : ToResult(result);
    }

    /// <summary>Deactivate a member: access and their tokens for this workspace are revoked; history keeps their name.</summary>
    [HttpPost("{memberId:guid}/deactivate", Name = "deactivateMember")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Deactivate(Guid memberId, CancellationToken ct) =>
        ToResult(await workspaces.DeactivateMemberAsync(memberId, ct));

    /// <summary>Invite the person behind a placeholder from an import. The same member joins, so their history follows.</summary>
    [HttpPost("{memberId:guid}/invite", Name = "invitePlaceholder")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType<MemberResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberResponse>> InvitePlaceholder(Guid memberId, InvitePlaceholderRequest request, CancellationToken ct)
    {
        var (result, member, limit) = await workspaces.InvitePlaceholderAsync(memberId, request, ct);
        return result switch
        {
            MemberChangeResult.Ok => member!,
            MemberChangeResult.GuestLimitReached => this.GuestLimitProblem(limit!),
            MemberChangeResult.AlreadyMember => this.Problem(StatusCodes.Status409Conflict, "already_member",
                "That email is already a member or invited. Merge the placeholder into them instead."),
            MemberChangeResult.EmailRequired => this.Problem(StatusCodes.Status400BadRequest, "email_required",
                "This placeholder has no email from the source. Say where to send the invite."),
            _ => this.Problem(StatusCodes.Status404NotFound, "member_not_found"),
        };
    }

    /// <summary>Merge a placeholder from an import into a person: every reference moves to them and the placeholder is deleted.</summary>
    [HttpPost("{memberId:guid}/merge", Name = "mergePlaceholder")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MergePlaceholder(Guid memberId, MergePlaceholderRequest request, CancellationToken ct) =>
        ToResult(await workspaces.MergePlaceholderAsync(memberId, request, ct));

    private IActionResult ToResult(MemberChangeResult result) => result switch
    {
        MemberChangeResult.Ok => NoContent(),
        MemberChangeResult.LastOwner => this.Problem(StatusCodes.Status409Conflict, "last_owner",
            "A workspace needs at least one active owner."),
        MemberChangeResult.BillingAccessHumansOnly => this.Problem(StatusCodes.Status400BadRequest,
            "billing_access_humans_only", "Billing access is for people, not agents."),
        MemberChangeResult.IsPlaceholder => this.Problem(StatusCodes.Status409Conflict, "member_is_placeholder",
            "A placeholder has no access to change. Invite it or merge it into a member."),
        MemberChangeResult.InvalidMergeTarget => this.Problem(StatusCodes.Status400BadRequest, "invalid_merge_target",
            "Merge into an active person or another placeholder."),
        MemberChangeResult.ImportRunning => this.Problem(StatusCodes.Status409Conflict, "import_running",
            "An import is writing to this workspace. Merge when it has finished."),
        _ => this.Problem(StatusCodes.Status404NotFound, "member_not_found"),
    };
}

/// <summary>Invites addressed to your verified email.</summary>
[ApiController]
[Route("api/v1/invites")]
[Authorize(Policy = AuthExtensions.Policies.Human)]
public sealed class InvitesController(WorkspaceService workspaces) : ControllerBase
{
    /// <summary>List your pending workspace invites.</summary>
    [HttpGet(Name = "listInvites")]
    [NoAgentSurface(AgentSurface.Mcp, "Invites are addressed to people.")]
    public Task<IReadOnlyList<InviteResponse>> List(CancellationToken ct) =>
        workspaces.ListInvitesAsync(User.GetUserId(), ct);

    /// <summary>Accept an invite and join the workspace.</summary>
    [HttpPost("{memberId:guid}/accept", Name = "acceptInvite")]
    [NoAgentSurface(AgentSurface.Mcp, "Invites are addressed to people.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkspaceSummary>> Accept(Guid memberId, CancellationToken ct)
    {
        var (result, joined, limit) = await workspaces.AcceptInviteAsync(User.GetUserId(), memberId, ct);
        return result switch
        {
            MemberChangeResult.Ok => joined!,
            MemberChangeResult.GuestLimitReached => this.GuestLimitProblem(limit!),
            _ => this.Problem(StatusCodes.Status404NotFound, "invite_not_found"),
        };
    }
}

internal static class GuestLimitProblems
{
    /// <summary>409 <c>guest_limit_reached</c> with <c>guests</c> and <c>guestLimit</c>, so clients can show both.</summary>
    public static ObjectResult GuestLimitProblem(this ControllerBase controller, GuestLimit limit)
    {
        var result = controller.Problem(StatusCodes.Status409Conflict, "guest_limit_reached",
            $"{limit.Guests} of {limit.Limit} free guests are taken. Add a seat, or deactivate a guest or a guest invite.");
        var problem = (ProblemDetails)result.Value!;
        problem.Extensions["guests"] = limit.Guests;
        problem.Extensions["guestLimit"] = limit.Limit;
        return result;
    }
}
