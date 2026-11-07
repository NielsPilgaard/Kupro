using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kupro.Import.Linear;

/// <summary>How a Linear credential is sent.</summary>
public enum LinearAuth
{
    /// <summary>A personal API key, sent bare: <c>Authorization: lin_api_…</c>.</summary>
    ApiKey,

    /// <summary>An OAuth access token: <c>Authorization: Bearer …</c>.</summary>
    OAuth,
}

/// <summary>
/// HTTP for Linear's GraphQL API: the credential in the <c>Authorization</c> header, only to <c>api.linear.app</c> and
/// to the upload host for files, and throttling. Linear limits each user by requests and by query complexity per hour
/// and says how much is left on every response (<c>X-RateLimit-*</c>, resets in epoch milliseconds); past a limit it
/// answers a <c>RATELIMITED</c> error, so the client waits before it runs out and waits out the reset when it does.
/// </summary>
internal sealed class LinearClient(HttpClient http, string credential, LinearAuth auth, TimeProvider time)
{
    public static readonly Uri Api = new("https://api.linear.app/graphql");

    /// <summary>Files pasted into descriptions and comments; Linear serves them only to an authenticated request.</summary>
    public const string UploadHost = "uploads.linear.app";

    private const int MaxRetries = 6;

    private readonly string _header = auth == LinearAuth.OAuth ? "Bearer " + credential : credential;

    private DateTimeOffset _notBefore;

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (wait, ct) => Task.Delay(wait, time, ct);

    /// <summary>
    /// Runs one query. <paramref name="operation"/> is its name, for error messages; <paramref name="variables"/> is
    /// sent as given. Any GraphQL error fails the call: a partial page would silently drop items.
    /// </summary>
    public async Task<LinearData> QueryAsync(string operation, string query, JsonObject? variables, CancellationToken ct)
    {
        var body = Body(operation, query, variables);
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(Api, ct, body);
            LinearResponse? parsed = null;
            if (response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.OrdinalIgnoreCase) is true)
            {
                try
                {
                    parsed = await response.Content.ReadFromJsonAsync(LinearJson.Default.LinearResponse, ct);
                }
                catch (JsonException)
                {
                    // Not Linear's JSON (a proxy's error page); judged by the status below.
                }
            }

            var errors = parsed?.Errors ?? [];
            var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests || errors.Any(e => Code(e) == "RATELIMITED");
            var unavailable = response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
            if ((rateLimited || unavailable) && attempt < MaxRetries)
            {
                _notBefore = time.GetUtcNow() + (RetryAfter(response) ?? (rateLimited ? Reset(response) : null) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }

            if (errors.Count > 0 || !response.IsSuccessStatusCode || parsed?.Data is null)
            {
                throw Failure(operation, response.StatusCode, errors, rateLimited);
            }

            return parsed.Data;
        }
    }

    /// <summary>
    /// A file by absolute URL. Linear's uploads are fetched with the credential, as Linear requires; a file anywhere
    /// else (an attachment on another service) without it. Plain http is refused. Redirects drop the header (.NET does).
    /// </summary>
    public async Task<Stream> DownloadAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new SourceAccessException($"Attachment {url.AbsolutePath} is not an https address; it was not downloaded.");
        }

        var response = await SendAsync(url, ct);
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && IsUpload(url)
                    ? new SourceAccessException("Linear refused the file with this credential. It may have been revoked.", response.StatusCode)
                    : new SourceAccessException($"Downloading {url.AbsolutePath} answered {(int)response.StatusCode}.", response.StatusCode);
            }

            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static bool IsUpload(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals(UploadHost, StringComparison.OrdinalIgnoreCase) && url.IsDefaultPort;

    private static bool IsApi(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals(Api.Host, StringComparison.OrdinalIgnoreCase) && url.IsDefaultPort;

    /// <summary>Sends a POST (with a body) or GET, after any wait the last response asked for. Only Linear's hosts get the credential.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct, byte[]? body = null)
    {
        var wait = _notBefore - time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            await Delay(wait, ct);
        }

        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (IsApi(url) || IsUpload(url))
        {
            // A personal key has no scheme, so it can't go through AuthenticationHeaderValue.
            request.Headers.TryAddWithoutValidation("Authorization", _header);
        }

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (IsApi(url))
        {
            Pace(response);
        }

        return response;
    }

    /// <summary>
    /// Stops before a limit runs out: no requests left, or less complexity left than the last query cost (each query
    /// of the connector costs about the same).
    /// </summary>
    private void Pace(HttpResponseMessage response)
    {
        var requestsLeft = Number(response, "X-RateLimit-Requests-Remaining");
        var complexityLeft = Number(response, "X-RateLimit-Complexity-Remaining");
        var cost = Number(response, "X-Complexity") ?? 0;
        TimeSpan? until = null;
        if (requestsLeft <= 0)
        {
            until = Until(response, "X-RateLimit-Requests-Reset");
        }

        if (complexityLeft is { } left && left < Math.Max(cost, 1))
        {
            var complexity = Until(response, "X-RateLimit-Complexity-Reset");
            until = until is null || complexity > until ? complexity : until;
        }

        if (until is { } delay && delay > TimeSpan.Zero)
        {
            _notBefore = time.GetUtcNow() + delay;
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date - time.GetUtcNow()) : null;

    /// <summary>The later of the two resets of the limits that ran out, or of both when the answer doesn't say which.</summary>
    private TimeSpan? Reset(HttpResponseMessage response)
    {
        var requests = Number(response, "X-RateLimit-Requests-Remaining") <= 0 ? Until(response, "X-RateLimit-Requests-Reset") : null;
        var complexity = Number(response, "X-RateLimit-Complexity-Remaining") <= 0 ? Until(response, "X-RateLimit-Complexity-Reset") : null;
        if (requests is null && complexity is null)
        {
            requests = Until(response, "X-RateLimit-Requests-Reset");
            complexity = Until(response, "X-RateLimit-Complexity-Reset");
        }

        var later = requests is null || complexity > requests ? complexity : requests;
        return later > TimeSpan.Zero ? later : null;
    }

    /// <summary>Time until a reset header's epoch-milliseconds moment.</summary>
    private TimeSpan? Until(HttpResponseMessage response, string name) =>
        Number(response, name) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) - time.GetUtcNow() : null;

    private static long? Number(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Ceiling(value)
            : null;

    private static string? Code(LinearError error) => error.Extensions?.Code;

    private SourceAccessException Failure(string operation, HttpStatusCode status, List<LinearError> errors, bool rateLimited)
    {
        var codes = errors.Select(Code).OfType<string>().ToList();
        if (status == HttpStatusCode.Unauthorized || codes.Contains("AUTHENTICATION_ERROR"))
        {
            return new SourceAccessException(
                auth == LinearAuth.OAuth
                    ? "Linear rejected the access token. It may have expired or been revoked: connect Linear again."
                    : "Linear rejected the API key. It may have been revoked: create a new one under Settings, Security & access.",
                HttpStatusCode.Unauthorized);
        }

        if (status == HttpStatusCode.Forbidden || codes.Contains("FORBIDDEN"))
        {
            return new SourceAccessException($"The Linear key may not read what {operation} asks for. It needs read access to the workspace.", HttpStatusCode.Forbidden);
        }

        if (rateLimited)
        {
            return new SourceAccessException("Linear's rate limit outlasted the retries. Run the import again later.", HttpStatusCode.TooManyRequests);
        }

        // Linear's own message says what was wrong ("Entity not found"); kept, minus anything that looks like the key.
        var message = errors.Select(e => e.Extensions?.UserPresentableMessage ?? e.Message).OfType<string>().FirstOrDefault();
        var reason = message is null ? $"answered {(int)status}" : $"answered {(codes.FirstOrDefault() ?? ((int)status).ToString(CultureInfo.InvariantCulture))}: {Scrub(message)}";
        return new SourceAccessException($"Linear {reason} for {operation}.", status == HttpStatusCode.OK ? null : status);
    }

    private string Scrub(string message) =>
        credential.Length == 0 ? message : message.Replace(credential, "***", StringComparison.Ordinal);

    /// <summary>The POST body. Written by hand: variables are a <see cref="JsonObject"/>, which needs no reflection.</summary>
    private static byte[] Body(string operation, string query, JsonObject? variables)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operationName", operation);
            writer.WriteString("query", query);
            writer.WritePropertyName("variables");
            if (variables is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                variables.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
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
