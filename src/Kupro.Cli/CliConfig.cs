using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Cli;

/// <summary>
/// Persisted CLI settings and credentials. Environment variables (<c>KUPRO_URL</c>, <c>KUPRO_TOKEN</c>,
/// <c>KUPRO_WORKSPACE</c>) override the file, so CI and agents need no config file at all.
/// </summary>
public sealed class CliConfig
{
    public const string DefaultUrl = "https://app.kupro.app";

    public string? Url { get; set; }
    public string? Workspace { get; set; }

    /// <summary>Kupro API token (<c>kp_pat_</c> / <c>kp_agent_</c>), set with <c>kupro token set</c>.</summary>
    public string? Token { get; set; }

    /// <summary>Keycloak session from <c>kupro login</c> (device flow).</summary>
    public OidcSession? Session { get; set; }

    [JsonIgnore]
    public string EffectiveUrl => (Environment.GetEnvironmentVariable("KUPRO_URL") ?? Url ?? DefaultUrl).TrimEnd('/');

    [JsonIgnore]
    public string? EffectiveToken => Environment.GetEnvironmentVariable("KUPRO_TOKEN") ?? Token;

    [JsonIgnore]
    public string? EffectiveWorkspace => Environment.GetEnvironmentVariable("KUPRO_WORKSPACE") ?? Workspace;

    public static string FilePath =>
        Environment.GetEnvironmentVariable("KUPRO_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kupro", "config.json");

    /// <summary>Tests use an in-memory config that is never written to disk.</summary>
    [JsonIgnore]
    public bool InMemory { get; init; }

    /// <summary>Where this config lives; tests point it at a temp file.</summary>
    [JsonIgnore]
    public string StorePath { get; init; } = FilePath;

    public static CliConfig Load() => Load(FilePath);

    public static CliConfig Load(string path)
    {
        try
        {
            var loaded = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), CliJson.Default.CliConfig) : null;
            return new CliConfig
            {
                StorePath = path,
                Url = loaded?.Url,
                Workspace = loaded?.Workspace,
                Token = loaded?.Token,
                Session = loaded?.Session,
            };
        }
        catch (JsonException)
        {
            return new CliConfig { StorePath = path };
        }
    }

    public void Save()
    {
        if (InMemory)
        {
            return;
        }

        // Sync on purpose: callers are short commands with no synchronization context.
        using var gate = LockAsync(CancellationToken.None).GetAwaiter().GetResult();
        WriteFile();
    }

    /// <summary>
    /// Holds the file lock on the config across processes, so MCP bridges sharing one session refresh it one at a
    /// time (ADR-018 IMP-001). The OS releases it when a process dies, so a crash leaves no stale lock.
    /// </summary>
    public async Task<FileStream?> LockAsync(CancellationToken ct)
    {
        if (InMemory)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new CliException($"Another kupro process is holding {StorePath}.lock. Try again.");
                }

                await Task.Delay(50, ct);
            }
        }
    }

    /// <summary>Replaces the file in one step, so a bridge starting up never reads half a config. Hold the lock.</summary>
    public void WriteFile()
    {
        if (InMemory)
        {
            return;
        }

        var temp = StorePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Holds credentials: owner read/write only.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(temp, options))
        {
            writer.Write(JsonSerializer.Serialize(this, CliJson.Default.CliConfig));
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, StorePath, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
            {
                // Windows refuses while another process is reading the file.
                Thread.Sleep(50);
            }
        }
    }
}

public sealed class OidcSession
{
    public required string TokenEndpoint { get; set; }
    public required string ClientId { get; set; }
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>OAuth token endpoint responses (RFC 6749 / RFC 8628).</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

public sealed class DeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
}

public sealed class OidcDiscovery
{
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; set; } = "";
    [JsonPropertyName("device_authorization_endpoint")] public string? DeviceAuthorizationEndpoint { get; set; }
}

/// <summary>The one field of <c>GET /api/v1/status</c> the version check needs.</summary>
public sealed class StatusProbe
{
    [JsonPropertyName("minCliVersion")] public string? MinCliVersion { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(StatusProbe))]
[JsonSerializable(typeof(Install.InstallResult[]))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(OidcDiscovery))]
internal sealed partial class CliJson : JsonSerializerContext;
