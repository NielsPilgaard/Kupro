using System.CommandLine;
using System.Globalization;
using System.Text;
using Kupro.Cli.Api.Models;

namespace Kupro.Cli.Commands;

/// <summary>
/// ai status|providers|connections …|routes … (task 035, ADR-022): the workspace's own AI provider. Changing it needs
/// billing access. Keys come from stdin, <c>KUPRO_AI_KEY</c> or a hidden prompt, never from an argument, so they stay out
/// of shell history.
/// </summary>
internal static class AiCommands
{
    private const string KeyVariable = "KUPRO_AI_KEY";

    public static IEnumerable<Command> Build()
    {
        var ai = new Command("ai", "The workspace's AI: its own key or endpoint for the meeting agent and voice, and what runs where.");
        ai.Subcommands.Add(Status());
        ai.Subcommands.Add(Providers());

        var connections = new Command("connections", "The workspace's own AI connections. Changing them needs billing access.");
        foreach (var command in new[] { ListConnections(), Add(), Check(), SetKey(), Remove() })
        {
            connections.Subcommands.Add(command);
        }

        var routes = new Command("routes", "Which connection serves each capability. Changing them needs billing access.");
        foreach (var command in new[] { ListRoutes(), SetRoute(), RemoveRoute() })
        {
            routes.Subcommands.Add(command);
        }

        ai.Subcommands.Add(connections);
        ai.Subcommands.Add(routes);
        yield return ai;
    }

    private static Command Status() =>
        new ApiCommand("status", "What runs each capability, who pays, connection health and this month's usage.", "getAiStatus").Handle(async (c, ct) =>
        {
            var status = (await (await c.InWorkspaceAsync(ct)).Ai.GetAsync(cancellationToken: ct))!;
            if (c.Out.Json)
            {
                await c.Out.DoneAsync(status, "");
                return;
            }

            await PrintRoutesAsync(c, status.Routes ?? []);
            await c.Out.MessageAsync("");
            await c.Out.ListAsync(status.Connections ?? [], ["ID", "NAME", "PROVIDER", "KEY", "STATUS", "LAST ERROR"], ConnectionRow);
            foreach (var usage in status.Usage ?? [])
            {
                await c.Out.MessageAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{(usage.PaidBy == AiPaidBy.Workspace ? "Your key" : "Kupro's hours")}: {usage.AudioMinutes:0.#} audio minutes, {usage.InputTokens} input and {usage.OutputTokens} output tokens, about €{usage.EstimatedEuro:0.00}."));
            }
        });

    private static Command Providers() =>
        new ApiCommand("providers", "Provider types you can connect, with Kupro's tested models.", "listAiProviders").Handle(async (c, ct) =>
        {
            var list = await (await c.InWorkspaceAsync(ct)).Ai.Providers.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(list, ["ID", "NAME", "ENDPOINT", "TRANSCRIPTION", "CHAT"], p =>
                [p.Id, p.Name, p.CustomEndpoint == true ? "yours" : "fixed", p.TranscriptionModel ?? "(name one)", p.ChatModel ?? "(name one)"]);
        });

    private static Command ListConnections() =>
        new ApiCommand("list", "The workspace's own connections. Keys are never shown, only their last four characters.", "getAiStatus").Handle(async (c, ct) =>
        {
            var status = (await (await c.InWorkspaceAsync(ct)).Ai.GetAsync(cancellationToken: ct))!;
            await c.Out.ListAsync(status.Connections ?? [], ["ID", "NAME", "PROVIDER", "KEY", "STATUS", "LAST ERROR"], ConnectionRow);
        });

    private static Command Add()
    {
        var provider = new Option<string>("--provider") { Description = "scaleway, or openai-compatible for your own endpoint.", DefaultValueFactory = _ => "scaleway" };
        var name = new Option<string?>("--name") { Description = "A name to tell connections apart." };
        var endpoint = new Option<string?>("--endpoint") { Description = "openai-compatible only: the https:// base URL." };
        var project = new Option<string?>("--project") { Description = "scaleway only, optional: the project the key belongs to." };
        var euConfirmed = new Option<bool>("--eu-confirmed") { Description = "openai-compatible only, required: the endpoint is in the EU/EEA and run by you or your processor." };
        var useFor = Cli.EnumListOption<AiCapability>("--use-for", "Route these capabilities here once the key checks out");
        var all = new Option<bool>("--use-for-all") { Description = "Route every capability here once the key checks out." };
        var transcriptionModel = new Option<string?>("--transcription-model") { Description = "With --use-for: the transcription model, if not the tested default." };
        var chatModel = new Option<string?>("--chat-model") { Description = "With --use-for: the model for proposals and voice commands, if not the tested default." };
        return new ApiCommand("add", $"Connect the workspace's own provider. The key comes from stdin, {KeyVariable} or a prompt.", "addAiConnection")
        {
            provider, name, endpoint, project, euConfirmed, useFor, all, transcriptionModel, chatModel,
        }.Handle(async (c, ct) =>
        {
            var key = await KeyAsync(c, ct);
            var capabilities = c.Get(all)
                ? [AiCapability.Transcription, AiCapability.MeetingProposals, AiCapability.VoiceCommands]
                : (c.Get(useFor) ?? []).Select(x => (AiCapability?)x).ToList();
            var created = await (await c.InWorkspaceAsync(ct)).Ai.Connections.PostAsync(new AddAiConnectionRequest
            {
                ProviderType = c.Get(provider),
                Key = key,
                Name = c.Get(name),
                Endpoint = c.Get(endpoint),
                ProjectId = c.Get(project),
                EuConfirmed = c.Get(euConfirmed),
                UseFor = capabilities,
                TranscriptionModel = c.Get(transcriptionModel),
                ChatModel = c.Get(chatModel),
            }, cancellationToken: ct);
            await c.Out.DoneAsync(created!, Checked(created!, capabilities.Count > 0));
        });
    }

    private static Command Check()
    {
        var id = new Argument<Guid>("id") { Description = "Connection id (ai connections list)." };
        return new ApiCommand("check", "Check a connection's key with its vendor again.", "checkAiConnection") { id }.Handle(async (c, ct) =>
        {
            var checkedConnection = await (await c.InWorkspaceAsync(ct)).Ai.Connections[c.Get(id)].Check.PostAsync(cancellationToken: ct);
            await c.Out.DoneAsync(checkedConnection!, Checked(checkedConnection!, routed: false));
        });
    }

    private static Command SetKey()
    {
        var id = new Argument<Guid>("id") { Description = "Connection id (ai connections list)." };
        return new ApiCommand("set-key", $"Replace a connection's key. It comes from stdin, {KeyVariable} or a prompt.", "setAiConnectionKey") { id }.Handle(async (c, ct) =>
        {
            var key = await KeyAsync(c, ct);
            var updated = await (await c.InWorkspaceAsync(ct)).Ai.Connections[c.Get(id)].Key.PutAsync(new SetAiConnectionKeyRequest { Key = key }, cancellationToken: ct);
            await c.Out.DoneAsync(updated!, Checked(updated!, routed: false));
        });
    }

    private static Command Remove()
    {
        var id = new Argument<Guid>("id") { Description = "Connection id (ai connections list)." };
        return new ApiCommand("remove", "Remove a connection. What it served goes back to Kupro's included hours.", "removeAiConnection") { id }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Ai.Connections[c.Get(id)].DeleteAsync(cancellationToken: ct);
            await c.Out.MessageAsync("Removed. What it served is back on Kupro's included hours.");
        });
    }

    private static Command ListRoutes() =>
        new ApiCommand("list", "What runs each capability, and who pays.", "getAiStatus").Handle(async (c, ct) =>
        {
            var status = (await (await c.InWorkspaceAsync(ct)).Ai.GetAsync(cancellationToken: ct))!;
            await PrintRoutesAsync(c, status.Routes ?? []);
        });

    private static Command SetRoute()
    {
        var capability = Cli.EnumOption<AiCapability>("--capability", "What to route");
        capability.Required = true;
        var connection = new Option<Guid>("--connection") { Description = "Connection id (ai connections list).", Required = true };
        var language = new Option<string?>("--language", "-l") { Description = "Only this meeting language, e.g. da. Left out: every language." };
        var model = new Option<string?>("--model") { Description = "Left out: the provider's tested default." };
        return new ApiCommand("set", "Route a capability to a connection.", "setAiRoute") { capability, connection, language, model }.Handle(async (c, ct) =>
        {
            var route = await (await c.InWorkspaceAsync(ct)).Ai.Routes.PutAsync(new SetAiRouteRequest
            {
                Capability = c.Get(capability),
                ConnectionId = c.Get(connection),
                Language = c.Get(language),
                Model = c.Get(model),
            }, cancellationToken: ct);
            await c.Out.DoneAsync(route!, $"{Cli.Dashed(route!.Capability.ToString()!)} now runs on {route.ConnectionName} ({route.Model})."
                + (route.TestedModel == false ? " Kupro hasn't tested that model." : ""));
        });
    }

    private static Command RemoveRoute()
    {
        var capability = Cli.EnumOption<AiCapability>("--capability", "Which route");
        capability.Required = true;
        var language = new Option<string?>("--language", "-l") { Description = "The language route to remove. Left out: the every-language route." };
        return new ApiCommand("remove", "Remove a route. The capability falls back to the every-language route, or to Kupro's.", "removeAiRoute") { capability, language }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Ai.Routes.DeleteAsync(r =>
            {
                r.QueryParameters.Capability = c.Get(capability);
                r.QueryParameters.Language = c.Get(language);
            }, ct);
            await c.Out.MessageAsync("Route removed.");
        });
    }

    private static Task PrintRoutesAsync(CommandContext c, IReadOnlyCollection<AiRouteResponse> routes) =>
        c.Out.ListAsync(routes, ["CAPABILITY", "LANGUAGE", "RUNS ON", "MODEL", "PAID BY", "WORKING"], r =>
        [
            Cli.Dashed(r.Capability.ToString()!), r.Language ?? "all", r.ConnectionName, (r.Model ?? "-") + (r.TestedModel == false ? " (not tested)" : ""),
            r.PaidBy == AiPaidBy.Workspace ? "you, no limit" : "Kupro's hours", r.Available == true ? "yes" : "no",
        ]);

    private static string?[] ConnectionRow(AiConnectionResponse x) =>
        [x.Id?.ToString(), x.Name, x.ProviderType, string.IsNullOrEmpty(x.KeyHint) ? "****" : "…" + x.KeyHint, Cli.Dashed(x.Status.ToString()!), x.LastError];

    private static string Checked(AiConnectionResponse connection, bool routed) => connection.Status switch
    {
        AiConnectionStatus.Healthy => $"{connection.Name} works." + (routed ? " The capabilities you chose now run on it, with no hour limit." : " Route capabilities to it with `ai routes set`."),
        AiConnectionStatus.Rejected => $"{connection.Name} refused the key: {connection.LastError}. Nothing was routed to it; replace it with `ai connections set-key {connection.Id}`.",
        _ => $"{connection.Name} couldn't be checked: {connection.LastError}. Nothing was routed to it; try `ai connections check {connection.Id}`.",
    };

    /// <summary>Stdin, then the environment variable, then a hidden prompt on a terminal. Never an argument.</summary>
    private static async Task<string> KeyAsync(CommandContext c, CancellationToken ct)
    {
        var input = Cli.Runtime.Value?.In ?? (Console.IsInputRedirected ? Console.In : null);
        if (input is not null && (await input.ReadToEndAsync(ct)).Trim() is { Length: > 0 } piped)
        {
            return piped;
        }

        if (Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } fromEnv)
        {
            return fromEnv.Trim();
        }

        if (Cli.Runtime.Value is null && !Console.IsInputRedirected)
        {
            await Console.Error.WriteAsync("Secret key (not shown): ".AsMemory(), ct);
            var key = new StringBuilder();
            for (var k = Console.ReadKey(intercept: true); k.Key != ConsoleKey.Enter; k = Console.ReadKey(intercept: true))
            {
                if (k.Key == ConsoleKey.Backspace && key.Length > 0)
                {
                    key.Length--;
                }
                else if (!char.IsControl(k.KeyChar))
                {
                    key.Append(k.KeyChar);
                }
            }

            await Console.Error.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct);
            if (key.ToString().Trim() is { Length: > 0 } typed)
            {
                return typed;
            }
        }

        throw new CliException($"Pipe the key in, set {KeyVariable}, or run this in a terminal to be asked for it.");
    }
}
