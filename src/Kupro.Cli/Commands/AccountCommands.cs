using System.CommandLine;
using Kupro.Cli.Api.Models;

namespace Kupro.Cli.Commands;

/// <summary>status, login, logout, token, me.</summary>
internal static class AccountCommands
{
    public static IEnumerable<Command> Build()
    {
        yield return new ApiCommand("status", "Check the API and show where you're signed in.", "getStatus").Handle(async (c, ct) =>
        {
            var status = await c.Session.CreateClient(authenticated: false).Api.V1.Status.GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(status!,
                ("API", c.Session.Config.EffectiveUrl),
                ("Version", status!.Version),
                ("Sign-in", status.Authority),
                ("Credential", c.Session.Config.EffectiveToken is { } t ? t[..Math.Min(t.Length, 12)] + "..."
                    : c.Session.Config.Session is not null ? "login session" : "none"),
                ("Workspace", c.Session.Config.EffectiveWorkspace));
        });

        var login = new Command("login", "Sign in through the browser (OAuth device flow).");
        yield return login.Handle(async (c, ct) =>
        {
            var status = await c.Session.CreateClient(authenticated: false).Api.V1.Status.GetAsync(cancellationToken: ct);
            if (status?.Authority is not { } authority)
            {
                throw new CliException("This server has no sign-in configured. Use `kupro token set <token>`.");
            }

            await c.Session.LoginAsync(authority, status.CliClientId ?? "cli", c.Out.Writer, ct);
            var me = await c.Api.Api.V1.Me.GetAsync(cancellationToken: ct);
            await c.Out.Writer.WriteLineAsync($"Signed in to {c.Session.Config.EffectiveUrl} as {me?.DisplayName} ({me?.Email}).");
        });

        yield return new Command("logout", "Forget the stored login session and token.").Handle((c, _) =>
        {
            c.Session.Config.Session = null;
            c.Session.Config.Token = null;
            c.Session.Config.Save();
            return c.Out.MessageAsync("Signed out.");
        });

        yield return Token();
        yield return Me();
    }

    private static Command Token()
    {
        var token = new Command("token", "API tokens for CI and agents.");

        var secret = new Argument<string>("token") { Description = "An kp_pat_ or kp_agent_ token." };
        var set = new Command("set", "Use an API token for this CLI (stored in your config).") { secret };
        token.Subcommands.Add(set.Handle(async (c, ct) =>
        {
            c.Session.Config.Token = c.Get(secret);
            var me = await c.Api.Api.V1.Me.GetAsync(cancellationToken: ct);
            c.Session.Config.Save();
            await c.Out.Writer.WriteLineAsync($"Using token for {me?.DisplayName}{(me?.Agent is { } a ? $" (agent {a})" : "")}.");
        }));

        token.Subcommands.Add(new ApiCommand("list", "List your active API tokens.", "listApiTokens").Handle(async (c, ct) =>
        {
            var tokens = await c.Api.Api.V1.Tokens.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(tokens, ["ID", "HINT", "KIND", "AGENT", "LAST USED", "NAME"], t =>
                [t.Id?.ToString(), t.Hint, Cli.Dashed(t.Kind.ToString()!), t.AgentName, t.LastUsedAt?.ToString("yyyy-MM-dd HH:mm"), t.Name]);
        }));

        var name = new Option<string>("--name") { Description = "Label for the token.", Required = true };
        var agent = new Option<string?>("--agent") { Description = "Create an agent token with this agent name (needs a workspace)." };
        var days = new Option<int?>("--expires-in-days") { Description = "Expiry in days (1-365)." };
        var scoped = new Option<bool>("--workspace-only") { Description = "Limit a personal token to the current workspace." };
        var create = new ApiCommand("create", "Create an API token; prints the secret once.", "createApiToken") { name, agent, days, scoped };
        token.Subcommands.Add(create.Handle(async (c, ct) =>
        {
            var isAgent = c.Get(agent) is not null;
            var created = await c.Api.Api.V1.Tokens.PostAsync(new CreateApiTokenRequest
            {
                Name = c.Get(name),
                Kind = isAgent ? ApiTokenKind.Agent : ApiTokenKind.Personal,
                AgentName = c.Get(agent),
                ExpiresInDays = c.Get(days),
                Workspace = isAgent || c.Get(scoped) ? await c.WorkspaceAsync(ct) : null,
            }, cancellationToken: ct);
            await c.Out.DoneAsync(created!, $"{created!.Secret}\nStore it now; it won't be shown again.");
        }));

        var id = new Argument<Guid>("id") { Description = "Token id (from `token list`)." };
        token.Subcommands.Add(new ApiCommand("revoke", "Revoke an API token immediately.", "revokeApiToken") { id }.Handle(async (c, ct) =>
        {
            await c.Api.Api.V1.Tokens[c.Get(id)].DeleteAsync(cancellationToken: ct);
            await c.Out.MessageAsync("Token revoked.");
        }));

        return token;
    }

    private static Command Me()
    {
        var me = new ApiCommand("me", "Show who you are signed in as.", "getMe");
        me.Handle(async (c, ct) =>
        {
            var profile = await c.Api.Api.V1.Me.GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(profile!, ("Name", profile!.DisplayName), ("Email", profile.Email), ("Agent", profile.Agent),
                ("Language", profile.Locale), ("Time zone", profile.TimeZone));
        });

        var name = new Option<string?>("--name") { Description = "Display name." };
        var locale = new Option<string?>("--language") { Description = "UI language: en or da." };
        var zone = new Option<string?>("--time-zone") { Description = "IANA time zone, e.g. Europe/Copenhagen." };
        me.Subcommands.Add(new ApiCommand("update", "Update your profile.", "updateMe") { name, locale, zone }.Handle(async (c, ct) =>
        {
            var profile = await c.Api.Api.V1.Me.PatchAsync(
                new UpdateMeRequest { DisplayName = c.Get(name), Locale = c.Get(locale), TimeZone = c.Get(zone) }, cancellationToken: ct);
            await c.Out.DoneAsync(profile!, "Profile updated.");
        }));
        return me;
    }
}
