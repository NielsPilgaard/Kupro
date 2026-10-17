using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;

namespace Flowstate.BackupAgent;

/// <summary>
/// Dead-man's-switch heartbeats (ADR-016): the vendor alerts on Unhealthy and on a missing
/// heartbeat. <see cref="HeartbeatOptions.Provider"/> picks the vendor. A failed send only logs:
/// a broken alerting vendor must never stop a backup.
/// </summary>
public sealed class Heartbeats(IHeartbeatSender sender, IOptions<HeartbeatOptions> options, ILogger<Heartbeats> logger)
{
    public enum Kind
    {
        Wal,
        Backup,
        Drill,
        Verify,
    }

    private readonly ConcurrentDictionary<Kind, (DateTimeOffset At, Health Result)> _lastSent = new();

    private string Target(Kind kind) => kind switch
    {
        Kind.Wal => options.Value.Wal,
        Kind.Backup => options.Value.Backup,
        Kind.Drill => options.Value.Drill,
        _ => options.Value.Verify,
    };

    public bool IsConfigured(Kind kind) => sender.IsConfigured && !string.IsNullOrWhiteSpace(Target(kind));

    public (DateTimeOffset At, Health Result)? LastSent(Kind kind) => _lastSent.TryGetValue(kind, out var last) ? last : null;

    public async Task SendAsync(Kind kind, Health result, string? reason, TimeSpan? took, CancellationToken cancellationToken)
    {
        if (!IsConfigured(kind))
        {
            return;
        }

        try
        {
            using var response = await sender.SendAsync(Target(kind).Trim(), result, reason, took, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Provider} heartbeat {Kind} returned {Status}", options.Value.Provider, kind, (int)response.StatusCode);
                return;
            }

            _lastSent[kind] = (DateTimeOffset.UtcNow, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send {Provider} heartbeat {Kind}", options.Value.Provider, kind);
        }
    }
}

/// <summary>One alerting vendor. <c>target</c> is the per-heartbeat setting, whose meaning is the vendor's.</summary>
public interface IHeartbeatSender
{
    /// <summary>The vendor's own settings (keys, ids) are present. Per-heartbeat targets are checked by <see cref="Heartbeats"/>.</summary>
    bool IsConfigured { get; }

    Task<HttpResponseMessage> SendAsync(string target, Health result, string? reason, TimeSpan? took, CancellationToken cancellationToken);
}

/// <summary>elmah.io Heartbeats. The target is the heartbeat id; elmah.io knows Healthy, Degraded and Unhealthy.</summary>
public sealed class ElmahIoHeartbeatSender(IHttpClientFactory httpClientFactory, IOptions<HeartbeatOptions> options) : IHeartbeatSender
{
    private ElmahIoHeartbeatOptions Options => options.Value.ElmahIo;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Options.ApiKey) && !string.IsNullOrWhiteSpace(Options.LogId);

    public Task<HttpResponseMessage> SendAsync(string target, Health result, string? reason, TimeSpan? took, CancellationToken cancellationToken) =>
        httpClientFactory.CreateClient().PostAsJsonAsync(
            $"{Options.BaseUrl.TrimEnd('/')}/v3/heartbeats/{Options.LogId}/{target}?api_key={Uri.EscapeDataString(Options.ApiKey)}",
            new { result = result.ToString(), reason, took = took is { } t ? (long?)t.TotalMilliseconds : null, application = "backup-agent" },
            cancellationToken);
}

/// <summary>
/// Ping-URL vendors (Healthchecks.io, Better Stack, Cronitor and the like). The target is the
/// heartbeat's ping URL. A request to it is a success, the URL plus <see cref="PingHeartbeatOptions.FailSuffix"/>
/// a failure. These vendors have no "degraded", so Degraded pings as a success with the reason in the
/// body; Degraded issues still show in the console and status.json.
/// </summary>
public sealed class PingHeartbeatSender(IHttpClientFactory httpClientFactory, IOptions<HeartbeatOptions> options) : IHeartbeatSender
{
    public bool IsConfigured => true;

    public Task<HttpResponseMessage> SendAsync(string target, Health result, string? reason, TimeSpan? took, CancellationToken cancellationToken)
    {
        var url = result == Health.Unhealthy ? target + options.Value.Ping.FailSuffix : target;
        var body = took is { } t ? $"{result}: {reason} (took {t.TotalSeconds:0.#} s)" : $"{result}: {reason}";
        return httpClientFactory.CreateClient().PostAsync(url, new StringContent(body, Encoding.UTF8, "text/plain"), cancellationToken);
    }
}
