using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kupro.Import.Monday;

/// <summary>
/// HTTP for Monday's GraphQL API (task 027 Access): a personal API token in <c>Authorization</c>, only to
/// <c>api.monday.com</c>, and a pinned <c>API-Version</c>. Monday limits query complexity per minute and answers a
/// complexity or rate-limit error with how long to wait; the client waits that out.
/// </summary>
internal sealed class MondayClient(HttpClient http, string token, TimeProvider time)
{
    public static readonly Uri Api = new("https://api.monday.com/v2");
    private const string ApiVersion = "2025-04";
    private const int MaxRetries = 6;

    private DateTimeOffset _notBefore;

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (wait, ct) => Task.Delay(wait, time, ct);

    /// <summary>Runs one query and returns its <c>data</c>. Any GraphQL error fails the call: a partial page would drop items.</summary>
    public async Task<JsonObject> QueryAsync(string query, JsonObject? variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables ?? [] }.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            var wait = _notBefore - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Delay(wait, ct);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Api) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", token.Trim());
            request.Headers.Add("API-Version", ApiVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, ct);
            JsonObject? parsed = null;
            try
            {
                parsed = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            }
            catch (JsonException)
            {
                // Not Monday's JSON (a proxy's error page); judged by the status below.
            }

            var errors = parsed?["errors"] as JsonArray;
            var code = (string?)parsed?["error_code"] ?? errors?.Select(e => (string?)e?["extensions"]?["code"]).FirstOrDefault(c => c is not null);
            var limited = response.StatusCode == HttpStatusCode.TooManyRequests
                || code is "ComplexityException" or "COMPLEXITY_BUDGET_EXHAUSTED" or "RATE_LIMIT_EXCEEDED" or "maxConcurrencyExceeded";
            if ((limited || response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway) && attempt < MaxRetries)
            {
                _notBefore = time.GetUtcNow() + (RetryIn(response, parsed) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) * (limited ? 15 : 1)));
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || code is "UserUnauthorizedException" or "USER_UNAUTHORIZED" or "Unauthorized")
            {
                throw new SourceAccessException("Monday rejected the token. Use a personal API token (Profile → Developers → My access tokens); it may have been regenerated.", HttpStatusCode.Unauthorized);
            }

            if (!response.IsSuccessStatusCode || errors is { Count: > 0 } || parsed?["data"] is not JsonObject data)
            {
                var message = (string?)parsed?["error_message"] ?? errors?.Select(e => (string?)e?["message"]).FirstOrDefault() ?? $"HTTP {(int)response.StatusCode}";
                throw new SourceAccessException($"Monday refused a query: {message}", response.StatusCode);
            }

            return data;
        }
    }

    /// <summary>A file at its signed public URL (https only, no token).</summary>
    public async Task<Stream> DownloadAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new SourceAccessException($"File {url.AbsolutePath} is not an https address; it was not downloaded.");
        }

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SourceAccessException($"Downloading {url.AbsolutePath} answered {(int)response.StatusCode}.", response.StatusCode);
        }

        var content = new MemoryStream();
        await response.Content.CopyToAsync(content, ct);
        content.Position = 0;
        return content;
    }

    private TimeSpan? RetryIn(HttpResponseMessage response, JsonObject? parsed)
    {
        if (response.Headers.RetryAfter is { } header && (header.Delta ?? (header.Date - time.GetUtcNow())) is { } delta)
        {
            return delta;
        }

        var seconds = parsed?["errors"]?.AsArray().Select(e => e?["extensions"]?["retry_in_seconds"]).FirstOrDefault(s => s is not null)
            ?? parsed?["retry_in_seconds"];
        return seconds is JsonValue value && value.TryGetValue<double>(out var s) ? TimeSpan.FromSeconds(s)
            : seconds is JsonValue text && double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? TimeSpan.FromSeconds(t)
            : null;
    }
}
