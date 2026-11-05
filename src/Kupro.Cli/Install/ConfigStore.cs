using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kupro.Cli.Install;

/// <summary>
/// Reads and changes the <c>kupro</c> entry in a client's config and nothing else: JSON for most clients, TOML for
/// Codex. Entries are the same JSON shape either way. A file is copied to <c>.kupro.bak</c> before it changes.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string BackupPath(string path) => path + ".kupro.bak";

    /// <summary>The entry has every value we want. Clients may add their own, like <c>"env": {}</c>.</summary>
    public static bool Matches(JsonObject? current, JsonObject desired) =>
        current is not null && desired.All(p => JsonNode.DeepEquals(current[p.Key], p.Value));

    public static JsonObject? Read(AgentClient client, string path) =>
        client.ServersKey is { } key ? ReadRoot(path)?[key]?[AgentClients.ServerName] as JsonObject : ReadToml(path);

    public static void Write(AgentClient client, string path, JsonObject entry)
    {
        if (client.ServersKey is not { } key)
        {
            WriteToml(path, entry);
            return;
        }

        var root = ReadRoot(path) ?? [];
        if (root[key] is not JsonObject servers)
        {
            servers = [];
            root[key] = servers;
        }

        servers[AgentClients.ServerName] = entry.DeepClone();
        Replace(path, Serialize(root, indented: true) + "\n");
    }

    public static void Remove(AgentClient client, string path)
    {
        if (client.ServersKey is not { } key)
        {
            RemoveToml(path);
            return;
        }

        var root = ReadRoot(path);
        if (root?[key] is not JsonObject servers || !servers.Remove(AgentClients.ServerName))
        {
            return;
        }

        if (servers.Count == 0)
        {
            root.Remove(key);
        }

        // Nothing left means the install created the file.
        Replace(path, root.Count == 0 ? null : Serialize(root, indented: true) + "\n");
    }

    public static string Compact(JsonObject entry) => Serialize(entry, indented: false);

    private static JsonObject? ReadRoot(string path)
    {
        if (!File.Exists(path) || File.ReadAllText(path) is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonNode.Parse(text, documentOptions: Lenient) as JsonObject
            ?? throw new JsonException($"{path} doesn't hold a JSON object.");
    }

    // A writer, not ToJsonString: nothing here may need reflection in the AOT build. Relaxed, or æ in a path becomes æ.
    private static string Serialize(JsonNode node, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Backup, then a one-step replace: a client may read the file at any moment. Null deletes the file.</summary>
    private static void Replace(string path, string? content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Copy(path, BackupPath(path), overwrite: true);
        }

        if (content is null)
        {
            File.Delete(path);
            return;
        }

        var temp = path + ".kupro.tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static JsonObject? ReadToml(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        JsonObject? entry = null;
        var inSection = false;
        foreach (var line in File.ReadLines(path).Select(l => l.Trim()))
        {
            if (line.StartsWith('['))
            {
                inSection = IsOurTable(line);
                entry ??= inSection ? [] : null;
            }
            else if (inSection && line.Split('=', 2) is [var name, var value])
            {
                var strings = TomlStrings(value);
                switch (name.Trim())
                {
                    case "command" or "url" when strings.Count > 0:
                        entry![name.Trim()] = strings[0];
                        break;
                    case "args":
                        entry!["args"] = new JsonArray([.. strings.Select(s => (JsonNode)s)]);
                        break;
                }
            }
        }

        return entry;
    }

    private static void WriteToml(string path, JsonObject entry)
    {
        var text = WithoutOurTables(path, out var newline).TrimEnd();
        var lines = new List<string> { $"[mcp_servers.{AgentClients.ServerName}]" };
        if (entry["url"] is { } url)
        {
            lines.Add($"url = {TomlString(url.GetValue<string>())}");
        }
        else
        {
            lines.Add($"command = {TomlString(entry["command"]!.GetValue<string>())}");
            var args = entry["args"]!.AsArray().Select(a => TomlString(a!.GetValue<string>()));
            lines.Add($"args = [{string.Join(", ", args)}]");
        }

        var table = string.Join(newline, lines) + newline;
        Replace(path, text.Length == 0 ? table : text + newline + newline + table);
    }

    private static void RemoveToml(string path)
    {
        if (ReadToml(path) is not null)
        {
            var text = WithoutOurTables(path, out var newline).TrimEnd();
            Replace(path, text.Length == 0 ? null : text + newline);
        }
    }

    private static string WithoutOurTables(string path, out string newline)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var kept = new List<string>();
        var inSection = false;
        foreach (var line in text.Split(newline))
        {
            if (line.TrimStart().StartsWith('['))
            {
                inSection = IsOurTable(line.Trim());
            }

            if (!inSection)
            {
                kept.Add(line);
            }
        }

        return string.Join(newline, kept);
    }

    private static bool IsOurTable(string header) =>
        header is "[mcp_servers.kupro]" or "[mcp_servers.\"kupro\"]"
        || header.StartsWith("[mcp_servers.kupro.", StringComparison.Ordinal)
        || header.StartsWith("[mcp_servers.\"kupro\".", StringComparison.Ordinal);

    private static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>The strings in a TOML value: basic (<c>"a\\b"</c>) and literal (<c>'a\b'</c>), alone or in an array.</summary>
    private static List<string> TomlStrings(string value)
    {
        var found = new List<string>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '#')
            {
                break;
            }

            if (value[i] is not ('"' or '\''))
            {
                continue;
            }

            var quote = value[i];
            var current = new StringBuilder();
            for (i++; i < value.Length && value[i] != quote; i++)
            {
                if (quote == '"' && value[i] == '\\' && i + 1 < value.Length)
                {
                    i++;
                }

                current.Append(value[i]);
            }

            found.Add(current.ToString());
        }

        return found;
    }
}
