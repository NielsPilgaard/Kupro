using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Kupro.Cli.Api;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace Kupro.Cli;

/// <summary>Builds the generated API client with the CLI's credentials and headers.</summary>
public sealed class ApiSession(CliConfig config, HttpMessageHandler? transport = null, string? cliVersion = null)
{
    private const string SignInUnavailable = "The sign-in server is unavailable. Try again shortly.";

    /// <summary>
    /// Generated clients and the version check multiplex over one connection here, so a command does one TLS handshake.
    /// Kiota's default settings: its own handler deals with redirects. The lifetime picks up DNS changes in a long <c>kupro mcp</c>.
    /// </summary>
    private static readonly SocketsHttpHandler ApiPool = new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };

    /// <summary>For plain clients, which have no redirect handler of their own.</summary>
    private static readonly SocketsHttpHandler PlainPool = new()
    {
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };

    private Task<string?>? _versionCheck;

    public CliConfig Config { get; } = config;

    public KuproClient CreateClient(bool authenticated = true) => new(CreateAdapter(authenticated));

    /// <summary>The adapter behind <see cref="CreateClient"/>, for a request the generated client can't build (a raw upload).</summary>
    public HttpClientRequestAdapter CreateAdapter(bool authenticated = true)
    {
        // Alongside the command's own requests, so it costs no extra round trip.
        _versionCheck ??= CheckVersionAsync();

        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        handlers.Add(new CliHeadersHandler());
        var http = KiotaClientFactory.Create(handlers, transport ?? ApiPool);
        IAuthenticationProvider auth = authenticated
            ? new BaseBearerTokenAuthenticationProvider(new TokenProvider(this))
            : new AnonymousAuthenticationProvider();
        return new HttpClientRequestAdapter(auth, httpClient: http) { BaseUrl = Config.EffectiveUrl };
    }

    /// <summary>Plain HTTP client on the same transport (tests swap it), for non-generated calls like the MCP bridge.</summary>
    public HttpClient CreateHttpClient() => Http2Client(transport ?? PlainPool);

    /// <summary>HTTP/2 when the server offers it, as generated clients do: a pool only shares a connection within one version.</summary>
    private static HttpClient Http2Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { DefaultRequestVersion = HttpVersion.Version20 };

    /// <summary>API token if set, otherwise the login session's access token, refreshed when about to expire.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (Config.EffectiveToken is { } token)
        {
            return token;
        }

        if (Config.Session is not { } session)
        {
            return null;
        }

        if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30) || session.RefreshToken is null)
        {
            return session.AccessToken;
        }

        // Bridges share this session: one refreshes, the others wait here and pick up its result (ADR-018 IMP-001).
        await using var gate = await Config.LockAsync(ct);
        var stored = Config.InMemory ? Config : CliConfig.Load(Config.StorePath);
        if (stored.Session is not { } latest)
        {
            throw new CliException("You signed out. Run `kupro login` again.");
        }

        if (latest.ExpiresAt > session.ExpiresAt)
        {
            session.AccessToken = latest.AccessToken;
            session.RefreshToken = latest.RefreshToken;
            session.ExpiresAt = latest.ExpiresAt;
        }

        if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30) || session.RefreshToken is null)
        {
            return session.AccessToken;
        }

        using var http = CreateHttpClient();
        using var response = await http.PostAsync(session.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken,
            ["client_id"] = session.ClientId,
        }), ct);
        // OAuth reports a dead refresh token with these. Anything else leaves the session valid, so a retry can work.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new CliException("Your session expired. Run `kupro login` again.");
        }

        var refreshed = response.IsSuccessStatusCode ? await ReadJsonOrNullAsync(response.Content, CliJson.Default.TokenResponse, ct) : null;
        if (refreshed?.AccessToken is null)
        {
            throw new CliException(SignInUnavailable);
        }

        session.AccessToken = refreshed.AccessToken;
        session.RefreshToken = refreshed.RefreshToken ?? session.RefreshToken;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
        // Writes what is stored, not this process's config: a one-off --url must not stick because a token expired.
        stored.Session = session;
        stored.WriteFile();
        return session.AccessToken;
    }

    /// <summary>
    /// Prints the warning if this CLI is older than the server's <c>minCliVersion</c>. A slow answer is dropped: the
    /// next command checks again. <paramref name="wait"/> is how long a command may be held up for it.
    /// </summary>
    public async Task FinishVersionCheckAsync(TextWriter error, TimeSpan wait)
    {
        if (_versionCheck is null)
        {
            return;
        }

        try
        {
            if (await _versionCheck.WaitAsync(wait) is { } warning)
            {
                await error.WriteLineAsync(warning);
            }
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task<string?> CheckVersionAsync()
    {
        var current = cliVersion ?? CliVersion.Current;
        if (current == CliVersion.Unstamped)
        {
            return null;
        }

        try
        {
            using var http = Http2Client(transport ?? ApiPool);
            http.Timeout = TimeSpan.FromSeconds(5);
            var status = await http.GetFromJsonAsync(Config.EffectiveUrl + "/api/v1/status", CliJson.Default.StatusProbe);
            return CliVersion.Warning(current, status?.MinCliVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            // The command itself reports an unreachable server.
            return null;
        }
    }

    /// <summary>OAuth 2.0 device authorization grant against the authority the API advertises (ADR-005).</summary>
    public async Task LoginAsync(string authority, string clientId, TextWriter output, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        using var configuration = await http.GetAsync(authority.TrimEnd('/') + "/.well-known/openid-configuration", ct);
        var discovery = (configuration.IsSuccessStatusCode ? await ReadJsonOrNullAsync(configuration.Content, CliJson.Default.OidcDiscovery, ct) : null)
            ?? throw new CliException(SignInUnavailable);
        if (discovery.DeviceAuthorizationEndpoint is null)
        {
            throw new CliException("The sign-in server doesn't support device login. Use `kupro token set`.");
        }

        using var start = await http.PostAsync(discovery.DeviceAuthorizationEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = "openid profile email" }), ct);
        var device = start.IsSuccessStatusCode ? await ReadJsonOrNullAsync(start.Content, CliJson.Default.DeviceAuthorizationResponse, ct) : null;
        if (device is null)
        {
            throw new CliException("Couldn't start device login.");
        }

        var url = device.VerificationUriComplete ?? device.VerificationUri;
        await output.WriteLineAsync($"Open {url} and confirm the code {device.UserCode}.");
        TryOpenBrowser(url);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 1));
        var down = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            var token = await PollTokenAsync(http, discovery.TokenEndpoint, device.DeviceCode, clientId, ct);
            if (token is null)
            {
                // The user may already have confirmed in the browser, and the code stays good until the deadline.
                if (!down)
                {
                    await output.WriteLineAsync("The sign-in server isn't answering. Still trying until the code expires.");
                }

                down = true;
                continue;
            }

            down = false;
            switch (token.Error)
            {
                case null when token.AccessToken is not null:
                    Config.Session = new OidcSession
                    {
                        TokenEndpoint = discovery.TokenEndpoint,
                        ClientId = clientId,
                        AccessToken = token.AccessToken,
                        RefreshToken = token.RefreshToken,
                        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
                    };
                    Config.Save();
                    return;
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                default:
                    throw new CliException($"Login failed: {token.ErrorDescription ?? token.Error ?? "unknown error"}.");
            }
        }

        throw new CliException(down ? SignInUnavailable : "Login timed out. Run `kupro login` again.");
    }

    /// <summary>Null when the sign-in server gave no OAuth answer: unreachable, a 5xx, or a proxy's HTML page.</summary>
    private static async Task<TokenResponse?> PollTokenAsync(
        HttpClient http, string tokenEndpoint, string deviceCode, string clientId, CancellationToken ct)
    {
        try
        {
            using var poll = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = clientId,
            }), ct);
            // Only 5xx counts as down: "authorization_pending" comes as a 400.
            return (int)poll.StatusCode >= 500 ? null : await ReadJsonOrNullAsync(poll.Content, CliJson.Default.TokenResponse, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Null when the body isn't this JSON: a proxy's HTML page can come with any status, even a 200.</summary>
    private static async Task<T?> ReadJsonOrNullAsync<T>(HttpContent content, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        try
        {
            return await content.ReadFromJsonAsync(type, ct);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryOpenBrowser(string url)
    {
        if (Environment.GetEnvironmentVariable("KUPRO_NO_BROWSER") is not null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser (SSH, container): the printed URL is enough.
        }
    }

    private sealed class TokenProvider(ApiSession session) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new();

        public async Task<string> GetAuthorizationTokenAsync(
            Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
            await session.GetAccessTokenAsync(cancellationToken)
            ?? throw new CliException("Not signed in. Run `kupro login` or `kupro token set <token>`.");
    }

    /// <summary>Marks requests as CLI traffic (activity "via CLI") and makes mutations retry-safe.</summary>
    private sealed class CliHeadersHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Kupro-Client", Environment.GetEnvironmentVariable("KUPRO_CLIENT") ?? "cli");
            if (request.Method != HttpMethod.Get && !request.Headers.Contains("Idempotency-Key"))
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>An error with a message for the user; printed without a stack trace.</summary>
public sealed class CliException(string message) : Exception(message);
