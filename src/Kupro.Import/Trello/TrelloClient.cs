using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Kupro.Import.Trello;

/// <summary>
/// HTTP for the Trello REST API: key and token in the OAuth <c>Authorization</c> header (never in a URL, so they stay
/// out of logs), and throttling. Trello limits each token to 100 requests per 10 seconds and answers 429 past that;
/// every response says how much of the window is left, so the client waits before it runs out.
/// </summary>
internal sealed partial class TrelloClient(HttpClient http, string key, string token, TimeProvider time)
{
    public static readonly Uri Api = new("https://api.trello.com/1/");

    private const int MaxRetries = 6;
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    private readonly AuthenticationHeaderValue _auth = new("OAuth", $"oauth_consumer_key=\"{key}\", oauth_token=\"{token}\"");

    private DateTimeOffset _notBefore;

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (wait, ct) => Task.Delay(wait, time, ct);

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(Api, path), authorize: true, ct);
        await EnsureSuccessAsync(response, path, ct);
        return await response.Content.ReadFromJsonAsync(type, ct) ?? throw new SourceAccessException($"Trello answered an empty body for {Resource(path)}.", response.StatusCode);
    }

    /// <summary>
    /// A file by absolute URL. A Trello upload (<c>trello.com/1/cards/…/attachments/…/download/…</c>) is fetched from
    /// <c>api.trello.com</c> with the OAuth header, as Trello requires; a file anywhere else is fetched without
    /// credentials. The key and token only ever go to <see cref="Api"/>'s host. Redirects drop the header (.NET does).
    /// </summary>
    public async Task<Stream> DownloadAsync(Uri url, CancellationToken ct)
    {
        var upload = (url.Host.Equals("trello.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals(Api.Host, StringComparison.OrdinalIgnoreCase))
            && UploadPath().IsMatch(url.AbsolutePath);
        if (!upload && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new SourceAccessException($"Attachment {url.AbsolutePath} is not an https address; it was not downloaded.");
        }

        var target = upload ? new Uri($"{Api.Scheme}://{Api.Authority}{url.AbsolutePath}") : url;
        var response = await SendAsync(target, authorize: upload, ct);
        try
        {
            await EnsureSuccessAsync(response, url.AbsolutePath, ct);
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Sends a GET, waiting out the rate limit. Only requests to the API host carry the key and token.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, bool authorize, CancellationToken ct)
    {
        authorize &= url.Host.Equals(Api.Host, StringComparison.OrdinalIgnoreCase) && url.Scheme == Uri.UriSchemeHttps;
        for (var attempt = 0; ; attempt++)
        {
            var wait = _notBefore - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Delay(wait, ct);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (authorize)
            {
                request.Headers.Authorization = _auth;
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                var retry = RetryAfter(response) ?? Window(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                response.Dispose();
                _notBefore = time.GetUtcNow() + retry;
                continue;
            }

            if (authorize && (Header(response, "x-rate-limit-api-token-remaining") is "0" || Header(response, "x-rate-limit-api-key-remaining") is "0"))
            {
                _notBefore = time.GetUtcNow() + (Window(response) ?? DefaultWindow);
            }

            return response;
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date - time.GetUtcNow()) : null;

    /// <summary>The rate-limit window from <c>x-rate-limit-api-token-interval-ms</c>: waiting it out resets the count.</summary>
    private static TimeSpan? Window(HttpResponseMessage response) =>
        int.TryParse(Header(response, "x-rate-limit-api-token-interval-ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Trello explains a refusal in a short plain-text body: "invalid token", "invalid key", "invalid id".
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        var resource = Resource(path);
        var status = response.StatusCode;
        throw status switch
        {
            HttpStatusCode.Unauthorized when body.Contains("invalid key", StringComparison.OrdinalIgnoreCase) =>
                new SourceAccessException("Trello rejected Kupro's API key. This is a Kupro configuration problem, not your token."),
            HttpStatusCode.Unauthorized when body.Contains("token", StringComparison.OrdinalIgnoreCase) =>
                new SourceAccessException("Trello rejected the token. It may have expired or been revoked: authorize Kupro in Trello again.", status),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new SourceAccessException($"The Trello token has no access to {resource}. Authorize Kupro with an account that is a member.", status),
            HttpStatusCode.NotFound => new SourceAccessException($"Trello has no {resource}.", status),
            HttpStatusCode.BadRequest when body.Contains("invalid id", StringComparison.OrdinalIgnoreCase) =>
                new SourceAccessException($"Trello has no {resource}.", HttpStatusCode.NotFound),
            _ => new SourceAccessException($"Trello answered {(int)status} for {resource}.", status),
        };
    }

    private static string Resource(string path) => path.Split('?')[0];

    [GeneratedRegex("^/1/cards/[0-9a-f]{24}/attachments/[0-9a-f]{24}/download/[^/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UploadPath();

    /// <summary>An attachment's body that disposes its response, and so frees the connection, when read to the end.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
