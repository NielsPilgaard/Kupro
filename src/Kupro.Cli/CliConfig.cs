using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kupro.Cli;

/// <summary>
/// Persisted CLI settings and credentials. Environment variables (<c>KUPRO_URL</c>, <c>KUPRO_TOKEN</c>,
/// <c>KUPRO_WORKSPACE</c>) override the file, so CI and agents need no config file at all.
/// </summary>
public sealed class CliConfig
{
    public const string DefaultUrl = "http://localhost:5239";

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

    public static CliConfig Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), CliJson.Default.CliConfig) ?? new CliConfig()
                : new CliConfig();
        }
        catch (JsonException)
        {
            return new CliConfig();
        }
    }

    public void Save()
    {
        if (InMemory)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, CliJson.Default.CliConfig));
        if (!OperatingSystem.IsWindows())
        {
            // Holds credentials: owner read/write only.
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(OidcDiscovery))]
internal sealed partial class CliJson : JsonSerializerContext;
