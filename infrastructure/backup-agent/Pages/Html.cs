using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace Kupro.BackupAgent.Pages;

/// <summary>
/// Server-rendered HTML, no build step and no CDN (ADR-016). Every dynamic value goes through
/// <see cref="E"/>. Styles and the one script are served by the agent itself, so the CSP can stay
/// 'self' only.
/// </summary>
public static class Html
{
    public static string E(object? value) => HtmlEncoder.Default.Encode(value?.ToString() ?? "");

    public static string Attr(object? value) => E(value);

    private static readonly (string Path, string Label)[] Nav =
    [
        ("/", "Overview"),
        ("/backups", "Backups"),
        ("/drills", "Drills"),
        ("/restore", "Restore"),
        ("/history", "History"),
    ];

    public static IResult Page(HttpContext context, string title, string body, AgentStatus? status)
    {
        var path = context.Request.Path.Value ?? "/";
        var nav = new StringBuilder();
        foreach (var (href, label) in Nav)
        {
            var active = href == "/" ? path == "/" : path.StartsWith(href, StringComparison.Ordinal);
            nav.Append($"""<a href="{href}"{(active ? " class=\"active\"" : "")}>{E(label)}</a>""");
        }

        var health = status is null ? "" : $"""<span class="pill {HealthClass(status.Health)}">{E(HealthLabel(status.Health))}</span>""";
        var volumes = status is null ? "" : $"""<span class="muted">Live: <b>{E(status.Volumes.Live)}</b> · Spare: <b>{E(status.Volumes.Spare)}</b></span>{(status.Volumes.GoLivePending ? $"""<a class="pill warn" href="/restore">{E(status.Volumes.Spare)} live on restart</a>""" : "")}""";
        var error = context.Request.Query["error"].ToString();
        var notice = context.Request.Query["ok"].ToString();
        var alerts = (error.Length > 0 ? $"<div class=\"alert bad\">{E(error)}</div>" : "")
            + (notice.Length > 0 ? $"<div class=\"alert good\">{E(notice)}</div>" : "");
        var html = $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(title)} · Backup console</title>
            <link rel="stylesheet" href="/console.css">
            <link rel="icon" href="/favicon.svg" type="image/svg+xml">
            </head>
            <body>
            <header class="top">
              <div class="brand">Kupro · Backup console</div>
              {health}
              {volumes}
            </header>
            <nav class="nav">{nav}</nav>
            <main>
            {alerts}
            {body}
            </main>
            <script src="/console.js"></script>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <summary>A POST form with the antiforgery token. Only same-origin pages can read the token.</summary>
    public static string Form(HttpContext context, string action, string content, string? cssClass = null)
    {
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        return $"""
            <form method="post" action="{Attr(action)}"{(cssClass is null ? "" : $" class=\"{cssClass}\"")}>
            <input type="hidden" name="{Attr(tokens.FormFieldName)}" value="{Attr(tokens.RequestToken)}">
            {content}
            </form>
            """;
    }

    public static string HealthClass(Health health) => health switch
    {
        Health.Healthy => "good",
        Health.Degraded => "warn",
        _ => "bad",
    };

    public static string HealthLabel(Health health) => health switch
    {
        Health.Healthy => "Healthy",
        Health.Degraded => "Warning",
        _ => "Unhealthy",
    };

    public static string Ok(bool? ok) => ok switch
    {
        true => """<span class="pill good">OK</span>""",
        false => """<span class="pill bad">Fail</span>""",
        null => """<span class="pill muted">—</span>""",
    };

    public static string Card(string title, string content, string? cssClass = null) => $"""
        <section class="card{(cssClass is null ? "" : " " + cssClass)}">
        <h3>{E(title)}</h3>
        {content}
        </section>
        """;

    public static string Redirect(string path, string? error = null, string? ok = null)
    {
        var query = error is not null ? $"?error={Uri.EscapeDataString(error)}" : ok is not null ? $"?ok={Uri.EscapeDataString(ok)}" : "";
        return path + query;
    }

    public static string Checks(IEnumerable<CheckResult> checks)
    {
        var rows = string.Concat(checks.Select(c => $"<tr><td>{Ok(c.Ok)}</td><td>{E(c.Name)}</td><td>{E(c.Detail)}</td></tr>"));
        return $"""<table><thead><tr><th></th><th>Check</th><th>Result</th></tr></thead><tbody>{rows}</tbody></table>""";
    }

    /// <summary>A small inline SVG line of values, oldest first. Used for the drill RTO trend.</summary>
    public static string Sparkline(IReadOnlyList<double> values, string label)
    {
        if (values.Count < 2)
        {
            return "";
        }

        const int width = 320;
        const int height = 60;
        var max = Math.Max(values.Max(), 1);
        var points = string.Join(' ', values.Select((v, i) =>
            FormattableString.Invariant($"{i * (width - 8) / (double)(values.Count - 1) + 4:0.#},{height - 4 - v / max * (height - 8):0.#}")));
        return $"""
            <svg class="spark" viewBox="0 0 {width} {height}" role="img" aria-label="{Attr(label)}">
            <polyline points="{points}" />
            </svg>
            """;
    }

    public const string Favicon = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="7" fill="#3538cd"/><path d="M9 10h14v4H9zm0 6h14v6H9z" fill="#fff"/></svg>
        """;

    public const string Css = """
        :root { --bg:#f6f7f9; --card:#fff; --text:#1f2933; --muted:#667085; --line:#e4e7ec;
          --good:#067647; --good-bg:#dcfae6; --warn:#93370d; --warn-bg:#fef0c7; --bad:#b42318; --bad-bg:#fee4e2; --accent:#3538cd; }
        @media (prefers-color-scheme: dark) { :root { --bg:#0f1115; --card:#181b21; --text:#e6e8eb; --muted:#98a2b3; --line:#2a2f37;
          --good:#75e0a7; --good-bg:#053321; --warn:#fec84b; --warn-bg:#4e1d09; --bad:#fda29b; --bad-bg:#55160c; --accent:#a4bcfd; } }
        * { box-sizing:border-box; }
        body { margin:0; font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif; background:var(--bg); color:var(--text); }
        .top { display:flex; flex-wrap:wrap; gap:12px; align-items:center; padding:12px 16px; background:var(--card); border-bottom:1px solid var(--line); }
        .brand { font-weight:600; margin-right:auto; }
        .nav { display:flex; flex-wrap:wrap; gap:4px; padding:8px 16px; background:var(--card); border-bottom:1px solid var(--line); }
        .nav a { padding:6px 10px; border-radius:6px; color:var(--muted); text-decoration:none; }
        .nav a.active, .nav a:hover { background:var(--bg); color:var(--text); }
        main { max-width:1100px; margin:0 auto; padding:16px; }
        h1 { font-size:22px; margin:8px 0 16px; } h2 { font-size:18px; margin:24px 0 8px; } h3 { font-size:14px; margin:0 0 8px; color:var(--muted); font-weight:600; }
        .big { font-size:32px; font-weight:700; margin:0; } .muted { color:var(--muted); } .small { font-size:13px; }
        .grid { display:grid; gap:12px; grid-template-columns:repeat(auto-fill,minmax(240px,1fr)); }
        .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px; }
        .card.wide { grid-column:1/-1; }
        .hero { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px; margin-bottom:12px; }
        .hero.good { border-left:6px solid var(--good); } .hero.warn { border-left:6px solid var(--warn); } .hero.bad { border-left:6px solid var(--bad); }
        .pill { display:inline-block; padding:1px 8px; border-radius:999px; font-size:12px; font-weight:600; }
        .pill.good { background:var(--good-bg); color:var(--good); } .pill.warn { background:var(--warn-bg); color:var(--warn); }
        .pill.bad { background:var(--bad-bg); color:var(--bad); } .pill.muted { background:var(--bg); color:var(--muted); }
        .alert { padding:10px 12px; border-radius:8px; margin-bottom:12px; } .alert.bad { background:var(--bad-bg); color:var(--bad); }
        .alert.good { background:var(--good-bg); color:var(--good); } .alert.warn { background:var(--warn-bg); color:var(--warn); }
        ul.issues { margin:0; padding-left:20px; }
        .table-wrap { overflow-x:auto; } table { width:100%; border-collapse:collapse; background:var(--card); font-size:14px; }
        th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
        th { color:var(--muted); font-weight:600; font-size:12px; text-transform:uppercase; }
        .mid { font-size:20px; font-weight:700; margin:0; }
        meter { width:100%; height:12px; margin-top:4px; }
        form { margin:0; } form.stack { display:grid; gap:10px; max-width:560px; }
        label { display:block; font-weight:500; } input, select, textarea { font:inherit; padding:7px 9px; border:1px solid var(--line); border-radius:6px; background:var(--card); color:var(--text); width:100%; }
        input[type=radio], input[type=checkbox] { width:auto; margin-right:6px; }
        .choice { display:flex; align-items:flex-start; gap:4px; font-weight:400; }
        button { font:inherit; padding:8px 14px; border-radius:6px; border:1px solid var(--line); background:var(--card); color:var(--text); cursor:pointer; }
        button.primary { background:var(--accent); border-color:var(--accent); color:#fff; } button.danger { background:var(--bad); border-color:var(--bad); color:#fff; }
        button:disabled { opacity:.5; cursor:not-allowed; }
        .actions { display:flex; flex-wrap:wrap; gap:8px; align-items:center; }
        pre.log { background:#0b0d10; color:#d0d5dd; padding:12px; border-radius:8px; max-height:60vh; overflow:auto; font-size:12px; white-space:pre-wrap; word-break:break-word; }
        code, .mono { font-family:ui-monospace,SFMono-Regular,Menlo,monospace; font-size:13px; }
        .steps li { margin-bottom:6px; } .spark { width:100%; max-width:320px; height:60px; } .spark polyline { fill:none; stroke:var(--accent); stroke-width:2; }
        details summary { cursor:pointer; color:var(--accent); }
        """;

    public const string Script = """
        // Live log for a running job: polls the agent and appends new lines.
        (function () {
          var log = document.getElementById('log');
          if (!log) return;
          var id = log.getAttribute('data-job');
          var next = 0;
          var state = document.getElementById('job-state');
          function poll() {
            fetch('/job/' + id + '/log?after=' + next, { headers: { 'Accept': 'application/json' } })
              .then(function (r) { return r.json(); })
              .then(function (data) {
                if (data.lines.length) {
                  var atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 20;
                  log.textContent += data.lines.join('\n') + '\n';
                  if (atBottom) log.scrollTop = log.scrollHeight;
                }
                next = data.next;
                if (data.done) {
                  state.textContent = data.ok ? 'Done' : 'Failed';
                  state.className = 'pill ' + (data.ok ? 'good' : 'bad');
                  var summary = document.getElementById('job-summary');
                  summary.textContent = data.summary || '';
                  summary.className = 'alert ' + (data.ok ? 'good' : 'bad');
                } else {
                  setTimeout(poll, 1000);
                }
              })
              .catch(function () { setTimeout(poll, 3000); });
          }
          poll();
        })();
        // Restore form: show only the inputs for the chosen kind of target.
        (function () {
          var radios = document.querySelectorAll('input[name=mode]');
          function update() {
            radios.forEach(function (r) {
              var panel = document.getElementById('mode-' + r.value);
              if (panel) panel.hidden = !r.checked;
            });
          }
          radios.forEach(function (r) { r.addEventListener('change', update); });
          update();
        })();
        """;
}
