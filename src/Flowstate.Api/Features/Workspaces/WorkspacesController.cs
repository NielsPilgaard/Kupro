using Flowstate.Api.Auth;
using Flowstate.Api.Common;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Workspaces;

/// <summary>Workspaces you belong to.</summary>
[ApiController]
[Route("api/v1/workspaces")]
public sealed class WorkspacesController(WorkspaceService workspaces) : ControllerBase
{
    /// <summary>List workspaces you are an active member of.</summary>
    [HttpGet(Name = "listWorkspaces")]
    public async Task<IReadOnlyList<WorkspaceSummary>> List(CancellationToken ct) =>
        await workspaces.ListMineAsync(User.GetUserId(), ct);

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

    /// <summary>Get a workspace, your role in it and its seat count.</summary>
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
    /// <summary>List members, including pending invites and deactivated members.</summary>
    [HttpGet(Name = "listMembers")]
    public Task<IReadOnlyList<MemberResponse>> List(CancellationToken ct) => workspaces.ListMembersAsync(ct);

    /// <summary>Invite someone by email. They join when they accept.</summary>
    [HttpPost(Name = "inviteMember")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType<MemberResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberResponse>> Invite(InviteMemberRequest request, CancellationToken ct)
    {
        var (result, member) = await workspaces.InviteAsync(request, ct);
        return result == MemberChangeResult.Ok
            ? StatusCode(StatusCodes.Status201Created, member)
            : this.Problem(StatusCodes.Status409Conflict, "already_member", "That email is already a member or invited.");
    }

    /// <summary>Change a member's role.</summary>
    [HttpPatch("{memberId:guid}", Name = "updateMember")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid memberId, UpdateMemberRequest request, CancellationToken ct) =>
        ToResult(await workspaces.UpdateMemberAsync(memberId, request, ct));

    /// <summary>Deactivate a member: access and their tokens for this workspace are revoked; history keeps their name.</summary>
    [HttpPost("{memberId:guid}/deactivate", Name = "deactivateMember")]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace administration is for people; agents are capped at Member.")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Deactivate(Guid memberId, CancellationToken ct) =>
        ToResult(await workspaces.DeactivateMemberAsync(memberId, ct));

    private IActionResult ToResult(MemberChangeResult result) => result switch
    {
        MemberChangeResult.Ok => NoContent(),
        MemberChangeResult.LastOwner => this.Problem(StatusCodes.Status409Conflict, "last_owner",
            "A workspace needs at least one active owner."),
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
    public async Task<ActionResult<WorkspaceSummary>> Accept(Guid memberId, CancellationToken ct) =>
        await workspaces.AcceptInviteAsync(User.GetUserId(), memberId, ct) is { } joined
            ? joined
            : this.Problem(StatusCodes.Status404NotFound, "invite_not_found");
}
