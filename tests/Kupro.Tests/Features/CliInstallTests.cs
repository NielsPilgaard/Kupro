using System.CommandLine;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kupro.Api.Mcp;
using Kupro.Cli;
using Kupro.Cli.Install;
using ModelContextProtocol.Server;
using Actions = Kupro.Cli.Install.McpInstaller.Actions;
using CliApp = Kupro.Cli.Cli;

namespace Kupro.Tests.Features;

/// <summary>Task 020 / ADR-018: install scripts' CLI half, against a temp home directory and no real clients.</summary>
public sealed partial class CliInstallTests : IDisposable
{
    private const string Binary = @"C:\Users\Søren\kupro.exe";
    private const string RemoteUrl = "https://app.example.dk/mcp";

    private readonly string _home = Directory.CreateTempSubdirectory("kupro-install-").FullName;
    private readonly List<string[]> _ran = [];
    private readonly HashSet<string> _onPath = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private InstallHost Host => new()
    {
        Home = _home,
        UserConfig = Path.Combine(_home, "appdata"),
        FindExecutable = name => _onPath.Contains(name) ? name : null,
        Run = (_, args) =>
        {
            _ran.Add(args);
            return Task.FromResult(true);
        },
    };

    private McpInstaller Installer => new(Host);

    private static InstallOptions Options(
        string? client = null, string? remote = null, bool skill = true, bool dryRun = false) =>
        new(client, Binary, remote, skill, dryRun);

    private static AgentClient Client(string name) => AgentClients.All.Single(c => c.Name == name);

    private string ConfigOf(string name) => Client(name).ConfigPath(Host);

    private void Detect(string name) => Directory.CreateDirectory(Client(name).Markers(Host)[0]);

    private Dictionary<string, string> Snapshot() => Directory
        .EnumerateFileSystemEntries(_home, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllText(path) : "<folder>");

    public static TheoryData<string> AllClients => [.. AgentClients.Names];

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task Install_registers_the_stdio_bridge_in_each_client(string name)
    {
        var result = (await Installer.InstallAsync(Options(name)))[0];

        result.Action.ShouldBe(Actions.Added);
        var entry = ConfigStore.Read(Client(name), ConfigOf(name)).ShouldNotBeNull();
        entry["command"]!.GetValue<string>().ShouldBe(Binary);
        entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["mcp"]);
    }

    [Theory]
    [InlineData("claude-code", "http")]
    [InlineData("vscode", "http")]
    [InlineData("cursor", null)]
    [InlineData("codex", null)]
    public async Task Remote_writes_the_url_in_each_clients_shape(string name, string? type)
    {
        await Installer.InstallAsync(Options(name, RemoteUrl));

        var entry = ConfigStore.Read(Client(name), ConfigOf(name)).ShouldNotBeNull();
        entry["url"]!.GetValue<string>().ShouldBe(RemoteUrl);
        entry["type"]?.GetValue<string>().ShouldBe(type);
        entry["command"].ShouldBeNull();
    }

    [Fact]
    public async Task Remote_skips_claude_desktop_whose_config_file_takes_local_servers_only()
    {
        var results = await Installer.InstallAsync(Options("claude-desktop", RemoteUrl));

        results.ShouldHaveSingleItem().Action.ShouldBe(Actions.Skipped);
        File.Exists(ConfigOf("claude-desktop")).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task Installing_twice_changes_nothing_the_second_time(string name)
    {
        await Installer.InstallAsync(Options(name));
        var before = Snapshot();

        var second = await Installer.InstallAsync(Options(name));

        second.ShouldAllBe(r => r.Action == Actions.Unchanged);
        Snapshot().ShouldBe(before);
    }

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task Uninstall_on_a_clean_machine_leaves_nothing_behind(string name)
    {
        await Installer.InstallAsync(Options(name));

        await Installer.UninstallAsync(name, dryRun: false);

        Snapshot().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("claude-code", """{"theme":"dark","mcpServers":{"other":{"command":"x"}}}""")]
    [InlineData("claude-desktop", """{"mcpServers":{"other":{"command":"x"}},"globalShortcut":"Ctrl+K"}""")]
    [InlineData("cursor", """{"mcpServers":{"other":{"url":"https://rød.example"}}}""")]
    [InlineData("vscode", """{"inputs":[],"servers":{"other":{"type":"stdio","command":"x"}}}""")]
    [InlineData("codex", "model = \"gpt\"\n\n[mcp_servers.other]\ncommand = \"x\"\n\n[tui]\ntheme = \"dark\"")]
    public async Task Everything_else_in_a_config_survives_and_uninstall_restores_it(string name, string original)
    {
        var path = ConfigOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original, Ct);

        await Installer.InstallAsync(Options(name));

        (await File.ReadAllTextAsync(ConfigStore.BackupPath(path), Ct)).ShouldBe(original);
        var written = await File.ReadAllTextAsync(path, Ct);
        written.ShouldContain("other");
        written.ShouldContain("kupro");

        await Installer.UninstallAsync(name, dryRun: false);

        Normalize(await File.ReadAllTextAsync(path, Ct), name).ShouldBe(Normalize(original, name));
        File.Exists(ConfigStore.BackupPath(path)).ShouldBeFalse();
    }

    private static string Normalize(string text, string client) => client == "codex"
        ? text.Trim()
        : JsonNode.Parse(text)!.ToJsonString();

    [Fact]
    public async Task Codex_table_replaces_the_old_one_and_keeps_following_tables()
    {
        var path = ConfigOf("codex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "[mcp_servers.kupro]\ncommand = 'old'\n\n[mcp_servers.kupro.env]\nA = \"1\"\n\n[tui]\ntheme = \"dark\"\n", Ct);

        (await Installer.InstallAsync(Options("codex")))[0].Action.ShouldBe(Actions.Updated);

        var text = await File.ReadAllTextAsync(path, Ct);
        text.ShouldNotContain("old");
        text.ShouldNotContain("kupro.env");
        text.ShouldContain("[tui]");
        ConfigStore.Read(Client("codex"), path)!["command"]!.GetValue<string>().ShouldBe(Binary);
    }

    [Fact]
    public void Codex_literal_and_basic_strings_both_read_back()
    {
        var path = ConfigOf("codex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[mcp_servers.kupro]\ncommand = 'C:\\a\\kupro.exe' # comment\nargs = [ \"mcp\" ]\n");

        var entry = ConfigStore.Read(Client("codex"), path).ShouldNotBeNull();

        ConfigStore.Matches(entry, Client("codex").Stdio(@"C:\a\kupro.exe")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extra_fields_a_client_adds_to_its_entry_count_as_unchanged()
    {
        var path = ConfigOf("claude-code");
        await File.WriteAllTextAsync(path, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["kupro"] = new JsonObject { ["type"] = "stdio", ["command"] = Binary, ["args"] = new JsonArray("mcp"), ["env"] = new JsonObject() },
            },
        }.ToJsonString(), Ct);

        (await Installer.InstallAsync(Options("claude-code", skill: false)))[0].Action.ShouldBe(Actions.Unchanged);
    }

    [Fact]
    public async Task Without_a_client_only_the_ones_found_are_touched()
    {
        Detect("cursor");

        var results = await Installer.InstallAsync(Options(skill: false));

        results.ShouldHaveSingleItem().Client.ShouldBe("Cursor");
        File.Exists(ConfigOf("codex")).ShouldBeFalse();
    }

    [Fact]
    public async Task Several_clients_get_one_copy_each_of_a_skill_folder()
    {
        foreach (var name in AgentClients.Names)
        {
            Detect(name);
        }

        var results = await Installer.InstallAsync(Options());

        results.Count(r => r.Client != "Kupro skill").ShouldBe(AgentClients.All.Count);
        results.Where(r => r.Client == "Kupro skill").Select(r => r.Path).ShouldBe(
            [Path.Combine(_home, ".claude", "skills", "kupro", "SKILL.md"), Path.Combine(_home, ".agents", "skills", "kupro", "SKILL.md")]);

        await Installer.UninstallAsync(null, dryRun: false);
        File.Exists(results[^1].Path).ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_on_path_is_found_and_its_own_cli_does_the_work()
    {
        _onPath.Add("claude");

        await Installer.InstallAsync(Options(skill: false));

        var args = _ran.ShouldHaveSingleItem();
        args[..5].ShouldBe(["mcp", "add-json", "kupro", "--scope", "user"]);
        JsonNode.Parse(args[5])!["command"]!.GetValue<string>().ShouldBe(Binary);
    }

    [Fact]
    public async Task The_file_is_the_fallback_when_a_clients_cli_writes_nothing()
    {
        _onPath.Add("codex");

        await Installer.InstallAsync(Options("codex", skill: false));

        _ran.ShouldHaveSingleItem().ShouldBe(["mcp", "add", "kupro", "--", Binary, "mcp"]);
        ConfigStore.Read(Client("codex"), ConfigOf("codex")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Vscode_cli_gets_the_name_inside_the_json()
    {
        _onPath.Add("code");

        await Installer.InstallAsync(Options("vscode", skill: false));

        var args = _ran.ShouldHaveSingleItem();
        args[0].ShouldBe("--add-mcp");
        var json = JsonNode.Parse(args[1])!;
        json["name"]!.GetValue<string>().ShouldBe("kupro");
        json["type"]!.GetValue<string>().ShouldBe("stdio");
    }

    [Fact]
    public async Task Dry_run_reports_and_changes_nothing()
    {
        Detect("cursor");
        var before = Snapshot();

        var results = await Installer.InstallAsync(Options(dryRun: true));

        results.Select(r => r.Action).ShouldBe([Actions.WouldAdd, Actions.WouldAdd]);
        Snapshot().ShouldBe(before);
    }

    [Fact]
    public async Task A_config_it_cannot_parse_is_left_alone_and_reported()
    {
        var path = ConfigOf("cursor");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json", Ct);

        var results = await Installer.InstallAsync(Options("cursor"));

        results.ShouldHaveSingleItem().Action.ShouldBe(Actions.Failed);
        (await File.ReadAllTextAsync(path, Ct)).ShouldBe("{ not json");
    }

    [Fact]
    public async Task No_skill_flag_opts_out_and_claude_desktop_has_no_skill_folder()
    {
        (await Installer.InstallAsync(Options("cursor", skill: false))).ShouldHaveSingleItem();
        (await Installer.InstallAsync(Options("claude-desktop"))).ShouldHaveSingleItem();
        File.Exists(Path.Combine(_home, ".agents", "skills", "kupro", "SKILL.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_shared_skill_stays_until_the_last_client_using_it_is_removed()
    {
        var skill = Path.Combine(_home, ".agents", "skills", "kupro", "SKILL.md");
        await Installer.InstallAsync(Options("cursor"));
        await Installer.InstallAsync(Options("codex"));

        await Installer.UninstallAsync("cursor", dryRun: false);
        File.Exists(skill).ShouldBeTrue();

        await Installer.UninstallAsync("codex", dryRun: false);
        File.Exists(skill).ShouldBeFalse();
    }

    [Fact]
    public async Task Refresh_updates_existing_skills_and_adds_none()
    {
        Detect("claude-code");
        var skill = Path.Combine(_home, ".claude", "skills", "kupro", "SKILL.md");

        Installer.InstallSkill(refresh: true).ShouldBeEmpty();
        File.Exists(skill).ShouldBeFalse();

        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        await File.WriteAllTextAsync(skill, "old", Ct);
        Installer.InstallSkill(refresh: true).ShouldHaveSingleItem().Action.ShouldBe(Actions.Updated);
        (await File.ReadAllTextAsync(skill, Ct)).ShouldBe(McpInstaller.SkillContent());
    }

    [Fact]
    public void Skill_frontmatter_names_it_and_says_when_to_use_it()
    {
        var skill = McpInstaller.SkillContent();

        skill.ShouldStartWith("---\nname: kupro\n");
        skill.ShouldContain("description:");
    }

    // ADR-018 IMP-002: the skill can't drift from the tools and commands.

    [Fact]
    public void Skill_names_only_tools_and_commands_that_exist() =>
        UnknownNames(McpInstaller.SkillContent()).ShouldBeEmpty();

    [Theory]
    [InlineData("Call `make_coffee` first.", "make_coffee")]
    [InlineData("Run `kupro issue brew`.", "kupro issue brew")]
    [InlineData("Run `kupro brew`.", "kupro brew")]
    [InlineData("Run `kupro issue create-many --mug`.", "--mug")]
    public void The_skill_check_catches_what_does_not_exist(string skill, string unknown) =>
        UnknownNames(skill).ShouldContain(unknown);

    private static List<string> UnknownNames(string skill)
    {
        var tools = typeof(KuproTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>().ToHashSet();
        var root = CliApp.BuildRoot();

        var unknown = ToolName().Matches(skill).Select(m => m.Groups[1].Value).Where(name => !tools.Contains(name)).ToList();
        foreach (var command in CliCommand().Matches(skill).Select(m => m.Groups[1].Value))
        {
            Command current = root;
            foreach (var word in command.Split(' '))
            {
                if (word.StartsWith("--", StringComparison.Ordinal))
                {
                    if (!current.Options.Concat(root.Options).Any(o => o.Name == word))
                    {
                        unknown.Add(word);
                    }
                }
                else if (!word.StartsWith('<'))
                {
                    if (current.Subcommands.FirstOrDefault(c => c.Name == word) is not { } next)
                    {
                        unknown.Add($"kupro {command}");
                        break;
                    }

                    current = next;
                }
            }
        }

        return unknown;
    }

    [GeneratedRegex("`([a-z]+(?:_[a-z]+)+)`")]
    private static partial Regex ToolName();

    [GeneratedRegex("`kupro ([^`]+)`")]
    private static partial Regex CliCommand();

    // kupro update

    private const string Rid = "linux-x64";
    private const string Latest = "1.2.0";
    private static readonly byte[] NewBinary = "new binary"u8.ToArray();

    private static Dictionary<string, byte[]> Bucket(byte[]? served = null, string? checksum = null) => new()
    {
        ["/cli/latest"] = Encoding.UTF8.GetBytes(Latest + "\n"),
        [$"/cli/{Latest}/SHA256SUMS"] = Encoding.UTF8.GetBytes(
            $"{checksum ?? Convert.ToHexStringLower(SHA256.HashData(NewBinary))}  kupro-{Rid}\nabc  kupro-win-x64.exe\n"),
        [$"/cli/{Latest}/kupro-{Rid}"] = served ?? NewBinary,
    };

    private static Updater UpdaterFor(Dictionary<string, byte[]> bucket) =>
        new(new HttpClient(new BucketHandler(bucket)), "https://bucket.example/", Rid);

    private string OldBinary()
    {
        var target = Path.Combine(_home, "kupro");
        File.WriteAllText(target, "old binary");
        return target;
    }

    [Fact]
    public async Task Update_reads_latest_and_replaces_the_binary()
    {
        var target = OldBinary();
        var updater = UpdaterFor(Bucket());

        (await updater.LatestAsync(Ct)).ShouldBe(Latest);
        await updater.ReplaceAsync(Latest, target, Ct);

        (await File.ReadAllBytesAsync(target, Ct)).ShouldBe(NewBinary);
        Directory.EnumerateFiles(_home).Where(f => f.EndsWith(".new", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Update_keeps_the_old_binary_when_the_checksum_is_wrong()
    {
        var target = OldBinary();

        var error = await Should.ThrowAsync<CliException>(
            () => UpdaterFor(Bucket(served: "tampered"u8.ToArray())).ReplaceAsync(Latest, target, Ct));

        error.Message.ShouldContain("checksum");
        (await File.ReadAllTextAsync(target, Ct)).ShouldBe("old binary");
        Directory.EnumerateFiles(_home).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Update_keeps_the_old_binary_when_the_upload_is_incomplete()
    {
        var target = OldBinary();
        var bucket = Bucket();
        bucket.Remove($"/cli/{Latest}/kupro-{Rid}");

        var error = await Should.ThrowAsync<CliException>(() => UpdaterFor(bucket).ReplaceAsync(Latest, target, Ct));

        error.Message.ShouldContain("404");
        (await File.ReadAllTextAsync(target, Ct)).ShouldBe("old binary");
    }

    [Fact]
    public async Task Update_refuses_a_latest_that_is_not_a_version()
    {
        var bucket = Bucket();
        bucket["/cli/latest"] = "../../evil"u8.ToArray();

        await Should.ThrowAsync<CliException>(() => UpdaterFor(bucket).LatestAsync(Ct));
    }

    private sealed class BucketHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // Token refresh under the file lock (ADR-018 IMP-001)

    private string ConfigFile => Path.Combine(_home, "kupro", "config.json");

    private CliConfig ExpiredConfig()
    {
        var config = new CliConfig
        {
            StorePath = ConfigFile,
            Url = "https://stored.example",
            Session = new OidcSession
            {
                TokenEndpoint = "https://auth.example/token",
                ClientId = "cli",
                AccessToken = "expired",
                RefreshToken = "refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
        };
        config.Save();
        return config;
    }

    [Fact]
    public async Task Bridges_sharing_a_session_refresh_it_once()
    {
        ExpiredConfig();
        var handler = new TokenHandler();

        // Separate sessions and config objects, as separate processes have.
        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            new ApiSession(CliConfig.Load(ConfigFile), handler).GetAccessTokenAsync(Ct)));

        handler.Refreshes.ShouldBe(1);
        tokens.ShouldAllBe(t => t == "fresh-1");
        CliConfig.Load(ConfigFile).Session!.AccessToken.ShouldBe("fresh-1");
    }

    [Fact]
    public async Task Concurrent_requests_in_one_bridge_refresh_once()
    {
        var handler = new TokenHandler();
        var session = new ApiSession(ExpiredConfig(), handler);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.GetAccessTokenAsync(Ct)));

        handler.Refreshes.ShouldBe(1);
        tokens.ShouldAllBe(t => t == "fresh-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.OK)]
    public async Task A_token_endpoint_answering_html_is_a_cli_error(HttpStatusCode status)
    {
        var config = ExpiredConfig();

        var error = await Should.ThrowAsync<CliException>(() => new ApiSession(config, new HtmlHandler(status)).GetAccessTokenAsync(Ct));

        error.Message.ShouldContain("unavailable");
        CliConfig.Load(ConfigFile).Session!.RefreshToken.ShouldBe("refresh");
    }

    [Fact]
    public async Task A_rejected_refresh_token_means_the_session_expired()
    {
        var handler = new SignInHandler((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var error = await Should.ThrowAsync<CliException>(() => new ApiSession(ExpiredConfig(), handler).GetAccessTokenAsync(Ct));

        error.Message.ShouldContain("kupro login");
    }

    // Device login (ADR-005)

    [Theory]
    [InlineData("openid-configuration", HttpStatusCode.OK, "unavailable")]
    [InlineData("openid-configuration", HttpStatusCode.BadGateway, "unavailable")]
    [InlineData("device", HttpStatusCode.OK, "device login")]
    [InlineData("device", HttpStatusCode.BadGateway, "device login")]
    public async Task Html_from_the_sign_in_server_is_a_cli_error(string endpoint, HttpStatusCode status, string message)
    {
        Environment.SetEnvironmentVariable("KUPRO_NO_BROWSER", "1");
        var handler = new SignInHandler((path, _) => path.EndsWith(endpoint, StringComparison.Ordinal)
            ? new HttpResponseMessage(status) { Content = new StringContent("<html>Bad Gateway</html>", Encoding.UTF8, "text/html") }
            : SignInAnswer(path, 0));

        var error = await Should.ThrowAsync<CliException>(() =>
            new ApiSession(new CliConfig { InMemory = true }, handler).LoginAsync("https://auth.example", "cli", TextWriter.Null, Ct));

        error.Message.ShouldContain(message);
    }

    [Fact]
    public async Task Login_keeps_polling_through_a_pending_400()
    {
        Environment.SetEnvironmentVariable("KUPRO_NO_BROWSER", "1");
        var config = new CliConfig { InMemory = true };

        await new ApiSession(config, new SignInHandler(SignInAnswer)).LoginAsync("https://auth.example", "cli", TextWriter.Null, Ct);

        config.Session!.AccessToken.ShouldBe("signed-in");
    }

    [Theory]
    [InlineData("html-502")]
    [InlineData("html-200")]
    [InlineData("json-502")]
    [InlineData("dropped")]
    [InlineData("timeout")]
    public async Task Login_survives_a_failed_poll(string failure)
    {
        Environment.SetEnvironmentVariable("KUPRO_NO_BROWSER", "1");
        var config = new CliConfig { InMemory = true };
        var output = new StringWriter();
        var handler = new SignInHandler((path, polls) => path == "/token" && polls == 1 ? FailedPoll(failure) : SignInAnswer(path, 2));

        await new ApiSession(config, handler).LoginAsync("https://auth.example", "cli", output, Ct);

        config.Session!.AccessToken.ShouldBe("signed-in");
        output.ToString().ShouldContain("isn't answering");
    }

    [Fact]
    public async Task Login_reports_a_sign_in_server_still_down_at_the_deadline()
    {
        Environment.SetEnvironmentVariable("KUPRO_NO_BROWSER", "1");
        var handler = new SignInHandler((path, _) => path == "/token" ? FailedPoll("html-502") : ExpiringSignInAnswer(path));

        var error = await Should.ThrowAsync<CliException>(() =>
            new ApiSession(new CliConfig { InMemory = true }, handler).LoginAsync("https://auth.example", "cli", TextWriter.Null, Ct));

        error.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Login_times_out_when_nobody_confirms()
    {
        Environment.SetEnvironmentVariable("KUPRO_NO_BROWSER", "1");
        var handler = new SignInHandler((path, _) => path == "/token"
            ? Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""")
            : ExpiringSignInAnswer(path));

        var error = await Should.ThrowAsync<CliException>(() =>
            new ApiSession(new CliConfig { InMemory = true }, handler).LoginAsync("https://auth.example", "cli", TextWriter.Null, Ct));

        error.Message.ShouldContain("timed out");
    }

    [Fact]
    public async Task Login_stops_when_the_sign_in_server_refuses()
    {
        Environment.SetEnvironmentVariable("KUPRO_NO_BROWSER", "1");
        var handler = new SignInHandler((path, _) => path == "/token"
            ? Json(HttpStatusCode.BadRequest, """{"error":"access_denied"}""")
            : SignInAnswer(path, 0));

        var error = await Should.ThrowAsync<CliException>(() =>
            new ApiSession(new CliConfig { InMemory = true }, handler).LoginAsync("https://auth.example", "cli", TextWriter.Null, Ct));

        error.Message.ShouldContain("access_denied");
    }

    private static HttpResponseMessage FailedPoll(string failure) => failure switch
    {
        "html-502" => Html(HttpStatusCode.BadGateway),
        "html-200" => Html(HttpStatusCode.OK),
        "json-502" => Json(HttpStatusCode.BadGateway, """{"message":"bad gateway"}"""),
        "dropped" => throw new HttpRequestException("connection dropped"),
        _ => throw new TaskCanceledException(),
    };

    /// <summary>A device code that runs out after one poll.</summary>
    private static HttpResponseMessage ExpiringSignInAnswer(string path) => path == "/device"
        ? Json(HttpStatusCode.OK, """{"device_code":"d","user_code":"u","verification_uri":"https://auth.example/verify","expires_in":1,"interval":1}""")
        : SignInAnswer(path, 0);

    private static HttpResponseMessage Html(HttpStatusCode status) =>
        new(status) { Content = new StringContent("<html>Bad Gateway</html>", Encoding.UTF8, "text/html") };

    private static HttpResponseMessage SignInAnswer(string path, int polls) => path switch
    {
        "/.well-known/openid-configuration" => Json(HttpStatusCode.OK,
            """{"token_endpoint":"https://auth.example/token","device_authorization_endpoint":"https://auth.example/device"}"""),
        "/device" => Json(HttpStatusCode.OK,
            """{"device_code":"d","user_code":"u","verification_uri":"https://auth.example/verify","expires_in":60,"interval":1}"""),
        _ when polls == 1 => Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
        _ => Json(HttpStatusCode.OK, """{"access_token":"signed-in","expires_in":300}"""),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Timeouts and dropped connections

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_bridge_request_gets_an_error_reply(bool timeout)
    {
        Exception failure = timeout ? new TaskCanceledException() : new IOException("connection dropped");
        var (exit, output, error) = await RunCliAsync(failure, """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", "mcp");

        exit.ShouldBe(0, error);
        var reply = JsonNode.Parse(output)!;
        reply["id"]!.GetValue<int>().ShouldBe(7);
        reply["error"]!["message"]!.GetValue<string>().ShouldContain(timeout ? "in time" : "connection dropped");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>", "502")]
    [InlineData(HttpStatusCode.OK, "<html>Sign in</html>", "200")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"title":"Too many requests"}""", "429")]
    [InlineData(HttpStatusCode.BadGateway, "", "502")]
    public async Task A_gateway_answer_on_the_bridge_gets_an_error_reply(HttpStatusCode status, string body, string message)
    {
        var (exit, output, error) = await RunBridgeAsync(status, body, """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");

        exit.ShouldBe(0, error);
        var reply = JsonNode.Parse(output)!;
        reply["id"]!.GetValue<int>().ShouldBe(7);
        reply["error"]!["message"]!.GetValue<string>().ShouldContain(message);
    }

    [Fact]
    public async Task A_gateway_answer_to_a_notification_gets_no_reply()
    {
        var (exit, output, error) = await RunBridgeAsync(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        exit.ShouldBe(0, error);
        output.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_json_rpc_answer_passes_through_the_bridge(HttpStatusCode status)
    {
        const string answer = """{"jsonrpc":"2.0","id":7,"error":{"code":-32601,"message":"No such method"}}""";

        var (exit, output, error) = await RunBridgeAsync(status, answer, """{"jsonrpc":"2.0","id":7,"method":"nope"}""");

        exit.ShouldBe(0, error);
        output.Trim().ShouldBe(answer);
    }

    [Fact]
    public async Task An_empty_accepted_answer_gets_no_reply()
    {
        var (exit, output, error) = await RunBridgeAsync(HttpStatusCode.Accepted, "", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        exit.ShouldBe(0, error);
        output.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_that_times_out_is_a_cli_error()
    {
        var (exit, _, error) = await RunCliAsync(new TaskCanceledException(), "", "status");

        exit.ShouldBe(1);
        error.ShouldContain("in time");
    }

    private static Task<(int Exit, string Out, string Error)> RunCliAsync(Exception failure, string stdin, params string[] args) =>
        RunCliAsync(new FailingHandler(failure), stdin, args);

    private static Task<(int Exit, string Out, string Error)> RunBridgeAsync(HttpStatusCode status, string body, string message) =>
        RunCliAsync(new SignInHandler((_, _) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") }), message, "mcp");

    private static async Task<(int Exit, string Out, string Error)> RunCliAsync(HttpMessageHandler handler, string stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var config = new CliConfig { InMemory = true, Url = "http://localhost", Token = "token" };
        CliApp.Runtime.Value = new CliRuntime(config, stdout, stderr, handler, Json: true, In: new StringReader(stdin));

        var exit = await CliApp.BuildRoot().Parse(args).InvokeAsync(new InvocationConfiguration { Error = stderr, Output = stdout }, Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    /// <summary>Answers by path; the count is how many token polls have come in, this one included.</summary>
    private sealed class SignInHandler(Func<string, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int _polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(answer(path, path == "/token" ? ++_polls : 0));
        }
    }

    [Fact]
    public async Task Refreshing_does_not_save_a_one_off_url()
    {
        var config = ExpiredConfig();
        config.Url = "https://one-off.example";

        await new ApiSession(config, new TokenHandler()).GetAccessTokenAsync(Ct);

        CliConfig.Load(ConfigFile).Url.ShouldBe("https://stored.example");
    }

    [Fact]
    public async Task A_bridge_does_not_bring_back_a_session_after_logout()
    {
        var config = ExpiredConfig();
        var other = CliConfig.Load(ConfigFile);
        other.Session = null;
        other.Save();

        await Should.ThrowAsync<CliException>(() => new ApiSession(config, new TokenHandler()).GetAccessTokenAsync(Ct));

        CliConfig.Load(ConfigFile).Session.ShouldBeNull();
    }

    private sealed class HtmlHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("<html>Bad Gateway</html>", Encoding.UTF8, "text/html"),
            });
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private int _refreshes;

        public int Refreshes => _refreshes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _refreshes);
            // Slow enough that the other bridges are waiting at the lock.
            await Task.Delay(100, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"fresh-{{count}}","expires_in":300}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
