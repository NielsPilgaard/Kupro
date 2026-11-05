using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Kupro.Cli.Install;

/// <summary>Where the machine keeps things. Tests swap in a temp home and a fake process runner.</summary>
public sealed class InstallHost
{
    public required string Home { get; init; }

    /// <summary><c>%APPDATA%</c>, <c>~/Library/Application Support</c> or <c>~/.config</c>.</summary>
    public required string UserConfig { get; init; }

    public required Func<string, string?> FindExecutable { get; init; }

    /// <summary>Runs a client's CLI and says whether it exited with 0.</summary>
    public required Func<string, string[], Task<bool>> Run { get; init; }

    public static InstallHost Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userConfig = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support")
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        return new InstallHost { Home = home, UserConfig = userConfig, FindExecutable = FindOnPath, Run = RunAsync };
    }

    /// <summary>
    /// Real executables only. npm's <c>.cmd</c> shims and VS Code's <c>code.cmd</c> would need cmd.exe, which mangles a
    /// JSON argument, so on those machines the config file is edited instead (ADR-018 IMP-005).
    /// </summary>
    private static string? FindOnPath(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, file))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<bool> RunAsync(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            // A client's CLI may stop to run its own sign-in. The caller then checks the config for the entry.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await output;
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// One agent client, as its docs described it on 2026-11-04 (ADR-018 IMP-003). <see cref="ServersKey"/> is the JSON key
/// holding the servers, or null for Codex's TOML. <see cref="SkillsDir"/> is null where the client has no skills folder.
/// </summary>
public sealed record AgentClient(
    string Name,
    string Label,
    Func<InstallHost, string> ConfigPath,
    string? ServersKey,
    Func<string, JsonObject> Stdio,
    Func<string, JsonObject>? Remote,
    Func<InstallHost, string>? SkillsDir,
    Func<InstallHost, string[]> Markers,
    string? Executable = null,
    Func<JsonObject, string[]>? AddArgs = null,
    string[]? RemoveArgs = null);

public static class AgentClients
{
    public const string ServerName = "kupro";

    private static JsonObject Command(string path) => new() { ["command"] = path, ["args"] = new JsonArray("mcp") };

    private static JsonObject TypedCommand(string path) =>
        new() { ["type"] = "stdio", ["command"] = path, ["args"] = new JsonArray("mcp") };

    private static JsonObject Url(string url) => new() { ["url"] = url };

    private static JsonObject TypedUrl(string url) => new() { ["type"] = "http", ["url"] = url };

    // Cursor, VS Code and Codex all read ~/.agents/skills, so they share one copy. Claude Code only reads its own folder.
    private static string SharedSkills(InstallHost h) => Path.Combine(h.Home, ".agents", "skills");

    public static readonly IReadOnlyList<AgentClient> All =
    [
        new("claude-code", "Claude Code",
            h => Path.Combine(h.Home, ".claude.json"), "mcpServers", Command, TypedUrl,
            h => Path.Combine(h.Home, ".claude", "skills"),
            h => [Path.Combine(h.Home, ".claude"), Path.Combine(h.Home, ".claude.json")],
            "claude",
            entry => ["mcp", "add-json", ServerName, "--scope", "user", ConfigStore.Compact(entry)],
            ["mcp", "remove", ServerName, "--scope", "user"]),

        // Its config file takes local servers only; remote ones are added under Settings > Connectors.
        new("claude-desktop", "Claude Desktop",
            h => Path.Combine(h.UserConfig, "Claude", "claude_desktop_config.json"), "mcpServers", Command, null,
            null,
            h => [Path.Combine(h.UserConfig, "Claude")]),

        new("cursor", "Cursor",
            h => Path.Combine(h.Home, ".cursor", "mcp.json"), "mcpServers", TypedCommand, Url,
            SharedSkills,
            h => [Path.Combine(h.Home, ".cursor")]),

        new("vscode", "VS Code",
            h => Path.Combine(h.UserConfig, "Code", "User", "mcp.json"), "servers", TypedCommand, TypedUrl,
            SharedSkills,
            h => [Path.Combine(h.UserConfig, "Code")],
            "code",
            entry =>
            {
                var named = new JsonObject { ["name"] = ServerName };
                foreach (var (key, value) in entry)
                {
                    named[key] = value?.DeepClone();
                }

                return ["--add-mcp", ConfigStore.Compact(named)];
            }),

        // `codex mcp remove` isn't documented, so removal edits the file.
        new("codex", "Codex",
            h => Path.Combine(h.Home, ".codex", "config.toml"), null, Command, Url,
            SharedSkills,
            h => [Path.Combine(h.Home, ".codex")],
            "codex",
            entry => entry["url"] is { } url
                ? ["mcp", "add", ServerName, "--url", url.GetValue<string>()]
                : ["mcp", "add", ServerName, "--", entry["command"]!.GetValue<string>(), "mcp"]),
    ];

    public static IEnumerable<string> Names => All.Select(c => c.Name);

    public static bool IsDetected(AgentClient client, InstallHost host) =>
        client.Markers(host).Any(path => File.Exists(path) || Directory.Exists(path))
        || (client.Executable is { } name && host.FindExecutable(name) is not null);
}
