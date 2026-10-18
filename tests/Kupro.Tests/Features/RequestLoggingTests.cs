using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kupro.Api.Common;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kupro.Tests.Features;

/// <summary>
/// The Grafana dashboards and alerts query these request log lines in Loki (ADR-014). A failure here means the
/// panels in infrastructure/observability/ would go blank or wrong in production.
/// </summary>
public sealed class RequestLoggingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private readonly CapturingLoggerProvider _logs = new();
    private WebApplicationFactory<Program> _app = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _app = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(_logs)));
    }

    [Fact]
    public async Task Request_is_logged_as_one_line_with_route_status_duration_and_caller()
    {
        using var client = _app.CreateClient();

        (await client.GetAsync("/api/v1/status", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var line = await _logs.SingleRequestLogAsync("/api/v1/status");
        line.EventName.ShouldBe("RequestResponseLog");
        line.Fields["Method"].ShouldBe("GET");
        line.Fields["StatusCode"].ShouldBe(200);
        line.Fields["Duration"].ShouldBeOfType<double>();
        line.Fields[RequestLogging.RouteField].ShouldBe("/api/v1/status");
        line.Fields[RequestLogging.CallerField].ShouldBe(CallerKind.Anonymous);
    }

    [Fact]
    public async Task Route_is_the_template_and_query_string_is_not_logged()
    {
        using var client = Bearer(KuproApiFactory.CreateJwt(TestUser.New()));
        var path = $"/api/v1/tokens/{Guid.NewGuid()}";

        await client.DeleteAsync(path + "?secret=hunter2", Ct);

        var line = await _logs.SingleRequestLogAsync(path);
        line.Fields[RequestLogging.RouteField].ShouldBe("/api/v1/tokens/{id:guid}");
        line.Fields[RequestLogging.CallerField].ShouldBe(CallerKind.Session);
        line.Fields.Values.ShouldNotContain(v => v != null && v.ToString()!.Contains("hunter2"));
    }

    [Fact]
    public async Task Agent_and_personal_tokens_are_told_apart()
    {
        using var human = Bearer(KuproApiFactory.CreateJwt(TestUser.New()));
        var personal = await CreateTokenAsync(human, new("laptop", ApiTokenKind.Personal, null, null));
        // Agent tokens belong to a workspace (task 003).
        var slug = "w-" + Guid.NewGuid().ToString("N")[..12];
        await human.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest("Logs", slug), Json.Options, Ct);
        var agent = await CreateTokenAsync(human, new("triage", ApiTokenKind.Agent, "triage-agent", 30, slug));
        _logs.Clear();

        using (var viaPersonal = Bearer(personal.Secret))
        {
            await viaPersonal.GetAsync("/api/v1/me", Ct);
        }

        (await _logs.SingleRequestLogAsync("/api/v1/me")).Fields[RequestLogging.CallerField].ShouldBe(CallerKind.Token);
        _logs.Clear();

        using (var viaAgent = Bearer(agent.Secret))
        {
            await viaAgent.GetAsync("/api/v1/me", Ct);
        }

        (await _logs.SingleRequestLogAsync("/api/v1/me")).Fields[RequestLogging.CallerField].ShouldBe(CallerKind.Agent);
    }

    [Fact]
    public async Task Unmatched_paths_are_grouped()
    {
        using var client = _app.CreateClient();

        await client.GetAsync("/wp-login.php", Ct);

        var line = await _logs.SingleRequestLogAsync("/wp-login.php");
        line.Fields[RequestLogging.RouteField].ShouldBe(RequestLogging.UnmatchedRoute);
    }

    [Fact]
    public async Task Health_probes_are_not_logged()
    {
        using var client = _app.CreateClient();

        await client.GetAsync("/alive", Ct);
        await client.GetAsync("/health", Ct);
        await client.GetAsync("/api/v1/status", Ct);

        // The status line proves logging ran; the probes before it must not have produced lines.
        await _logs.SingleRequestLogAsync("/api/v1/status");
        _logs.RequestLogs.Count.ShouldBe(1);
    }

    private HttpClient Bearer(string secret)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private static async Task<CreatedApiTokenResponse> CreateTokenAsync(HttpClient client, CreateApiTokenRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tokens", request, Json.Options, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(Json.Options, Ct))!;
    }

    public override async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await base.DisposeAsync();
    }
}

internal sealed record RequestLogLine(string? EventName, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Records HttpLogging lines with their structured fields, the way the OTLP exporter sees them.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private const string HttpLoggingCategory = "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware";
    private readonly ConcurrentQueue<RequestLogLine> _lines = new();

    public IReadOnlyCollection<RequestLogLine> RequestLogs => _lines;

    public void Clear() => _lines.Clear();

    /// <summary>The middleware logs after the response is flushed, so the line can trail the client by a moment.</summary>
    public async Task<RequestLogLine> SingleRequestLogAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var matches = _lines.Where(l => (string?)l.Fields.GetValueOrDefault("Path") == path).ToList();
            if (matches.Count > 0)
            {
                return matches.ShouldHaveSingleItem();
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new ShouldAssertException($"No request log line for {path}.");
    }

    public ILogger CreateLogger(string categoryName) =>
        categoryName == HttpLoggingCategory ? new Logger(_lines) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<RequestLogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                // The OTLP exporter keeps primitives typed and exports anything else (PathString…) via ToString().
                lines.Enqueue(new RequestLogLine(eventId.Name, fields.ToDictionary(
                    f => f.Key,
                    f => f.Value is null or string or int or long or double or bool ? f.Value : f.Value.ToString())));
            }
        }
    }
}
