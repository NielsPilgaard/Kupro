using System.Net;
using System.Net.Http.Headers;
using Kupro.Import;

namespace Kupro.Tests.Features;

/// <summary>How every connector waits out a throttling source, and the delta cursor they all write (ADR-019 Mode).</summary>
public sealed class SourceThrottleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_throttled_request_waits_what_the_source_asks_then_backs_off()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(30)),
            Answer(HttpStatusCode.ServiceUnavailable),
            Answer(HttpStatusCode.OK),
        ]);
        var (throttle, waits) = Throttle();

        using var response = await throttle.SendAsync(Client(answers), Request, throttle.RetryIn, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        waits.ShouldBe([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)]);
    }

    [Fact]
    public async Task A_source_that_stays_throttled_gets_its_answer_back_after_the_last_retry()
    {
        var answers = new Queue<HttpResponseMessage>(Enumerable.Range(0, SourceThrottle.MaxRetries + 1).Select(_ => Answer(HttpStatusCode.TooManyRequests)));
        var (throttle, waits) = Throttle();

        using var response = await throttle.SendAsync(Client(answers), Request, throttle.RetryIn, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        waits.Count.ShouldBe(SourceThrottle.MaxRetries);
        answers.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pause_holds_back_the_next_request()
    {
        var (throttle, waits) = Throttle();
        throttle.PauseFor(TimeSpan.FromSeconds(10));

        using var response = await throttle.SendAsync(Client(new([Answer(HttpStatusCode.OK)])), Request, throttle.RetryIn, Ct);

        waits.ShouldBe([TimeSpan.FromSeconds(10)]);
    }

    [Fact]
    public void A_cursor_is_the_read_start_and_moves_back_by_the_overlap()
    {
        var started = new DateTimeOffset(2026, 11, 8, 13, 0, 0, TimeSpan.FromHours(1));
        var cursor = DeltaCursor.At(started);

        cursor.ShouldBe("2026-11-08T12:00:00.0000000Z");
        DeltaCursor.Since(cursor).ShouldBe(started);
        DeltaCursor.Since(null).ShouldBeNull();
        DeltaCursor.WithOverlap(cursor, TimeSpan.FromMinutes(10)).ShouldBe("2026-11-08T11:50:00.0000000Z");
        DeltaCursor.WithOverlap("page-7", TimeSpan.FromMinutes(10)).ShouldBe("page-7");
        Should.Throw<ArgumentException>(() => DeltaCursor.Since("page-7"));
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://source.example/api");

    private static (SourceThrottle Throttle, List<TimeSpan> Waits) Throttle()
    {
        // A clock that the waits move forward, so a pause ends when it was waited out.
        var time = new SteppedTime();
        var waits = new List<TimeSpan>();
        var throttle = new SourceThrottle(time)
        {
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                time.Now += wait;
                return Task.CompletedTask;
            },
        };
        return (throttle, waits);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        return response;
    }

    private static HttpClient Client(Queue<HttpResponseMessage> answers) => new(new Answers(answers));

    private sealed class Answers(Queue<HttpResponseMessage> answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answers.Dequeue());
    }

    private sealed class SteppedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
