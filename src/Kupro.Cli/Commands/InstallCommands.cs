using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Kupro.Cli.Install;

namespace Kupro.Cli.Commands;

/// <summary>install mcp, install skill, uninstall mcp, update (ADR-018). No API operations, so no parity ids.</summary>
internal static class InstallCommands
{
    public static IEnumerable<Command> Build()
    {
        yield return InstallCommand();
        yield return UninstallCommand();
        yield return Update();
    }

    private static Option<string?> ClientOption() => new Option<string?>("--client")
    {
        Description = $"Only this client ({string.Join(", ", AgentClients.Names)}).",
    }.AcceptOnlyFromAmong([.. AgentClients.Names]);

    private static Command InstallCommand()
    {
        var install = new Command("install", "Connect this CLI to your agent clients.");

        var client = ClientOption();
        var remote = new Option<bool>("--remote") { Description = "Write the server's URL; each client then signs in by itself." };
        var noSkill = new Option<bool>("--no-skill") { Description = "Don't install the Kupro skill." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print what would change and change nothing." };
        var mcp = new Command("mcp", "Sign in if needed, register Kupro in every agent client found, and install the skill.")
        {
            client, remote, noSkill, dryRun,
        };
        install.Subcommands.Add(mcp.Handle(async (c, ct) =>
        {
            var config = c.Session.Config;
            var isRemote = c.Get(remote);
            var isDry = c.Get(dryRun);
            var urlGiven = c.Parse.GetValue(Cli.UrlOption) is not null;
            if (!isRemote && !isDry)
            {
                // A session belongs to one server, so --url signs in again. Login saves the URL with the session.
                if (config.EffectiveToken is null && (config.Session is null || urlGiven))
                {
                    await AccountCommands.LoginAsync(c, ct);
                }
                else if (urlGiven)
                {
                    config.Save();
                }
            }

            var results = await new McpInstaller(InstallHost.Current()).InstallAsync(new InstallOptions(
                c.Get(client),
                isRemote ? "" : InstalledBinary(),
                isRemote ? config.EffectiveUrl + "/mcp" : null,
                !c.Get(noSkill),
                isDry));
            await ReportAsync(c, results);
            if (results.Count == 0)
            {
                await c.Out.MessageAsync("No agent clients found. Name one with --client.");
            }
            else if (!isDry && results.Any(r => r.Action is McpInstaller.Actions.Added or McpInstaller.Actions.Updated))
            {
                await c.Out.MessageAsync("Restart your agent client so it picks up the change.");
            }
        }));

        var refresh = new Option<bool>("--refresh") { Description = "Only update copies that are already installed." };
        install.Subcommands.Add(new Command("skill", "Install the Kupro skill for every agent client found.") { refresh }.Handle(async (c, _) =>
            await ReportAsync(c, new McpInstaller(InstallHost.Current()).InstallSkill(c.Get(refresh)))));
        return install;
    }

    private static Command UninstallCommand()
    {
        var uninstall = new Command("uninstall", "Undo `kupro install`.");
        var client = ClientOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Print what would change and change nothing." };
        var mcp = new Command("mcp", "Remove Kupro and the skill from your agent clients, and nothing else.") { client, dryRun };
        uninstall.Subcommands.Add(mcp.Handle(async (c, _) =>
        {
            var results = await new McpInstaller(InstallHost.Current()).UninstallAsync(c.Get(client), c.Get(dryRun));
            await ReportAsync(c, results);
            if (results.Count == 0)
            {
                await c.Out.MessageAsync("Kupro isn't installed in any agent client.");
            }
        }));
        return uninstall;
    }

    private static Command Update() =>
        new Command("update", "Update this CLI to the latest release, and refresh the skill.").Handle(async (c, ct) =>
        {
            var target = InstalledBinary();
            using var http = c.Session.CreateHttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            var updater = Updater.ForThisMachine(http);
            var latest = await updater.LatestAsync(ct);
            if (latest == CliVersion.Current)
            {
                new McpInstaller(InstallHost.Current()).InstallSkill(refresh: true);
                await c.Out.MessageAsync($"Already on {latest}.");
            }
            else
            {
                await updater.ReplaceAsync(latest, target, ct);
                // The skill is embedded in the binary, so the new binary is the one that has the new skill.
                using var refresh = Process.Start(new ProcessStartInfo(target, ["install", "skill", "--refresh"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
                await (refresh?.StandardOutput.ReadToEndAsync(ct) ?? Task.FromResult(""));
                await (refresh?.WaitForExitAsync(ct) ?? Task.CompletedTask);
                await c.Out.MessageAsync($"Updated {CliVersion.Current} to {latest}.");
            }

            if (c.Out.Json)
            {
                await c.Out.Writer.WriteLineAsync($$"""{"version":"{{latest}}","previous":"{{CliVersion.Current}}"}""", ct);
            }
        });

    /// <summary>This process's file, which clients will launch and <c>update</c> replaces. Not <c>dotnet</c>, under <c>dotnet run</c>.</summary>
    private static string InstalledBinary()
    {
        var path = Environment.ProcessPath;
        return path is not null && Path.GetFileNameWithoutExtension(path) == "kupro"
            ? path
            : throw new CliException("This needs the installed kupro binary, not a run through another program.");
    }

    private static async Task ReportAsync(CommandContext c, IReadOnlyList<InstallResult> results)
    {
        if (c.Out.Json)
        {
            await c.Out.Writer.WriteLineAsync(JsonSerializer.Serialize(results.ToArray(), CliJson.Default.InstallResultArray));
        }
        else
        {
            var labelWidth = results.Select(r => r.Client.Length).DefaultIfEmpty(0).Max();
            var actionWidth = results.Select(r => r.Action.Length).DefaultIfEmpty(0).Max();
            foreach (var r in results)
            {
                await c.Out.Writer.WriteLineAsync(
                    $"{r.Client.PadRight(labelWidth)}  {r.Action.PadRight(actionWidth)}  {r.Path ?? r.Note}".TrimEnd());
            }
        }

        if (results.FirstOrDefault(r => r.Action == McpInstaller.Actions.Failed) is { } failed)
        {
            throw new CliException($"{failed.Client}: {failed.Note}");
        }
    }
}
