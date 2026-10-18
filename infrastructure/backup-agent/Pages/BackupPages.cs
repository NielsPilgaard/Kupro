using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using static Kupro.BackupAgent.Pages.Html;

namespace Kupro.BackupAgent.Pages;

/// <summary>Backups, Drills, the safe actions (backup, verify, drill) and the live job log.</summary>
public static class BackupPages
{
    private static readonly TimeSpan ManualBackupInterval = TimeSpan.FromHours(1);
    private const string Busy = "Another job is running.";

    public static void Map(WebApplication app)
    {
        app.MapGet("/backups", Backups);
        app.MapGet("/drills", Drills);
        app.MapPost("/actions/backup", StartBackup);
        app.MapPost("/actions/verify", StartVerify);
        app.MapPost("/actions/drill", StartDrill);
        app.MapPost("/drills/manual", LogManualDrill);
        app.MapGet("/job/{id:guid}", JobPage);
        app.MapGet("/job/{id:guid}/log", JobLog);
    }

    /// <summary>Working hours on weekdays, when a backup's I/O competes with teams using the app.</summary>
    private static bool InWorkingHours(DateTimeOffset now) =>
        Fmt.Local(now) is { DayOfWeek: not (DayOfWeek.Saturday or DayOfWeek.Sunday), Hour: >= 8 and < 17 };

    private static async Task<IResult> Backups(HttpContext context, StatusBuilder statusBuilder, RepoInfoCache repo, StateStore state, JobRunner jobs, CancellationToken cancellationToken)
    {
        var status = await statusBuilder.BuildAsync(cancellationToken);
        var snapshot = repo.Current;
        var now = DateTimeOffset.UtcNow;
        var (lastManual, gaps) = state.Read(s => (s.LastManualBackupAt, s.Gaps.ToList()));
        var busy = jobs.Current is not null;
        var tooSoon = lastManual is { } last && now - last < ManualBackupInterval;
        var workingHours = InWorkingHours(now);

        var ioConfirm = workingHours
            ? "<label class=\"choice\"><input type=\"checkbox\" name=\"confirmIo\" value=\"true\"> Working hours (08–17): I accept the extra I/O</label>"
            : "";
        var backupForm = Form(context, "/actions/backup", $"""
            <div class="actions">
              <label class="choice"><input type="radio" name="type" value="full" checked> Full</label>
              <label class="choice"><input type="radio" name="type" value="incr"> Incremental</label>
            </div>
            {ioConfirm}
            <div class="actions"><button class="primary" type="submit"{(busy || tooSoon ? " disabled" : "")}>Back up now</button>
            <span class="muted small">Max once per hour.{(lastManual is null ? "" : $" Last: {E(Fmt.Ago(lastManual, now))}.")}</span></div>
            """, "stack");
        var verifyForm = Form(context, "/actions/verify", $"""
            <div class="actions"><button type="submit"{(busy ? " disabled" : "")}>Verify now</button>
            <span class="muted small">Reads and decrypts all backups and WAL.</span></div>
            """);

        var ranges = string.Concat(snapshot.Ranges.OrderByDescending(r => r.To).Select(r =>
            $"<tr><td>{r.Timeline}</td><td>{E(Fmt.DateTime(r.From))}</td><td>{E(Fmt.DateTime(r.To))}</td><td class=\"mono\">{E(r.FromBackup)}</td></tr>"));
        var backupRows = string.Concat(snapshot.Backups.OrderByDescending(b => b.StartedAt).Select(b => $"""
            <tr><td class="mono">{E(b.Label)}</td><td>{(b.Type == "full" ? "Full" : b.Type == "incr" ? "Incremental" : E(b.Type))}</td>
            <td>{E(Fmt.DateTime(b.StartedAt))}</td><td>{E(Fmt.Duration(b.DurationSeconds))}</td>
            <td>{E(Fmt.Bytes(b.DatabaseBytes))}</td><td>{E(Fmt.Bytes(b.RepoBytes))}</td><td class="mono small">{E(b.WalStart)}<br>{E(b.WalStop)}</td></tr>
            """));
        var gapRows = string.Concat(gaps.OrderByDescending(g => g.DetectedAt).Take(20).Select(g => $"""
            <tr><td>{E(Fmt.DateTime(g.DetectedAt))}</td><td>{E(g.Reason)}</td><td class="mono">{E(g.LastPushedSegment ?? "—")}</td>
            <td>{(g.ClosedAt is null ? "<span class=\"pill bad\">open</span>" : $"closed {E(Fmt.DateTime(g.ClosedAt))} by <span class=\"mono\">{E(g.ClosedByBackup)}</span>")}</td></tr>
            """));
        var segments = snapshot.Segments;
        var walRange = segments.Count == 0
            ? "No WAL in the repo yet."
            : $"{segments.Count} WAL segments, <span class=\"mono\">{E(segments.MinBy(s => (s.Timeline, s.Index))!.Name)}</span> to <span class=\"mono\">{E(snapshot.NewestSegment!.Name)}</span> (pushed {E(Fmt.Ago(snapshot.NewestSegment.PushedAt, now))}).";

        var body = $"""
            <h1>Backups</h1>
            <div class="grid">
            {Card("Manual backup", backupForm)}
            {Card("Verify", verifyForm)}
            </div>
            <h2>Restorable</h2>
            <p class="muted">{walRange}</p>
            <div class="table-wrap"><table><thead><tr><th>Timeline</th><th>From</th><th>To</th><th>From backup</th></tr></thead>
            <tbody>{(ranges.Length == 0 ? "<tr><td colspan=\"4\" class=\"muted\">Nothing restorable yet</td></tr>" : ranges)}</tbody></table></div>
            <h2>Backups in the repo</h2>
            <div class="table-wrap"><table><thead><tr><th>Label</th><th>Type</th><th>Start</th><th>Duration</th><th>Database</th><th>In repo</th><th>WAL start/stop</th></tr></thead>
            <tbody>{(backupRows.Length == 0 ? "<tr><td colspan=\"7\" class=\"muted\">No backups</td></tr>" : backupRows)}</tbody></table></div>
            <p class="muted small">From <code>pgbackrest info</code> {E(Fmt.Ago(snapshot.ReadAt == DateTimeOffset.MinValue ? null : snapshot.ReadAt, now))}.</p>
            <h2>WAL gaps</h2>
            <p class="muted">Postgres dropped the agent's slot (over 4 GB behind). Point-in-time restore can't cross a gap; the next full backup closes it.</p>
            <div class="table-wrap"><table><thead><tr><th>Detected</th><th>Reason</th><th>Last WAL before</th><th>Status</th></tr></thead>
            <tbody>{(gapRows.Length == 0 ? "<tr><td colspan=\"4\" class=\"muted\">No gaps</td></tr>" : gapRows)}</tbody></table></div>
            """;
        return Page(context, "Backups", body, status);
    }

    private static async Task<IResult> StartBackup(
        [FromForm] string? type,
        [FromForm] bool? confirmIo,
        JobRunner jobs,
        BackupService backups,
        StateStore state,
        OpsBucket ops,
        Scheduler scheduler,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var backupType = type == "incr" ? "incr" : "full";
        if (state.Read(s => s.LastManualBackupAt) is { } last && now - last < ManualBackupInterval)
        {
            return Results.Redirect(Redirect("/backups", error: $"Last manual backup was {Fmt.Ago(last, now)}. Max one per hour."));
        }

        if (InWorkingHours(now) && confirmIo != true)
        {
            return Results.Redirect(Redirect("/backups", error: "Working hours: tick the box to accept the extra I/O."));
        }

        var title = backupType == "full" ? "Manual full backup" : "Manual incremental backup";
        await Audit(ops, $"Console: {title}", cancellationToken);
        var job = jobs.TryStart(JobKind.Backup, title, async (job, ct) =>
        {
            var outcome = await backups.BackupAsync(job, backupType, "Started from the console", ct);
            scheduler.RefreshSoon();
            return outcome;
        });
        if (job is null)
        {
            return Results.Redirect(Redirect("/backups", error: Busy));
        }

        state.Update(s => s.LastManualBackupAt = now);
        return Results.Redirect($"/job/{job.Id}");
    }

    private static async Task<IResult> StartVerify(JobRunner jobs, BackupService backups, OpsBucket ops, Scheduler scheduler, CancellationToken cancellationToken)
    {
        await Audit(ops, "Console: manual verify", cancellationToken);
        var job = jobs.TryStart(JobKind.Verify, "Manual verify", async (job, ct) =>
        {
            var outcome = await backups.VerifyAsync(job, ct);
            scheduler.RefreshSoon();
            return outcome;
        });
        return job is null
            ? Results.Redirect(Redirect("/backups", error: Busy))
            : Results.Redirect($"/job/{job.Id}");
    }

    private static async Task<IResult> StartDrill(JobRunner jobs, BackupService backups, DrillChecks checks, OpsBucket ops, Scheduler scheduler, CancellationToken cancellationToken)
    {
        await Audit(ops, "Console: manual drill", cancellationToken);
        var job = jobs.TryStart(JobKind.Drill, "Manual drill", async (job, ct) =>
        {
            var outcome = await backups.DrillAsync(job, checks, ct);
            scheduler.RefreshSoon();
            return outcome;
        });
        return job is null
            ? Results.Redirect(Redirect("/drills", error: Busy))
            : Results.Redirect($"/job/{job.Id}");
    }

    public static Task Audit(OpsBucket ops, string action, CancellationToken cancellationToken) =>
        ops.AppendHistoryAsync(new HistoryEntry(DateTimeOffset.UtcNow, "action", null, action), cancellationToken);

    private static async Task<IResult> Drills(HttpContext context, StatusBuilder statusBuilder, StateStore state, OpsBucket ops, JobRunner jobs, CancellationToken cancellationToken)
    {
        var status = await statusBuilder.BuildAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var (lastDrill, manual, nextManual) = state.Read(s => (s.LastDrill, s.LastManualDrill, s.NextManualDrillDue));
        var history = ops.ReadHistory(months: 6);
        var drills = history.Where(h => h.Kind == "drill" && h.Details is not null)
            .Select(h => (Entry: h, Result: h.Details!.Value.Deserialize<DrillResult>(AgentJson.Options)))
            .Where(x => x.Result is not null)
            .ToList();
        var manualDrills = history.Where(h => h.Kind == "manual-drill").ToList();
        var busy = jobs.Current is not null;

        var drillRows = string.Concat(drills.Take(30).Select(d => $"""
            <tr><td>{E(Fmt.DateTime(d.Entry.At))}</td><td>{Ok(d.Result!.Ok)}</td><td>{E(Fmt.Duration(d.Result.TotalSeconds))}</td>
            <td>{E(Fmt.Duration(d.Result.RestoreSeconds))} + {E(Fmt.Duration(d.Result.RecoverySeconds))}</td><td>{E(d.Result.TargetDescription)}</td>
            <td>{d.Result.Checks.Count(c => c.Ok == true)}/{d.Result.Checks.Count}{string.Concat(d.Result.Checks.Where(c => c.Ok == false).Select(c => $"<br><span class=\"small\">{E(c.Name)}: {E(c.Detail)}</span>"))}</td></tr>
            """));
        var trend = Sparkline([.. drills.Select(d => d.Result!.TotalSeconds / 60).Reverse()], "Restore time per drill, minutes");
        var overdue = nextManual is { } due && due < Fmt.LocalDate(now);
        var manualRows = string.Concat(manualDrills.Take(12).Select(m => $"<tr><td>{E(Fmt.Date(m.At))}</td><td>{E(m.Summary)}</td></tr>"));

        var drillForm = Form(context, "/actions/drill", $"""
            <div class="actions"><button class="primary" type="submit"{(busy ? " disabled" : "")}>Drill now</button>
            <span class="muted small">Restores to tmpfs, checks against live, discards the copy.</span></div>
            """);
        var manualForm = Form(context, "/drills/manual", $"""
            <label>Date <input type="date" name="date" required value="{Fmt.LocalDate(now):yyyy-MM-dd}"></label>
            <label>Measured RTO (minutes, VPS gone → app running) <input type="number" name="rtoMinutes" min="1" max="10000" required></label>
            <label>What broke? (no personal data) <textarea name="notes" rows="3" maxlength="2000"></textarea></label>
            <div><button type="submit">Log manual drill</button></div>
            """, "stack");

        var body = $"""
            <h1>Drills</h1>
            <div class="grid">
            {Card("Weekly drill", drillForm + (lastDrill is null ? "<p class=\"muted\">No drill yet.</p>" : $"<p>Last: {Ok(lastDrill.Ok)} {E(Fmt.DateTime(lastDrill.At))} · RTO {E(Fmt.Duration(lastDrill.TotalSeconds))}</p>"))}
            {Card("Restore time (RTO)", trend.Length == 0 ? "<p class=\"muted\">Needs two drills.</p>" : trend + "<p class=\"muted small\">Minutes, oldest left.</p>")}
            {Card("Quarterly manual drill", $"<p>Last: {(manual is null ? "never" : $"{E(Fmt.Date(manual.Date))}, RTO {manual.RtoMinutes} min")}</p><p class=\"{(overdue ? "pill bad" : "muted")}\">Next due: {(nextManual is { } n ? E(Fmt.Date(n)) : "—")}</p>")}
            </div>
            {(lastDrill is null ? "" : $"<h2>Last drill: checks</h2>{Checks(lastDrill.Checks)}")}
            <h2>Drill history</h2>
            <div class="table-wrap"><table><thead><tr><th>Time</th><th></th><th>RTO</th><th>Restore + WAL</th><th>Target</th><th>Checks</th></tr></thead>
            <tbody>{(drillRows.Length == 0 ? "<tr><td colspan=\"6\" class=\"muted\">No drills yet</td></tr>" : drillRows)}</tbody></table></div>
            <h2>Log the quarterly manual drill</h2>
            <p class="muted">Rebuild prod from scratch with the restore runbook (docs/runbooks/restore.md) and the password manager.</p>
            {manualForm}
            <div class="table-wrap"><table><thead><tr><th>Date</th><th>Result</th></tr></thead>
            <tbody>{(manualRows.Length == 0 ? "<tr><td colspan=\"2\" class=\"muted\">None logged</td></tr>" : manualRows)}</tbody></table></div>
            """;
        return Page(context, "Drills", body, status);
    }

    private static async Task<IResult> LogManualDrill([FromForm] string? date, [FromForm] int? rtoMinutes, [FromForm] string? notes, StateStore state, OpsBucket ops, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || rtoMinutes is not > 0)
        {
            return Results.Redirect(Redirect("/drills", error: "Fill in date and RTO."));
        }

        var text = (notes ?? "").Trim();
        if (text.Length > 2000)
        {
            text = text[..2000];
        }

        var drill = new ManualDrill(day, rtoMinutes.Value, text, DateTimeOffset.UtcNow);
        state.Update(s => s.LastManualDrill = drill);
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        await ops.AppendHistoryAsync(new HistoryEntry(at, "manual-drill", true,
            $"RTO {rtoMinutes} min.{(text.Length == 0 ? "" : $" {text}")}", rtoMinutes * 60.0, drill.ToJson()), cancellationToken);
        return Results.Redirect(Redirect("/drills", ok: "Manual drill logged."));
    }

    private static async Task<IResult> JobPage(HttpContext context, Guid id, JobRunner jobs, StatusBuilder statusBuilder, CancellationToken cancellationToken)
    {
        var status = await statusBuilder.BuildAsync(cancellationToken);
        if (jobs.Find(id) is not { } job)
        {
            return Page(context, "Job", "<h1>Job</h1><p class=\"muted\">Job gone (the agent restarted). See History.</p>", status);
        }

        var body = $"""
            <h1>{E(job.Title)}</h1>
            <p>Started {E(Fmt.DateTimeSeconds(job.StartedAt))} · <span id="job-state" class="pill warn">Running…</span></p>
            <div id="job-summary"></div>
            <pre class="log" id="log" data-job="{job.Id}"></pre>
            <p><a href="/">Overview</a> · <a href="/history">History</a></p>
            """;
        return Page(context, job.Title, body, status);
    }

    private static IResult JobLog(Guid id, int? after, JobRunner jobs)
    {
        if (jobs.Find(id) is not { } job)
        {
            return Results.NotFound();
        }

        var (lines, next) = job.LinesFrom(after ?? 0);
        return Results.Json(new { lines, next, done = job.EndedAt is not null, ok = job.Ok, summary = job.Summary });
    }
}
