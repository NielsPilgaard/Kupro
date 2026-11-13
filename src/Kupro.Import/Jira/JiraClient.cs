using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace Kupro.Import.Jira;

/// <summary>
/// HTTP for one Jira site (task 024 Access): Basic auth with email and API token on Cloud, a personal access token as
/// Bearer on Data Center and Server. Cloud rate-limits with 429 and <c>Retry-After</c>; Data Center sheds load with
/// 503 (and 429 when rate limiting is on). Both are waited out, and a <c>Retry-After</c> on any response is honoured
/// before the next request.
/// </summary>
internal sealed class JiraClient
{
    private const int MaxRetries = 6;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private DateTimeOffset _notBefore;

    /// <param name="token"><c>email:api-token</c> for Basic auth (Cloud), anything else is a PAT sent as Bearer.</param>
    public JiraClient(HttpClient http, Uri site, string token, TimeProvider time)
    {
        _http = http;
        _time = time;
        Site = site;
        Basic = token.Contains(':', StringComparison.Ordinal);
        Auth = Basic
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(token)))
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public Uri Site { get; }

    /// <summary>True when the token is <c>email:api-token</c>, the only kind Cloud takes.</summary>
    public bool Basic { get; }

    private AuthenticationHeaderValue Auth { get; }

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }
        = (wait, ct) => Task.Delay(wait, TimeProvider.System, ct);

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(Url(path), ct);
        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct) ?? throw Failure(response.StatusCode, path);
    }

    /// <summary>Like <see cref="GetAsync{T}"/>, but null when the resource doesn't exist or isn't readable.</summary>
    public async Task<T?> TryGetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(Url(path), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            return null;
        }

        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct);
    }

    /// <summary>A file by absolute URL. The token only goes to the site's own host.</summary>
    public async Task<Stream> DownloadAsync(Uri url, CancellationToken ct)
    {
        if (!url.Scheme.Equals(Site.Scheme, StringComparison.OrdinalIgnoreCase)
            || !url.Authority.Equals(Site.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceAccessException($"Attachment {url.AbsolutePath} is not on {Site.Authority}; it was not downloaded.");
        }

        var response = await SendAsync(url, ct);
        try
        {
            EnsureSuccess(response, url.AbsolutePath);
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private Uri Url(string path) => new($"{Site.AbsoluteUri.TrimEnd('/')}/{path}");

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var wait = _notBefore - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Delay(wait, ct);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = Auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var retryAfter = response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date - _time.GetUtcNow()) : null;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                response.Dispose();
                _notBefore = _time.GetUtcNow() + (retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }

            if (retryAfter is { } delay)
            {
                _notBefore = _time.GetUtcNow() + delay;
            }

            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
        // A login page instead of JSON means the token was not taken (a proxy or SSO in front of Data Center).
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "text/html")
        {
            throw Failure(HttpStatusCode.Unauthorized, path);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, path);
        }
    }

    private static SourceAccessException Failure(HttpStatusCode status, string path)
    {
        var resource = path.Split('?')[0];
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                new("Jira rejected the token. On Cloud use your Atlassian email and an API token; on Data Center a personal access token. It may have expired or been revoked.", status),
            HttpStatusCode.Forbidden => new($"The token's user may not read {resource} in Jira.", status),
            HttpStatusCode.NotFound => new($"Jira has no {resource}. Check the site URL.", status),
            _ => new($"Jira answered {(int)status} for {resource}.", status),
        };
    }

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
