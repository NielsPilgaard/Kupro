using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kupro.Cli.Install;

/// <summary>One line of <c>kupro install mcp</c>'s report.</summary>
public sealed record InstallResult(
    [property: JsonPropertyName("client")] string Client,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("note")] string? Note = null);

/// <param name="Client">Limit to this client; null means every client found on the machine.</param>
/// <param name="CommandPath">Absolute path of the kupro binary, for the stdio entry.</param>
/// <param name="RemoteUrl">Write this MCP URL instead of the stdio bridge.</param>
public sealed record InstallOptions(string? Client, string CommandPath, string? RemoteUrl, bool Skill, bool DryRun);

/// <summary>Registers Kupro in agent clients and installs the skill (ADR-018). Safe to run again.</summary>
public sealed class McpInstaller(InstallHost host)
{
    private const string SkillLabel = "Kupro skill";
    private const string SkillFileName = "SKILL.md";

    public static string SkillContent()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kupro.Cli.Skill.SKILL.md")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<IReadOnlyList<InstallResult>> InstallAsync(InstallOptions options)
    {
        var clients = Select(options.Client).ToList();
        var results = new List<InstallResult>();
        foreach (var client in clients)
        {
            results.Add(await InstallClientAsync(client, options));
        }

        if (options.Skill)
        {
            // Not where registering failed or doesn't apply: the skill would describe tools the client doesn't have.
            var dirs = clients.Zip(results)
                .Where(pair => pair.Second.Action is not (Actions.Skipped or Actions.Failed))
                .Select(pair => pair.First.SkillsDir?.Invoke(host))
                .OfType<string>().Distinct().ToList();
            results.AddRange(dirs.Select(dir => WriteSkill(dir, options.DryRun)));
        }

        return results;
    }

    /// <summary>Writes the skill for every client found, or with <paramref name="refresh"/> only over an existing copy.</summary>
    public IReadOnlyList<InstallResult> InstallSkill(bool refresh)
    {
        var dirs = AgentClients.All
            .Where(c => refresh || AgentClients.IsDetected(c, host))
            .Select(c => c.SkillsDir?.Invoke(host)).OfType<string>().Distinct()
            .Where(dir => !refresh || File.Exists(SkillPath(dir)));
        return [.. dirs.Select(dir => WriteSkill(dir, dryRun: false))];
    }

    public async Task<IReadOnlyList<InstallResult>> UninstallAsync(string? clientName, bool dryRun)
    {
        var results = new List<InstallResult>();
        var selected = clientName is null ? AgentClients.All : AgentClients.All.Where(c => c.Name == clientName).ToList();
        foreach (var client in selected)
        {
            results.Add(await UninstallClientAsync(client, dryRun));
        }

        results.RemoveAll(r => r.Action == Actions.Unchanged);
        var removedHere = selected.Select(c => c.Name).ToHashSet();
        foreach (var dir in selected.Select(c => c.SkillsDir?.Invoke(host)).OfType<string>().Distinct())
        {
            // A shared folder keeps the skill while another client still has Kupro.
            var stillUsed = AgentClients.All.Any(c => !removedHere.Contains(c.Name)
                && c.SkillsDir?.Invoke(host) == dir && ReadEntry(c) is not null);
            if (!stillUsed && File.Exists(SkillPath(dir)))
            {
                results.Add(new(SkillLabel, dryRun ? Actions.WouldRemove : Actions.Removed, SkillPath(dir)));
                if (!dryRun)
                {
                    File.Delete(SkillPath(dir));
                    DeleteEmptyFolders(SkillPath(dir));
                }
            }
        }

        return results;
    }

    private IEnumerable<AgentClient> Select(string? name) => name is null
        ? AgentClients.All.Where(c => AgentClients.IsDetected(c, host))
        : AgentClients.All.Where(c => c.Name == name);

    private JsonObject? ReadEntry(AgentClient client)
    {
        try
        {
            return ConfigStore.Read(client, client.ConfigPath(host));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<InstallResult> InstallClientAsync(AgentClient client, InstallOptions options)
    {
        var path = client.ConfigPath(host);
        var entry = options.RemoteUrl is { } url ? client.Remote?.Invoke(url) : client.Stdio(options.CommandPath);
        if (entry is null)
        {
            return new(client.Label, Actions.Skipped, null, "Takes remote servers only under Settings > Connectors.");
        }

        try
        {
            var current = ConfigStore.Read(client, path);
            if (ConfigStore.Matches(current, entry))
            {
                return new(client.Label, Actions.Unchanged, path);
            }

            var action = current is null ? Actions.Added : Actions.Updated;
            if (options.DryRun)
            {
                return new(client.Label, current is null ? Actions.WouldAdd : Actions.WouldUpdate, path);
            }

            if (current is not null)
            {
                await RemoveEntryAsync(client, path);
            }

            if (client.AddArgs is { } addArgs && host.FindExecutable(client.Executable!) is { } executable)
            {
                await host.Run(executable, addArgs(entry));
            }

            // The file is the fallback, and the check: a client's CLI can fail, or stop at its own sign-in.
            if (!ConfigStore.Matches(ConfigStore.Read(client, path), entry))
            {
                ConfigStore.Write(client, path, entry);
            }

            return new(client.Label, action, path);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(client.Label, Actions.Failed, path, e.Message);
        }
    }

    private async Task<InstallResult> UninstallClientAsync(AgentClient client, bool dryRun)
    {
        var path = client.ConfigPath(host);
        try
        {
            if (ConfigStore.Read(client, path) is null)
            {
                return new(client.Label, Actions.Unchanged, path);
            }

            if (dryRun)
            {
                return new(client.Label, Actions.WouldRemove, path);
            }

            await RemoveEntryAsync(client, path);
            File.Delete(ConfigStore.BackupPath(path));
            DeleteEmptyFolders(path);
            return new(client.Label, Actions.Removed, path);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(client.Label, Actions.Failed, path, e.Message);
        }
    }

    private async Task RemoveEntryAsync(AgentClient client, string path)
    {
        if (client.RemoveArgs is { } removeArgs && host.FindExecutable(client.Executable!) is { } executable)
        {
            await host.Run(executable, removeArgs);
        }

        if (ConfigStore.Read(client, path) is not null)
        {
            ConfigStore.Remove(client, path);
        }
    }

    private static InstallResult WriteSkill(string dir, bool dryRun)
    {
        var path = SkillPath(dir);
        var content = SkillContent();
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (existing == content)
        {
            return new(SkillLabel, Actions.Unchanged, path);
        }

        if (dryRun)
        {
            return new(SkillLabel, existing is null ? Actions.WouldAdd : Actions.WouldUpdate, path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new(SkillLabel, existing is null ? Actions.Added : Actions.Updated, path);
    }

    private static string SkillPath(string dir) => Path.Combine(dir, AgentClients.ServerName, SkillFileName);

    /// <summary>Walks up from a removed file, deleting folders that are now empty, and stops at the home folder.</summary>
    private void DeleteEmptyFolders(string removedFile)
    {
        var dir = Path.GetDirectoryName(removedFile);
        while (dir is not null
               && dir.StartsWith(host.Home, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), host.Home.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    public static class Actions
    {
        public const string Added = "added";
        public const string Updated = "updated";
        public const string Unchanged = "unchanged";
        public const string Removed = "removed";
        public const string Skipped = "skipped";
        public const string Failed = "failed";
        public const string WouldAdd = "would add";
        public const string WouldUpdate = "would update";
        public const string WouldRemove = "would remove";
    }
}
