using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;

namespace Kupro.Import.GitHub;

/// <summary>
/// HTTP for GitHub's REST API (task 027 Access): github.com, or GitHub Enterprise Server under <c>/api/v3</c>. Pages
/// follow the <c>Link</c> header. The primary rate limit (403 or 429 with <c>x-ratelimit-remaining: 0</c>) waits until
/// <c>x-ratelimit-reset</c>; a secondary limit waits its <c>Retry-After</c>.
/// </summary>
internal sealed class GitHubClient
{
    private const int MaxRetries = 6;
    private const string ApiVersion = "2022-11-28";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly AuthenticationHeaderValue _auth;
    private DateTimeOffset _notBefore;

    public GitHubClient(HttpClient http, Uri api, string token, TimeProvider time)
    {
        _http = http;
        _time = time;
        Api = api;
        _auth = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>The API root, ending in a slash: <c>https://api.github.com/</c> or <c>https://ghe.example/api/v3/</c>.</summary>
    public Uri Api { get; }

    /// <summary>Waits between requests; replaced in tests so rate limits don't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }
        = (wait, ct) => Task.Delay(wait, TimeProvider.System, ct);

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(Api, path), ct);
        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct) ?? throw Failure(response.StatusCode, path);
    }

    /// <summary>Every page of a list, following <c>Link: rel="next"</c>. Null items are skipped.</summary>
    public async IAsyncEnumerable<T> PagedAsync<T>(string path, JsonTypeInfo<List<T>> type, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in PagedAsync(path, type, missingIsEmpty: false, ct))
        {
            yield return item;
        }
    }

    /// <summary>Like <see cref="PagedAsync{T}(string, JsonTypeInfo{List{T}}, CancellationToken)"/>, but nothing when the list doesn't exist or isn't readable.</summary>
    public IAsyncEnumerable<T> TryPagedAsync<T>(string path, JsonTypeInfo<List<T>> type, CancellationToken ct) =>
        PagedAsync(path, type, missingIsEmpty: true, ct);

    private async IAsyncEnumerable<T> PagedAsync<T>(string path, JsonTypeInfo<List<T>> type, bool missingIsEmpty, [EnumeratorCancellation] CancellationToken ct)
    {
        Uri? url = new(Api, path);
        while (url is not null)
        {
            List<T> page;
            using (var response = await SendAsync(url, ct))
            {
                if (missingIsEmpty && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    yield break;
                }

                EnsureSuccess(response, path);
                page = await response.Content.ReadFromJsonAsync(type, ct) ?? [];
                url = Next(response);
            }

            foreach (var item in page)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>The next page, only on the API's own host so the token never goes elsewhere.</summary>
    private Uri? Next(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in string.Join(',', values).Split(','))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            if (pieces.Length > 1 && pieces.Skip(1).Any(p => p is "rel=\"next\"" or "rel=next")
                && Uri.TryCreate(pieces[0].Trim('<', '>'), UriKind.Absolute, out var next)
                && next.Scheme == Api.Scheme && next.Authority.Equals(Api.Authority, StringComparison.OrdinalIgnoreCase))
            {
                return next;
            }
        }

        return null;
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
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Kupro-Import", "1"));
            request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (RateLimited(response) is { } until && attempt < MaxRetries)
            {
                response.Dispose();
                _notBefore = until;
                continue;
            }

            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                response.Dispose();
                _notBefore = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Pow(2, attempt));
                continue;
            }

            return response;
        }
    }

    /// <summary>When to try again, if the response is a rate limit; null otherwise.</summary>
    private DateTimeOffset? RateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        if (response.Headers.RetryAfter is { } retry)
        {
            return now + (retry.Delta ?? (retry.Date - now) ?? TimeSpan.FromMinutes(1));
        }

        if (Header(response, "x-ratelimit-remaining") == "0")
        {
            return long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
                ? DateTimeOffset.FromUnixTimeSeconds(reset).AddSeconds(1)
                : now.AddMinutes(1);
        }

        // A 429 without headers is still a limit; a 403 without them is a permission answer.
        return response.StatusCode == HttpStatusCode.TooManyRequests ? now.AddMinutes(1) : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
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
                new("GitHub rejected the token. Use a fine-grained token with Issues and Metadata (read); it may have expired or been revoked.", status),
            HttpStatusCode.Forbidden => new($"The token may not read {resource} on GitHub.", status),
            HttpStatusCode.NotFound => new($"GitHub has no {resource}, or the token can't see it.", status),
            _ => new($"GitHub answered {(int)status} for {resource}.", status),
        };
    }
}
