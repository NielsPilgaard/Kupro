using System.Text;
using static Flowstate.BackupAgent.Pages.Html;

namespace Flowstate.BackupAgent.Pages;

/// <summary>Overview and History.</summary>
public static class OverviewPages
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/", Overview);
        app.MapGet("/history", History);
        app.MapGet("/healthz", () => Results.Text("ok"));
    }

    private static async Task<IResult> Overview(HttpContext context, StatusBuilder statusBuilder, WalState wal, RepoInfoCache repo, JobRunner jobs, StateStore state, OpsBucket ops, CancellationToken cancellationToken)
    {
        var status = await statusBuilder.BuildAsync(cancellationToken);
        var now = status.GeneratedAt;
        var w = wal.Current;
        var snapshot = repo.Current;
        var secured = status.DataSecuredAt;
        var securedClass = secured is { } at && now - at <= TimeSpan.FromMinutes(15) ? "good" : "bad";
        var body = new StringBuilder();

        body.Append($"""
            <div class="hero {securedClass}">
              <p class="big">Data secured {E(Fmt.Ago(secured, now))}</p>
              <p class="muted">Newest WAL in repo: <span class="mono">{E(status.Wal.RepoNewestSegment ?? "—")}</span> ({E(Fmt.Ago(status.Wal.RepoNewestSegmentAt, now))}) · last push {E(Fmt.Ago(w.LastPushAt, now))}</p>
            </div>
            """);

        if (jobs.Current is { } running)
        {
            body.Append($"""<div class="alert warn">Running: {E(running.Title)} · <a href="/job/{running.Id}">log</a></div>""");
        }

        if (status.Issues.Count > 0)
        {
            body.Append($"""<div class="alert {HealthClass(status.Health)}"><ul class="issues">{string.Concat(status.Issues.Select(i => $"<li>{E(i)}</li>"))}</ul></div>""");
        }

        var full = snapshot.NewestFull;
        var retentionAge = status.Backups.OldestRestorableAt is { } oldest ? (now - oldest).TotalDays : (double?)null;
        var retained = status.Wal.RetainedBytes ?? 0;
        var cap = Math.Max(status.Wal.SlotCapBytes, 1);
        var slotPercent = Math.Min(100, retained * 100 / cap);
        var lastDrill = state.Read(s => s.LastDrill);

        body.Append("""<div class="grid">""");
        body.Append(Card("Last full backup", full is null
            ? """<p class="bad">None</p>"""
            : $"""
                <p class="mid">{E(Fmt.Ago(full.StoppedAt, now))}</p>
                <p class="muted small">{E(full.Label)} · {E(Fmt.DateTime(full.StoppedAt))}<br>{E(Fmt.Bytes(full.DatabaseBytes))} ({E(Fmt.Bytes(full.RepoBytes))} in repo) · {E(Fmt.Duration(full.DurationSeconds))}</p>
                """));
        body.Append(Card("Oldest restore point", status.Backups.OldestRestorableAt is null
            ? """<p class="muted">None yet</p>"""
            : $"""
                <p class="mid">{E(Fmt.DateTime(status.Backups.OldestRestorableAt))}</p>
                <p class="small">{(status.Backups.RetentionOk ? $"<span class=\"pill good\">Within {status.Backups.RetentionDays} days</span>" : $"<span class=\"pill bad\">Older than {status.Backups.RetentionDays} days (DPA)</span>")}
                <span class="muted">{retentionAge:0.#} days back · {status.Backups.Count} backups</span></p>
                """));
        body.Append(Card("Replication slot", $"""
            <p>{SlotLabel(status.Wal.SlotStatus)} · receiver {(status.Wal.ReceiverPaused ? "<span class=\"pill bad\">paused</span>" : status.Wal.ReceiverRunning ? "<span class=\"pill good\">running</span>" : "<span class=\"pill warn\">stopped</span>")}</p>
            <p class="small muted">Held by Postgres: {E(Fmt.Bytes(retained))} of {E(Fmt.Bytes(status.Wal.SlotCapBytes))}</p>
            <meter min="0" max="100" low="50" high="75" optimum="0" value="{slotPercent}"></meter>
            <p class="small muted">Not pushed yet: {E(Fmt.Bytes(status.Wal.SpoolBytes))}{(w.ReceiverLastLine is null ? "" : $"<br>pg_receivewal: {E(w.ReceiverLastLine)}")}</p>
            """));
        body.Append(Card("Disk", status.Disk is null ? """<p class="muted">Unknown</p>""" : $"""
            <p class="mid">{status.Disk.UsedPercent}% used</p>
            <meter min="0" max="100" low="70" high="80" optimum="0" value="{status.Disk.UsedPercent}"></meter>
            <p class="small muted">{E(Fmt.Bytes(status.Disk.FreeBytes))} free</p>
            """));
        body.Append(Card("Last drill", lastDrill is null ? """<p class="muted">None yet</p>""" : $"""
            <p>{Ok(lastDrill.Ok)} {E(Fmt.Ago(lastDrill.At, now))}</p>
            <p class="small muted">RTO {E(Fmt.Duration(lastDrill.TotalSeconds))} · {lastDrill.Tables} tables, {Fmt.Number(lastDrill.Rows)} rows<br>Target: {E(lastDrill.TargetDescription)}</p>
            """));
        body.Append(Card("Last verify", status.Verify.LastAt is null ? """<p class="muted">None yet</p>""" : $"""
            <p>{Ok(status.Verify.LastOk)} {E(Fmt.Ago(status.Verify.LastAt, now))}</p>
            """));
        body.Append(Card("Next runs", $"""
            <p class="small">Full backup: {E(Fmt.DateTime(status.Schedule.NextFull))}<br>Verify: {E(Fmt.DateTime(status.Schedule.NextVerify))}<br>Drill: {E(Fmt.DateTime(status.Schedule.NextDrill))}</p>
            """));
        body.Append(Card("Heartbeats and ops bucket", $"""
            <p class="small">{string.Join("<br>", Enum.GetValues<Heartbeats.Kind>().Select(k => $"{k}: {HeartbeatLine(context, k, now)}"))}</p>
            <p class="small muted">Ops bucket: {(ops.IsConfigured ? ops.LastError is null ? $"written {E(Fmt.Ago(ops.LastWriteOkAt, now))}" : $"<span class=\"pill bad\">error</span> {E(ops.LastError)}" : "not set up")}</p>
            """));
        body.Append(Card("Volumes", $"""
            <p class="small">Live: <b>{E(status.Volumes.Live)}</b> (timeline {status.Postgres.Timeline?.ToString() ?? "?"}, Postgres {(status.Postgres.Reachable ? "up" : "<span class=\"pill bad\">down</span>")})<br>
            Spare: <b>{E(status.Volumes.Spare)}</b> – {E(status.Volumes.SpareContents)}{(status.Volumes.GoLivePending ? " <span class=\"pill warn\">live on restart</span>" : "")}{(status.Volumes.OldVolumeSince is { } since ? $"<br>Old database kept {(now - since).TotalDays:0.#} days" : "")}</p>
            """));
        body.Append("</div>");

        if (status.SshTunnelCommand is { } ssh)
        {
            body.Append($"""<p class="muted small">Tunnel: <code>{E(ssh)}</code></p>""");
        }

        return Page(context, "Overview", body.ToString(), status);
    }

    private static string SlotLabel(string status) => status switch
    {
        "reserved" => """<span class="pill good">reserved</span>""",
        "extended" => """<span class="pill warn">extended</span>""",
        "unreserved" => """<span class="pill bad">over limit</span>""",
        "lost" => """<span class="pill bad">lost</span>""",
        "missing" => """<span class="pill bad">missing</span>""",
        _ => $"""<span class="pill muted">{E(status)}</span>""",
    };

    private static string HeartbeatLine(HttpContext context, Heartbeats.Kind kind, DateTimeOffset now)
    {
        var heartbeats = context.RequestServices.GetRequiredService<Heartbeats>();
        if (!heartbeats.IsConfigured(kind))
        {
            return "<span class=\"muted\">not set up</span>";
        }

        return heartbeats.LastSent(kind) is { } last
            ? $"<span class=\"pill {HealthClass(last.Result)}\">{E(last.Result)}</span> {E(Fmt.Ago(last.At, now))}"
            : "<span class=\"muted\">not sent yet</span>";
    }

    private static readonly Dictionary<string, string> KindLabels = new()
    {
        ["backup"] = "Backup",
        ["drill"] = "Drill",
        ["verify"] = "Verify",
        ["restore"] = "Restore",
        ["deleteoldvolume"] = "Delete old volume",
        ["golive"] = "Go live",
        ["wal-gap"] = "WAL gap",
        ["slot"] = "Slot",
        ["timeline"] = "Timeline",
        ["agent"] = "Agent",
        ["action"] = "Action",
        ["checklist"] = "Checklist",
        ["manual-drill"] = "Manual drill",
    };

    private static async Task<IResult> History(HttpContext context, StatusBuilder statusBuilder, OpsBucket ops, CancellationToken cancellationToken)
    {
        var status = await statusBuilder.BuildAsync(cancellationToken);
        var rows = string.Concat(ops.ReadHistory().Take(500).Select(e => $"""
            <tr><td>{E(Fmt.DateTime(e.At))}</td><td>{E(KindLabels.GetValueOrDefault(e.Kind, e.Kind))}</td><td>{Ok(e.Ok)}</td>
            <td>{E(e.Summary)}</td><td>{(e.DurationSeconds is { } d ? E(Fmt.Duration(d)) : "")}</td></tr>
            """));
        var body = $"""
            <h1>History</h1>
            <p class="muted">Last 3 months. Also in <code>history/</code> in the ops bucket.</p>
            <div class="table-wrap"><table>
            <thead><tr><th>Time</th><th>Type</th><th></th><th>Summary</th><th>Duration</th></tr></thead>
            <tbody>{(rows.Length == 0 ? "<tr><td colspan=\"5\" class=\"muted\">No history yet</td></tr>" : rows)}</tbody>
            </table></div>
            """;
        return Page(context, "History", body, status);
    }
}
