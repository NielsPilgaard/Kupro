using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Kupro.Import.AzureDevOps;

/// <summary>
/// HTTP for one Azure DevOps collection: PAT auth, the API version (7.1 on Services, probed on Server), and throttling
/// (task 025 Access). Azure DevOps rate-limits by TSTU and says so in <c>Retry-After</c>, also on successful responses
/// when a caller nears the limit, so every response's header is honoured before the next request.
/// </summary>
internal sealed partial class AzureDevOpsClient(HttpClient http, Uri collection, string token, TimeProvider time)
{
    /// <summary>Tried from newest on a Server that rejects 7.1. 6.0 is Server 2020, 5.0 is Server 2019.</summary>
    private static readonly string[] ServerVersions = ["7.1", "7.0", "6.0", "5.1", "5.0"];

    private const int MaxRetries = 6;

    private readonly AuthenticationHeaderValue _auth =
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));

    private string? _version;
    private DateTimeOffset _notBefore;

    public Uri Collection { get; } = collection;

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (wait, ct) => Task.Delay(wait, time, ct);

    /// <summary>True for <c>dev.azure.com</c> and <c>*.visualstudio.com</c>; anything else is Azure DevOps Server.</summary>
    public bool IsServices { get; } = IsServicesHost(collection);

    public static bool IsServicesHost(Uri url) =>
        url.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The API version every request uses, probed once on Server.</summary>
    public async Task<string> VersionAsync(CancellationToken ct)
    {
        if (_version is not null)
        {
            return _version;
        }

        if (IsServices)
        {
            return _version = "7.1";
        }

        // Server answers a version it doesn't know with 400 and names the newest it supports. Use that when it parses,
        // otherwise walk down the list.
        for (var i = 0; i < ServerVersions.Length; i++)
        {
            var candidate = ServerVersions[i];
            using var response = await SendAsync(HttpMethod.Get, $"_apis/projects?$top=1&api-version={candidate}", null, ct);
            if (response.IsSuccessStatusCode)
            {
                return _version = candidate;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode != HttpStatusCode.BadRequest || !body.Contains("version", StringComparison.OrdinalIgnoreCase))
            {
                throw Failure(response.StatusCode, "_apis/projects");
            }

            var supported = SupportedVersion().Match(body) is { Success: true } match
                ? Array.IndexOf(ServerVersions, match.Groups[1].Value)
                : -1;
            if (supported > i)
            {
                i = supported - 1;
            }
        }

        throw new SourceAccessException("This Azure DevOps Server is older than 2019 (API 5.0), which the import does not support.", HttpStatusCode.BadRequest);
    }

    /// <summary>The comments API is still a preview on every version.</summary>
    public async Task<string> CommentsVersionAsync(CancellationToken ct) =>
        await VersionAsync(ct) is "7.1" ? "7.1-preview.4" : $"{await VersionAsync(ct)}-preview.3";

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, await WithVersionAsync(path, ct), null, ct);
        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct) ?? throw Failure(response.StatusCode, path);
    }

    /// <summary>Like <see cref="GetAsync{T}"/>, but null when the resource doesn't exist or isn't readable.</summary>
    public async Task<T?> TryGetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, await WithVersionAsync(path, ct), null, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            return null;
        }

        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct);
    }

    public async Task<TResult> PostAsync<TBody, TResult>(
        string path, TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<TResult> resultType, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, bodyType);
        using var response = await SendAsync(HttpMethod.Post, await WithVersionAsync(path, ct), json, ct);
        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(resultType, ct) ?? throw Failure(response.StatusCode, path);
    }

    /// <summary>A file by absolute URL. The PAT only goes to the collection's own host.</summary>
    public async Task<Stream> DownloadAsync(Uri url, CancellationToken ct)
    {
        if (!url.Scheme.Equals(Collection.Scheme, StringComparison.OrdinalIgnoreCase)
            || !url.Authority.Equals(Collection.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceAccessException($"Attachment {url.AbsolutePath} is not on {Collection.Authority}; it was not downloaded.");
        }

        var separator = string.IsNullOrEmpty(url.Query) ? '?' : '&';
        var response = await SendAsync(HttpMethod.Get, $"{url}{separator}download=true&api-version={await VersionAsync(ct)}", null, ct);
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

    private async Task<string> WithVersionAsync(string path, CancellationToken ct) =>
        path.Contains("api-version=", StringComparison.Ordinal)
            ? path
            : $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={await VersionAsync(ct)}";

    /// <summary>Sends with auth, waiting out throttling. <paramref name="json"/> is bytes so a retry can resend it.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathOrUrl, byte[]? json, CancellationToken ct)
    {
        var url = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? absolute
            : new Uri($"{Collection.AbsoluteUri.TrimEnd('/')}/{pathOrUrl}");
        for (var attempt = 0; ; attempt++)
        {
            var wait = _notBefore - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Delay(wait, ct);
            }

            using var request = new HttpRequestMessage(method, url);
            if (json is not null)
            {
                request.Content = new ByteArrayContent(json) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            }

            request.Headers.Authorization = _auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var retryAfter = RetryAfter(response);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                response.Dispose();
                _notBefore = time.GetUtcNow() + (retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }

            if (retryAfter is { } delay)
            {
                _notBefore = time.GetUtcNow() + delay;
            }

            return response;
        }
    }

    /// <summary><c>Retry-After</c>, or the reset time when <c>X-RateLimit-Remaining</c> hit zero.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } header)
        {
            return header.Delta ?? (header.Date - time.GetUtcNow());
        }

        if (Header(response, "X-RateLimit-Remaining") is "0"
            && long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset))
        {
            return DateTimeOffset.FromUnixTimeSeconds(reset) - time.GetUtcNow();
        }

        return null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
        // A rejected PAT gets a 203 with the HTML sign-in page instead of a 401.
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation
            || (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "text/html"))
        {
            throw Failure(HttpStatusCode.NonAuthoritativeInformation, path);
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
            HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation =>
                new("Azure DevOps rejected the personal access token. It may have expired or been revoked.", status),
            HttpStatusCode.Forbidden =>
                new("The personal access token lacks a scope: it needs Work Items (Read) and Project and Team (Read).", status),
            HttpStatusCode.NotFound => new($"Azure DevOps has no {resource}. Check the organization or collection URL.", status),
            _ => new($"Azure DevOps answered {(int)status} for {resource}.", status),
        };
    }

    [GeneratedRegex(@"supports is (\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SupportedVersion();

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
