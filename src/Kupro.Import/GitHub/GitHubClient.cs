using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Kupro.Import.GitHub;

/// <summary>
/// HTTP for GitHub's REST API (task 027 Access): github.com, or GitHub Enterprise Server under <c>/api/v3</c>. Pages
/// follow the <c>Link</c> header. GraphQL (Projects (v2) only) is at <c>/graphql</c>, on a server <c>/api/graphql</c>. The primary rate limit (403 or 429 with <c>x-ratelimit-remaining: 0</c>) waits until
/// <c>x-ratelimit-reset</c>; a secondary limit waits its <c>Retry-After</c>.
/// </summary>
internal sealed class GitHubClient
{
    private const string ApiVersion = "2022-11-28";

    private readonly HttpClient _http;
    private readonly SourceThrottle _throttle;
    private readonly AuthenticationHeaderValue _auth;

    public GitHubClient(HttpClient http, Uri api, string token, TimeProvider time)
    {
        _http = http;
        _throttle = new SourceThrottle(time);
        Api = api;
        GraphQL = new Uri(api, api.AbsolutePath.EndsWith("/api/v3/", StringComparison.OrdinalIgnoreCase) ? "../graphql" : "graphql");
        _auth = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>The API root, ending in a slash: <c>https://api.github.com/</c> or <c>https://ghe.example/api/v3/</c>.</summary>
    public Uri Api { get; }

    public Uri GraphQL { get; }

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get => _throttle.Delay; set => _throttle.Delay = value; }

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(Api, path), null, ct);
        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct) ?? throw Failure(response.StatusCode, path);
    }

    /// <summary>
    /// A GraphQL query: <c>data</c>, and <c>errors</c> (empty when none). GitHub answers with what it could read and an
    /// error per part it couldn't (a project the token may not see), so the caller judges the errors.
    /// </summary>
    public async Task<(JsonObject Data, JsonArray Errors)> QueryAsync(string query, JsonObject variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
        using var response = await SendAsync(GraphQL, body, ct);
        EnsureSuccess(response, "graphql");
        var parsed = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        var errors = parsed?["errors"] as JsonArray ?? [];
        return parsed?["data"] is JsonObject data
            ? (data, errors)
            : throw new SourceAccessException($"GitHub refused a query: {errors.Select(e => (string?)e?["message"]).FirstOrDefault() ?? "no data"}");
    }

    /// <summary>Like <see cref="GetAsync{T}"/>, but null when the resource doesn't exist or isn't readable.</summary>
    public async Task<T?> TryGetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        using var response = await SendAsync(new Uri(Api, path), null, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
        {
            return null;
        }

        EnsureSuccess(response, path);
        return await response.Content.ReadFromJsonAsync(type, ct);
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
            using (var response = await SendAsync(url, null, ct))
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

    /// <summary>A GET, or a POST of <paramref name="graphql"/> when given.</summary>
    private Task<HttpResponseMessage> SendAsync(Uri url, string? graphql, CancellationToken ct) =>
        _throttle.SendAsync(_http, () =>
        {
            var request = graphql is null
                ? new HttpRequestMessage(HttpMethod.Get, url)
                : new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(graphql, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = _auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Kupro-Import", "1"));
            request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            return request;
        }, async (response, attempt) =>
            await RateLimitedAsync(response, graphql is not null, ct) is { } until ? until - _throttle.Now
            : response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable ? SourceThrottle.Backoff(attempt)
            : null, ct);

    private static JsonNode? Parse(string content)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>When to try again, if the response is a rate limit; null otherwise.</summary>
    private async Task<DateTimeOffset?> RateLimitedAsync(HttpResponseMessage response, bool graphql, CancellationToken ct)
    {
        var now = _throttle.Now;
        if (graphql && response.StatusCode == HttpStatusCode.OK)
        {
            // GraphQL answers its primary limit, and can answer a secondary limit, with 200.
            // Only the error parts: a project title that says "secondary rate limit" is data, not a limit.
            var answer = Parse(await response.Content.ReadAsStringAsync(ct));
            var errors = answer?["errors"] as JsonArray ?? [];
            if (SourceThrottle.Header(response, "x-ratelimit-remaining") == "0" && errors.Any(e => (string?)e?["type"] == "RATE_LIMITED"))
            {
                return Reset(response, now);
            }

            if (response.Headers.RetryAfter is { } wait)
            {
                return now + (wait.Delta ?? (wait.Date - now) ?? TimeSpan.FromMinutes(1));
            }

            var messages = errors.Select(e => (string?)e?["message"]).Append(answer?["message"] is JsonValue m ? (string?)m : null);
            return messages.Any(m => m?.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) == true) ? now.AddMinutes(1) : null;
        }

        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return null;
        }

        if (response.Headers.RetryAfter is { } retry)
        {
            return now + (retry.Delta ?? (retry.Date - now) ?? TimeSpan.FromMinutes(1));
        }

        if (SourceThrottle.Header(response, "x-ratelimit-remaining") == "0")
        {
            return Reset(response, now);
        }

        // A 429 without headers is still a limit, and so is a 403 that says so (a secondary limit: wait a minute);
        // any other 403 is a permission answer.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return now.AddMinutes(1);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ? now.AddMinutes(1) : null;
    }

    private static DateTimeOffset Reset(HttpResponseMessage response, DateTimeOffset now) =>
        long.TryParse(SourceThrottle.Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
            ? DateTimeOffset.FromUnixTimeSeconds(reset).AddSeconds(1)
            : now.AddMinutes(1);

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
