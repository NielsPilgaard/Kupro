using System.Diagnostics;
using System.Net.Http.Json;
using Flowstate.Cli.Api;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace Flowstate.Cli;

/// <summary>Builds the generated API client with the CLI's credentials and headers.</summary>
public sealed class ApiSession(CliConfig config, HttpMessageHandler? transport = null)
{
    public CliConfig Config { get; } = config;

    public FlowstateClient CreateClient(bool authenticated = true)
    {
        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        handlers.Add(new CliHeadersHandler());
        var http = transport is null
            ? KiotaClientFactory.Create(handlers)
            : new HttpClient(KiotaClientFactory.ChainHandlersCollectionAndGetFirstLink(transport, [.. handlers])!);
        IAuthenticationProvider auth = authenticated
            ? new BaseBearerTokenAuthenticationProvider(new TokenProvider(this))
            : new AnonymousAuthenticationProvider();
        return new FlowstateClient(new HttpClientRequestAdapter(auth, httpClient: http) { BaseUrl = Config.EffectiveUrl });
    }

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

        using var http = new HttpClient();
        using var response = await http.PostAsync(session.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken,
            ["client_id"] = session.ClientId,
        }), ct);
        var refreshed = await response.Content.ReadFromJsonAsync(CliJson.Default.TokenResponse, ct);
        if (!response.IsSuccessStatusCode || refreshed?.AccessToken is null)
        {
            throw new CliException("Your session expired. Run `flowstate login` again.");
        }

        session.AccessToken = refreshed.AccessToken;
        session.RefreshToken = refreshed.RefreshToken ?? session.RefreshToken;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
        Config.Save();
        return session.AccessToken;
    }

    /// <summary>OAuth 2.0 device authorization grant against the authority the API advertises (ADR-005).</summary>
    public async Task LoginAsync(string authority, string clientId, TextWriter output, CancellationToken ct)
    {
        using var http = new HttpClient();
        var discovery = await http.GetFromJsonAsync(
            authority.TrimEnd('/') + "/.well-known/openid-configuration", CliJson.Default.OidcDiscovery, ct)
            ?? throw new CliException("The sign-in server returned no configuration.");
        if (discovery.DeviceAuthorizationEndpoint is null)
        {
            throw new CliException("The sign-in server doesn't support device login. Use `flowstate token set`.");
        }

        using var start = await http.PostAsync(discovery.DeviceAuthorizationEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = "openid profile email" }), ct);
        var device = await start.Content.ReadFromJsonAsync(CliJson.Default.DeviceAuthorizationResponse, ct);
        if (!start.IsSuccessStatusCode || device is null)
        {
            throw new CliException("Couldn't start device login.");
        }

        var url = device.VerificationUriComplete ?? device.VerificationUri;
        await output.WriteLineAsync($"Open {url} and confirm the code {device.UserCode}.");
        TryOpenBrowser(url);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 1));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            using var poll = await http.PostAsync(discovery.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = device.DeviceCode,
                ["client_id"] = clientId,
            }), ct);
            var token = await poll.Content.ReadFromJsonAsync(CliJson.Default.TokenResponse, ct);
            switch (token?.Error)
            {
                case null when token?.AccessToken is not null:
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
                    throw new CliException($"Login failed: {token?.ErrorDescription ?? token?.Error ?? "unknown error"}.");
            }
        }

        throw new CliException("Login timed out. Run `flowstate login` again.");
    }

    private static void TryOpenBrowser(string url)
    {
        if (Environment.GetEnvironmentVariable("FLOWSTATE_NO_BROWSER") is not null)
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
            ?? throw new CliException("Not signed in. Run `flowstate login` or `flowstate token set <token>`.");
    }

    /// <summary>Marks requests as CLI traffic (activity "via CLI") and makes mutations retry-safe.</summary>
    private sealed class CliHeadersHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Flowstate-Client", Environment.GetEnvironmentVariable("FLOWSTATE_CLIENT") ?? "cli");
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
