using System.CommandLine;
using Flowstate.Cli.Api.Models;

namespace Flowstate.Cli.Commands;

/// <summary>workspace, member, invite.</summary>
internal static class WorkspaceCommands
{
    public static IEnumerable<Command> Build()
    {
        yield return Workspace();
        yield return Member();
        yield return Invite();
    }

    private static Command Workspace()
    {
        var workspace = new Command("workspace", "Workspaces you belong to.");

        workspace.Subcommands.Add(new ApiCommand("list", "List your workspaces.", "listWorkspaces").Handle(async (c, ct) =>
        {
            var list = await c.Api.Api.V1.Workspaces.GetAsync(cancellationToken: ct) ?? [];
            var current = c.Session.Config.EffectiveWorkspace;
            await c.Out.ListAsync(list, ["", "SLUG", "ROLE", "NAME"],
                w => [w.Slug == current ? "*" : "", w.Slug, Cli.Dashed(w.Role.ToString()!), w.Name]);
        }));

        workspace.Subcommands.Add(new ApiCommand("get", "Show the current workspace.", "getWorkspace").Handle(async (c, ct) =>
        {
            var w = await (await c.InWorkspaceAsync(ct)).GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(w!, ("Name", w!.Name), ("Slug", w.Slug), ("Your role", Cli.Dashed(w.Role.ToString()!)),
                ("Seats", w.Seats?.ToString()), ("Hours/week", w.HoursPerWeek?.ToString()));
        }));

        var slug = new Argument<string>("slug") { Description = "URL id: lowercase letters, digits, dashes." };
        var name = new Option<string?>("--name") { Description = "Display name (default: the slug)." };
        workspace.Subcommands.Add(new ApiCommand("create", "Create a workspace; you become its owner.", "createWorkspace") { slug, name }
            .Handle(async (c, ct) =>
            {
                var created = await c.Api.Api.V1.Workspaces.PostAsync(
                    new CreateWorkspaceRequest { Slug = c.Get(slug), Name = c.Get(name) ?? c.Get(slug) }, cancellationToken: ct);
                await c.Out.DoneAsync(created!, $"Created {created!.Slug}. Use it with `flowstate workspace use {created.Slug}`.");
            }));

        var hours = new Option<double?>("--hours-per-week") { Description = "Capacity per person per week (delivery plan)." };
        workspace.Subcommands.Add(new ApiCommand("update", "Rename the workspace or change hours per week.", "updateWorkspace") { name, hours }
            .Handle(async (c, ct) =>
            {
                var w = await (await c.InWorkspaceAsync(ct)).PatchAsync(
                    new UpdateWorkspaceRequest { Name = c.Get(name), HoursPerWeek = c.Get(hours) }, cancellationToken: ct);
                await c.Out.DoneAsync(w!, "Workspace updated.");
            }));

        var useSlug = new Argument<string>("slug") { Description = "Workspace slug." };
        workspace.Subcommands.Add(new Command("use", "Make a workspace the default for later commands.") { useSlug }.Handle(async (c, ct) =>
        {
            var w = await c.Api.Api.V1.Workspaces[c.Get(useSlug)].GetAsync(cancellationToken: ct);
            c.Session.Config.Workspace = w!.Slug;
            c.Session.Config.Save();
            await c.Out.MessageAsync($"Now using {w.Name} ({w.Slug}).");
        }));

        return workspace;
    }

    private static Command Member()
    {
        var member = new Command("member", "People and agents in the workspace.");

        member.Subcommands.Add(new ApiCommand("list", "List members, invites and deactivated members.", "listMembers").Handle(async (c, ct) =>
        {
            var members = await (await c.InWorkspaceAsync(ct)).Members.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(members, ["ID", "KIND", "ROLE", "STATUS", "NAME"],
                m => [m.Id?.ToString(), Cli.Dashed(m.Kind.ToString()!), Cli.Dashed(m.Role.ToString()!), Cli.Dashed(m.Status.ToString()!),
                    m.Kind == MemberKind.Agent ? $"{m.Name} (for {m.OwnerName})" : m.Email is { } e && e != m.Name ? $"{m.Name} <{e}>" : m.Name]);
        }));

        var email = new Argument<string>("email") { Description = "Email to invite." };
        var role = Cli.EnumOption<WorkspaceRole>("--role", "Role on joining (default member)");
        member.Subcommands.Add(new ApiCommand("invite", "Invite someone by email.", "inviteMember") { email, role }.Handle(async (c, ct) =>
        {
            var invited = await (await c.InWorkspaceAsync(ct)).Members.PostAsync(
                new InviteMemberRequest { Email = c.Get(email), Role = c.Get(role) ?? WorkspaceRole.Member }, cancellationToken: ct);
            await c.Out.DoneAsync(invited!, $"Invited {invited!.Email}.");
        }));

        var id = new Argument<Guid>("member-id") { Description = "Member id (from `member list`)." };
        var newRole = Cli.EnumOption<WorkspaceRole>("--role", "New role");
        newRole.Required = true;
        member.Subcommands.Add(new ApiCommand("update", "Change a member's role.", "updateMember") { id, newRole }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Members[c.Get(id)].PatchAsync(
                new UpdateMemberRequest { Role = c.Get(newRole) }, cancellationToken: ct);
            await c.Out.MessageAsync("Role updated.");
        }));

        member.Subcommands.Add(new ApiCommand("deactivate", "Remove access and revoke their tokens; history keeps their name.",
            "deactivateMember") { id }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Members[c.Get(id)].Deactivate.PostAsync(cancellationToken: ct);
            await c.Out.MessageAsync("Member deactivated.");
        }));

        return member;
    }

    private static Command Invite()
    {
        var invite = new Command("invite", "Workspace invites sent to you.");

        invite.Subcommands.Add(new ApiCommand("list", "List your pending invites.", "listInvites").Handle(async (c, ct) =>
        {
            var invites = await c.Api.Api.V1.Invites.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(invites, ["ID", "WORKSPACE", "ROLE", "NAME"],
                i => [i.MemberId?.ToString(), i.WorkspaceSlug, Cli.Dashed(i.Role.ToString()!), i.WorkspaceName]);
        }));

        var id = new Argument<Guid>("id") { Description = "Invite id (from `invite list`)." };
        invite.Subcommands.Add(new ApiCommand("accept", "Accept an invite and join the workspace.", "acceptInvite") { id }.Handle(async (c, ct) =>
        {
            var joined = await c.Api.Api.V1.Invites[c.Get(id)].Accept.PostAsync(cancellationToken: ct);
            await c.Out.DoneAsync(joined!, $"Joined {joined!.Name} ({joined.Slug}).");
        }));

        return invite;
    }
}
