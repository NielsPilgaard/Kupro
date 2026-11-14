using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;

namespace Kupro.Import.YouTrack;

/// <summary>
/// HTTP for one YouTrack (task 027 Access): Cloud (<c>*.youtrack.cloud</c>) and self-hosted share the REST API under
/// <c>/api</c>, with a permanent token as Bearer. Lists page with <c>$skip</c> and <c>$top</c>. 429 and 503 are waited
/// out, honouring <c>Retry-After</c>.
/// </summary>
internal sealed class YouTrackClient
{
    public const int PageSize = 100;
    private const int MaxRetries = 6;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly AuthenticationHeaderValue _auth;
    private DateTimeOffset _notBefore;

    public YouTrackClient(HttpClient http, Uri site, string token, TimeProvider time)
    {
        _http = http;
        _time = time;
        Site = site;
        Api = new Uri($"{site.AbsoluteUri.TrimEnd('/')}/api/");
        _auth = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>The address people open, without <c>/api</c>.</summary>
    public Uri Site { get; }

    public Uri Api { get; }

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }
        = (wait, ct) => Task.Delay(wait, TimeProvider.System, ct);

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(Api, path), ct);
        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct) ?? throw Failure(response.StatusCode, path);
    }

    /// <summary>Every item of a list, <see cref="PageSize"/> at a time. With <paramref name="missingIsEmpty"/>, a list the token may not read is empty.</summary>
    public async IAsyncEnumerable<T> PagedAsync<T>(string path, JsonTypeInfo<List<T>> type, [EnumeratorCancellation] CancellationToken ct, bool missingIsEmpty = false)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        for (var skip = 0; ; skip += PageSize)
        {
            var page = $"{path}{separator}$skip={skip}&$top={PageSize}";
            List<T> items;
            using (var response = await SendAsync(new Uri(Api, page), ct))
            {
                if (missingIsEmpty && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    yield break;
                }

                EnsureSuccess(response, path);
                items = await response.Content.ReadFromJsonAsync(type, ct) ?? [];
            }

            foreach (var item in items)
            {
                yield return item;
            }

            if (items.Count < PageSize)
            {
                yield break;
            }
        }
    }

    /// <summary>A file by its URL (YouTrack gives a signed path under the site). The token only goes to the site's own host.</summary>
    public async Task<Stream> DownloadAsync(string url, CancellationToken ct)
    {
        var absolute = new Uri(Site, url);
        if (absolute.Scheme != Site.Scheme || !absolute.Authority.Equals(Site.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceAccessException($"Attachment {absolute.AbsolutePath} is not on {Site.Authority}; it was not downloaded.");
        }

        var response = await SendAsync(absolute, ct);
        try
        {
            EnsureSuccess(response, absolute.AbsolutePath);
            var content = new MemoryStream();
            await response.Content.CopyToAsync(content, ct);
            content.Position = 0;
            return content;
        }
        finally
        {
            response.Dispose();
        }
    }

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
            request.Headers.Authorization = _auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                var retry = response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date - _time.GetUtcNow()) : null;
                response.Dispose();
                _notBefore = _time.GetUtcNow() + (retry ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }

            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
        // A login page instead of JSON means the token was not taken (a proxy or SSO in front of the server).
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
                new("YouTrack rejected the token. Use a permanent token (Profile → Account Security); it may have expired or been revoked.", status),
            HttpStatusCode.Forbidden => new($"The token's user may not read {resource} in YouTrack.", status),
            HttpStatusCode.NotFound => new($"YouTrack has no {resource}. Check the address.", status),
            _ => new($"YouTrack answered {(int)status} for {resource}.", status),
        };
    }
}
