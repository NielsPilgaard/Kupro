using System.Net;

namespace Kupro.Import;

/// <summary>
/// Pacing for one source's API: waits out what the source last asked for before every request, and retries a throttled
/// or briefly unavailable answer after the wait the source names, else an exponential backoff. A client says what its
/// source's throttling looks like; this holds the loop, the clock and the test seam.
/// </summary>
internal sealed class SourceThrottle(TimeProvider time)
{
    public const int MaxRetries = 6;

    private DateTimeOffset _notBefore;

    /// <summary>Waits between requests; replaced in tests so throttling doesn't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (wait, ct) => Task.Delay(wait, time, ct);

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>
    /// Sends <paramref name="request"/> (built anew per attempt) until <paramref name="retryIn"/> stops asking for a
    /// retry or <see cref="MaxRetries"/> is reached. <paramref name="retryIn"/> gets the response and the attempt, and
    /// answers how long to wait first, or null to keep the response.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<HttpRequestMessage> request, Func<HttpResponseMessage, int, ValueTask<TimeSpan?>> retryIn, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitAsync(ct);
            using var message = request();
            var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (attempt < MaxRetries && await retryIn(response, attempt) is { } wait)
            {
                response.Dispose();
                PauseFor(wait);
                continue;
            }

            return response;
        }
    }

    /// <summary>The usual answer for a REST source: 429 and 503 wait for <c>Retry-After</c>, else back off.</summary>
    public ValueTask<TimeSpan?> RetryIn(HttpResponseMessage response, int attempt) =>
        ValueTask.FromResult(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            ? RetryAfter(response) ?? Backoff(attempt)
            : (TimeSpan?)null);

    /// <summary>Waits until the time the source last asked for.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        var wait = _notBefore - Now;
        if (wait > TimeSpan.Zero)
        {
            await Delay(wait, ct);
        }
    }

    /// <summary>Holds the next request back by <paramref name="wait"/> from now.</summary>
    public void PauseFor(TimeSpan wait) => _notBefore = Now + wait;

    /// <summary>The response's <c>Retry-After</c>, as a delay.</summary>
    public TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date - Now) : null;

    /// <summary>1, 2, 4, … seconds, times <paramref name="factor"/>.</summary>
    public static TimeSpan Backoff(int attempt, double factor = 1) => TimeSpan.FromSeconds(Math.Pow(2, attempt) * factor);

    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
