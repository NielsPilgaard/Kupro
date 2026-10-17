"""Generates the Flowstate Grafana dashboards (grafana/dashboards) and alert rules (grafana/provisioning/alerting).

Everything here is generated; edit this script, not the JSON. Run:

    python infrastructure/observability/build_dashboards.py

Grafana provisions both from these files at startup (see README.md). Standard library only.

Request numbers come from the API's request log lines in Loki, one per request (src/Flowstate.Api/Common/
RequestLogging.cs): Method, Path, Route (template), Caller, StatusCode, Duration, plus RequestId from the log scope.
tests/Flowstate.Tests/Features/RequestLoggingTests.cs pins that shape, and the queries were checked against a local
Loki fed by the API's own OTLP export. Rename a field there and panels here go blank.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "grafana", "dashboards")
ALERTS = os.path.join(HERE, "grafana", "provisioning", "alerting", "alert-rules.json")
FOLDER = "Flowstate"
# Uids fixed in grafana/provisioning/datasources/datasources.yaml.
PROM = {"type": "prometheus", "uid": "prometheus"}
LOKI = {"type": "loki", "uid": "loki"}
TEMPO = {"type": "tempo", "uid": "tempo"}

# service.namespace comes from ServiceDefaults, service.name from OTEL_SERVICE_NAME (Aspire resource name).
# Prometheus' OTLP receiver turns the pair into the job "flowstate/api".
JOB = 'job="flowstate/api"'
API = '{service_namespace="flowstate", service_name="api"}'
ALL = '{service_namespace="flowstate"}'  # api + migrations
# Health probes: Docker's compose healthcheck calls /alive every 10 s; under `aspire run` Aspire probes /health.
PROBES = 'http_route=~"/alive|/health"'
TRACE_SVC = 'resource.service.namespace="flowstate" && resource.service.name="api"'

# ADR-010 latency budget, server-side.
P50_TARGET_MS = 100
P99_BUDGET_MS = 500

_id = 0


def nid():
    global _id
    _id += 1
    return _id


# ------------------------------------------------------------------ query and panel helpers

def prom(expr, legend="", ref="A", instant=False):
    t = {"datasource": PROM, "refId": ref, "expr": expr, "legendFormat": legend}
    t.update({"instant": True, "range": False} if instant else {"range": True})
    return t


def loki(expr, ref="A", legend="", qtype="range"):
    return {"datasource": LOKI, "refId": ref, "expr": expr, "legendFormat": legend, "queryType": qtype}


def tempo(query, ref="A", limit=50):
    # tableType "spans": one row per matched span with the select()ed attributes as columns and a trace link.
    return {"datasource": TEMPO, "refId": ref, "queryType": "traceql", "query": query,
            "limit": limit, "spss": 3, "tableType": "spans"}


def row(title, y):
    return {"type": "row", "id": nid(), "title": title, "collapsed": False,
            "gridPos": {"h": 1, "w": 24, "x": 0, "y": y}, "panels": []}


def stat(title, targets, x, y, w=4, h=4, unit="short", thresholds=None, desc="",
         decimals=None, mappings=None, color_mode="value", no_value="0"):
    p = {
        "type": "stat", "id": nid(), "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": targets[0]["datasource"], "targets": targets,
        "fieldConfig": {"defaults": {
            "unit": unit, "noValue": no_value,
            "thresholds": {"mode": "absolute", "steps": thresholds or [{"color": "green", "value": None}]},
            "mappings": mappings or [],
            "color": {"mode": "thresholds"},
        }, "overrides": []},
        "options": {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                    "colorMode": color_mode, "graphMode": "none", "textMode": "auto",
                    "justifyMode": "center", "orientation": "auto"},
    }
    if decimals is not None:
        p["fieldConfig"]["defaults"]["decimals"] = decimals
    return p


def ts(title, targets, x, y, w=12, h=8, unit="short", desc="", stack=False, overrides=None,
       draw="line", fill=10, min0=True, threshold_lines=None):
    custom = {"drawStyle": draw, "lineWidth": 1, "fillOpacity": fill, "showPoints": "never",
              "spanNulls": False, "stacking": {"mode": "normal" if stack else "none", "group": "A"}}
    if draw == "bars":
        custom["fillOpacity"] = 80
    if draw == "points":
        custom["showPoints"] = "always"
        custom["pointSize"] = 6
    d = {"unit": unit, "custom": custom, "color": {"mode": "palette-classic"}}
    if min0:
        d["min"] = 0
    if threshold_lines:
        custom["thresholdsStyle"] = {"mode": "dashed"}
        d["thresholds"] = {"mode": "absolute", "steps": [{"color": "transparent", "value": None}] + [
            {"color": c, "value": v} for v, c in threshold_lines]}
    return {
        "type": "timeseries", "id": nid(), "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": targets[0]["datasource"], "targets": targets,
        "fieldConfig": {"defaults": d, "overrides": overrides or []},
        "options": {"legend": {"displayMode": "table", "placement": "right", "calcs": ["mean", "max"], "showLegend": True},
                    "tooltip": {"mode": "multi", "sort": "desc"}},
    }


def fixed_colors(colors):
    return [{"matcher": {"id": "byName", "options": k},
             "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": v}}]} for k, v in colors.items()]


STATUS_COLORS = {"2xx": "green", "3xx": "blue", "4xx": "orange", "5xx": "red"}
CALLER_COLORS = {"session": "blue", "token": "purple", "agent": "orange", "anonymous": "text"}


def logs_panel(title, expr, x, y, w=24, h=12, desc=""):
    return {"type": "logs", "id": nid(), "title": title, "description": desc,
            "gridPos": {"h": h, "w": w, "x": x, "y": y}, "datasource": LOKI,
            "targets": [loki(expr)],
            "options": {"showTime": True, "wrapLogMessage": True, "enableLogDetails": True,
                        "sortOrder": "Descending", "dedupStrategy": "none", "prettifyLogMessage": False}}


def table(title, targets, x, y, w=24, h=10, desc="", transformations=None, overrides=None, sort=None):
    opts = {"showHeader": True, "cellHeight": "sm", "footer": {"show": False}}
    if sort:
        opts["sortBy"] = [{"displayName": sort, "desc": True}]
    return {"type": "table", "id": nid(), "title": title, "description": desc,
            "gridPos": {"h": h, "w": w, "x": x, "y": y},
            "datasource": targets[0]["datasource"], "targets": targets,
            "transformations": transformations or [],
            "fieldConfig": {"defaults": {"custom": {"align": "auto", "filterable": True}},
                            "overrides": overrides or []},
            "options": opts}


STARTED = API + ' | scope_name="Microsoft.Hosting.Lifetime" |= "Application started"'

ANNOTATIONS = {"list": [
    {"builtIn": 1, "datasource": {"type": "grafana", "uid": "-- Grafana --"}, "enable": True, "hide": True,
     "iconColor": "rgba(0, 211, 255, 1)", "name": "Annotations & Alerts", "type": "dashboard"},
    {"datasource": LOKI, "enable": True, "iconColor": "purple", "name": "Deploys / restarts", "expr": STARTED,
     "titleFormat": "API started", "textFormat": "{{service_version}} on {{service_instance_id}}", "instant": False},
]}

LINKS = [{"title": "Flowstate", "type": "dashboards", "tags": ["flowstate"],
          "asDropdown": False, "includeVars": False, "keepTime": True}]


def dashboard(uid, title, desc, panels, time_from="now-24h", refresh="1m"):
    return {"uid": uid, "title": title, "description": desc,
            "tags": ["flowstate", "aspnetcore"], "timezone": "Europe/Copenhagen",
            "editable": True, "graphTooltip": 1, "refresh": refresh, "schemaVersion": 39,
            "time": {"from": time_from, "to": "now"}, "links": LINKS,
            "annotations": ANNOTATIONS, "templating": {"list": []}, "panels": panels}


# ------------------------------------------------------------------ log streams

# Filter on scope and our own Route field, not event_name: not every Loki version maps the OTLP event name.
REQLOG = 'scope_name="Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"'
REQ = API + f' | {REQLOG} | Route!=""'
# Unmatched = no endpoint: scanners probing /wp-login.php, /.env… They get 401 (the fallback policy requires
# auth), so they are filtered by route, not by status.
UNMATCHED = ' | Route="(unmatched)"'
REAL = REQ + ' | Route!="(unmatched)"'
STATUS = 'label_format status=`{{ substr 0 1 .StatusCode }}xx`'
ENDPOINT = 'label_format endpoint=`{{ .Method }} {{ .Route }}`'
# Feature area = first route segment after /api/v1/ and the workspace prefix:
# /api/v1/workspaces/{workspace}/members/… → members, /api/v1/tokens → tokens, /mcp → mcp.
AREA = ('label_format area=`{{ regexReplaceAll "[/{].*" (trimPrefix "workspaces/{workspace}/" '
        '(trimPrefix "/" (trimPrefix "/api/v1" .Route))) "" }}`')
EXC = API + ' | scope_name="Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"'
# OTel severity_number (Error = 17+, Warning = 13-16) rather than detected_level, whose casing differs between
# Loki versions ("error" vs "Error"). severity_text is the .NET level name: Information, Warning, Error, Critical.
ERRLVL = 'severity_number >= 17'
WARNLVL = 'severity_number >= 13 and severity_number < 17'
NOT_REQLOG = 'scope_name!="Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"'

# Warm vs cold. A fresh instance has no connections yet, so every request of the cold-start burst after a deploy
# (OIDC discovery, EF model build, JIT) is the first on its connection: RequestId "<ConnectionId>:00000001".
# Warm = a reused connection. Browsers also open new connections on a warm instance, so "new connection" is cold
# starts plus some fast requests; fine for both uses. Budget numbers use warm requests only.
WARM = ' | RequestId!~".*:00000001"'
CONN = ('label_format conn=`{{ if hasSuffix ":00000001" .RequestId }}new connection (cold starts land here)'
        '{{ else }}reused connection (warm){{ end }}`')


def cnt(stream, rng="$__interval", by=None, extra=""):
    inner = f"count_over_time({stream}{extra} [{rng}])"
    return f"sum by ({', '.join(by)}) ({inner})" if by else f"sum({inner})"


def gated_quantile(q, stream, rng, by, min_n, sentinel=False):
    """Quantile only where at least min_n requests back it; below that a p99 is just the slowest request.
    With sentinel=True, groups with too few requests return -1, which stat panels map to "too few"."""
    quant = f"quantile_over_time({q}, {stream} | unwrap Duration [{rng}]) by ({by})"
    n = f"sum by ({by}) (count_over_time({stream} [{rng}]))"
    expr = f"{quant} and on ({by}) ({n} >= {min_n})"
    return f"({expr}) or on ({by}) ({n} < {min_n}) * 0 - 1" if sentinel else expr


def ms_thresholds(orange, red):
    return [{"color": "green", "value": None}, {"color": "orange", "value": orange}, {"color": "red", "value": red}]


TOO_FEW = [{"type": "range", "options": {"from": -1.5, "to": -0.5, "result": {"text": "too few", "color": "text"}}}]


def warm_quantile_stat(title, q, min_n, thresholds, x, y, w, desc):
    stream = REAL + WARM
    p = stat(title, [
        loki(gated_quantile(q, stream, "$__range", "service_name", min_n, sentinel=True), "A", qtype="instant"),
        loki(cnt(stream, "$__range"), "B", qtype="instant"),
    ], x, y, w=w, unit="ms", decimals=0, thresholds=thresholds, no_value="no requests", mappings=TOO_FEW, desc=desc)
    p["options"]["textMode"] = "value_and_name"
    p["fieldConfig"]["overrides"] = [
        {"matcher": {"id": "byFrameRefID", "options": "A"}, "properties": [{"id": "displayName", "value": q_name(q)}]},
        {"matcher": {"id": "byFrameRefID", "options": "B"}, "properties": [
            {"id": "displayName", "value": "warm requests"}, {"id": "unit", "value": "short"},
            {"id": "color", "value": {"mode": "fixed", "fixedColor": "text"}}]},
    ]
    return p


def q_name(q):
    return f"p{round(q * 100)}"


# ================================================================ Service health
P = []
y = 0
P.append(row("At a glance (selected range; health probes are never logged)", y)); y += 1
alive = stat("API alive", [prom(f'sum(rate(http_server_request_duration_seconds_count{{{JOB}, {PROBES}}}[5m])) > bool 0', instant=True)],
             0, y, w=3, desc="A health probe was answered in the last 5 minutes (compose healthcheck on /alive, every 10 s).",
             mappings=[{"type": "value", "options": {"1": {"text": "UP", "color": "green"}, "0": {"text": "DOWN", "color": "red"}}}],
             thresholds=[{"color": "red", "value": None}, {"color": "green", "value": 1}], no_value="DOWN", color_mode="background")
P += [
    alive,
    stat("Requests", [loki(cnt(REAL, "$__range"), qtype="instant")], 3, y, w=3, decimals=0,
         desc="Requests to a real endpoint. Scanner noise (unmatched paths) is left out."),
    stat("5xx error rate", [loki(f'({cnt(REAL, "$__range", extra=" | StatusCode=~`5..`")} or vector(0)) / {cnt(REAL, "$__range")}', qtype="instant")],
         6, y, w=3, unit="percentunit", decimals=2,
         thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 0.001}, {"color": "red", "value": 0.01}]),
    warm_quantile_stat("p50, warm", 0.5, 20, ms_thresholds(P50_TARGET_MS, P99_BUDGET_MS), 9, y, 4,
                       f"Median of warm requests. ADR-010 target: < {P50_TARGET_MS} ms. Needs 20+ warm requests."),
    warm_quantile_stat("p99, warm", 0.99, 100, ms_thresholds(P99_BUDGET_MS * 0.6, P99_BUDGET_MS), 13, y, 4,
                       f"99th percentile of warm requests. ADR-010 budget: < {P99_BUDGET_MS} ms. Needs 100+ warm requests; "
                       "below that a p99 is just the single slowest request."),
    stat(f"Over budget (> {P99_BUDGET_MS} ms)", [loki(f'{cnt(REAL + WARM, "$__range", extra=f" | Duration > {P99_BUDGET_MS}")} or vector(0)', qtype="instant")],
         17, y, w=3, decimals=0, thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}],
         desc="Warm requests slower than the p99 budget. Find them in 'Requests over budget' below."),
    stat("Unhandled exceptions", [loki(cnt(EXC, "$__range"), qtype="instant")], 20, y, w=2, decimals=0,
         thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}]),
    stat("Deploys / restarts", [loki(cnt(STARTED, "$__range"), qtype="instant")], 22, y, w=2, decimals=0,
         color_mode="none", desc="'Application started' log lines."),
]
y += 4

P.append(row("Traffic", y)); y += 1
P += [
    ts("Requests by status class", [loki(f"sum by (status) (count_over_time({REAL} | {STATUS} [$__interval]))", legend="{{status}}")],
       0, y, w=8, stack=True, overrides=fixed_colors(STATUS_COLORS), draw="bars"),
    ts("Requests by caller", [loki(f"sum by (Caller) (count_over_time({REAL} [$__interval]))", legend="{{Caller}}")],
       8, y, w=8, stack=True, overrides=fixed_colors(CALLER_COLORS), draw="bars",
       desc="session = web app or MCP client signed in via Keycloak; token = personal API token (CLI, scripts); "
            "agent = agent token. Agent parity (ADR-001) in one graph."),
    ts("5xx by endpoint", [loki(f'sum by (endpoint) (count_over_time({REAL} | StatusCode=~"5.." | {ENDPOINT} [$__interval]))', legend="{{endpoint}}")],
       16, y, w=8, draw="bars", stack=True, desc="Server errors. Details and stack traces on the Errors & logs dashboard."),
]
y += 8
P += [
    ts("Auth failures (401 / 403) by endpoint", [loki(f'sum by (endpoint, StatusCode) (count_over_time({REAL} | StatusCode=~"401|403" | {ENDPOINT} [$__interval]))', legend="{{StatusCode}} {{endpoint}}")],
       0, y, w=12, draw="bars", stack=True,
       desc="Spikes after a deploy usually mean a policy change or a Keycloak problem. Expired agent tokens show up as 401s by caller 'anonymous'."),
    ts("Scanner noise (unmatched paths)", [loki(f"sum(count_over_time({REQ}{UNMATCHED} [$__interval]))", legend="requests")],
       12, y, w=12, draw="bars", desc="Requests that matched no endpoint: bots probing /wp-login.php, /.env… A rise right after "
                                         "a frontend deploy = the SPA calls a path that doesn't exist."),
]
y += 8

P.append(row(f"Latency vs budget (ADR-010: p50 < {P50_TARGET_MS} ms, p99 < {P99_BUDGET_MS} ms)", y)); y += 1
latency = ts("Slowest request per time bucket, and warm percentiles", [
    loki(f"max_over_time({REAL} | {CONN} | unwrap Duration [$__interval]) by (conn)", "A", "slowest, {{conn}}"),
    loki(gated_quantile(0.5, REAL + WARM, "$__interval", "service_name", 20), "B", "p50 warm (buckets with 20+ requests)"),
    loki(gated_quantile(0.99, REAL + WARM, "$__interval", "service_name", 100), "C", "p99 warm (buckets with 100+ requests)"),
    loki(cnt(REAL, "$__interval"), "D", "requests"),
], 0, y, w=24, h=9, unit="ms", draw="points", threshold_lines=[(P50_TARGET_MS, "orange"), (P99_BUDGET_MS, "red")],
    desc="Each point is the slowest request in its bucket, split by connection. Dashed lines are the ADR-010 target "
         "and budget. A purple dot at a deploy marker is a cold start; a blue dot above the red line is a warm request "
         "over budget: find it in 'Requests over budget' below. Percentile lines only appear in buckets with enough "
         "requests; grey bars (right axis) are the request count.",
    overrides=[
        {"matcher": {"id": "byRegexp", "options": ".*new connection.*"},
         "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": "purple"}}]},
        {"matcher": {"id": "byRegexp", "options": ".*reused connection.*"},
         "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": "blue"}}]},
        {"matcher": {"id": "byFrameRefID", "options": "B"},
         "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": "green"}},
                        {"id": "custom.drawStyle", "value": "line"}, {"id": "custom.lineWidth", "value": 2}]},
        {"matcher": {"id": "byFrameRefID", "options": "C"},
         "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": "orange"}},
                        {"id": "custom.drawStyle", "value": "line"}, {"id": "custom.lineWidth", "value": 2}]},
        {"matcher": {"id": "byFrameRefID", "options": "D"},
         "properties": [{"id": "unit", "value": "short"}, {"id": "decimals", "value": 0},
                        {"id": "custom.axisPlacement", "value": "right"}, {"id": "custom.axisLabel", "value": "requests"},
                        {"id": "custom.drawStyle", "value": "bars"}, {"id": "custom.fillOpacity", "value": 25},
                        {"id": "custom.showPoints", "value": "never"},
                        {"id": "custom.thresholdsStyle", "value": {"mode": "off"}},
                        {"id": "color", "value": {"mode": "fixed", "fixedColor": "#8080804d"}}]},
    ])
P.append(latency)
y += 9


def status_cell():
    return [{"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": {"mode": "absolute", "steps": [
                {"color": "green", "value": None}, {"color": "orange", "value": 400}, {"color": "red", "value": 500}]}}]


def duration_cell():
    # Tempo reports span duration in nanoseconds.
    return [{"id": "unit", "value": "ns"},
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": {"mode": "absolute", "steps": [
                {"color": "green", "value": None}, {"color": "orange", "value": P50_TARGET_MS * 1_000_000},
                {"color": "red", "value": P99_BUDGET_MS * 1_000_000}]}}]


def trace_table(title, query, x, y, desc, renames, hide, extra_overrides, order):
    # The Tempo "spans" frame has helper columns (traceIdHidden feeds the Span ID link, so it is hidden, not
    # removed). "Trace Name" is the root span name, i.e. the API request, e.g. "GET api/v1/tokens/{id:guid}".
    hidden = "|".join(["traceIdHidden", "Spans traceIdHidden", "Trace Service", "kind", "service\\.name", "url\\.path"] + hide)
    return table(title, [tempo(query)], x, y, w=12, h=10, sort="Duration", desc=desc,
                 transformations=[{"id": "organize", "options": {
                     "renameByName": {"Trace Name": "Request", **renames},
                     "indexByName": {name: i for i, name in enumerate(order)}}}],
                 overrides=[{"matcher": {"id": "byRegexp", "options": f"^({hidden})$"},
                             "properties": [{"id": "custom.hidden", "value": True}]},
                            {"matcher": {"id": "byName", "options": "Span ID"}, "properties": [{"id": "custom.width", "value": 150}]},
                            {"matcher": {"id": "byName", "options": "Start time"}, "properties": [{"id": "custom.width", "value": 160}]},
                            {"matcher": {"id": "byName", "options": "Duration"}, "properties": duration_cell()}] + extra_overrides)


over_budget_traces = trace_table(
    f"Requests over budget (> {P99_BUDGET_MS} ms)",
    f'{{{TRACE_SVC} && kind=server && duration > {P99_BUDGET_MS}ms}}'
    ' | select(span.http.response.status_code, span.flowstate.caller, resource.service.instance.id, resource.service.version)',
    0, y,
    "From Tempo. Click the span ID to see where the time went. Several in the same second on one instance = cold "
    "start after a deploy. 5xx = error path, not slowness. Tempo search covers at most 30 days.",
    {"http.response.status_code": "Status", "flowstate.caller": "Caller", "service.instance.id": "Instance",
     "service.version": "Version"},
    ["Name"],
    [{"matcher": {"id": "byName", "options": "Status"}, "properties": status_cell()}],
    ["Trace Name", "Duration", "http.response.status_code", "flowstate.caller", "Start time", "Span ID",
     "service.instance.id", "service.version"])
P += [
    over_budget_traces,
    trace_table(
        "Slow calls inside requests (over 50 ms)",
        f'{{{TRACE_SVC} && kind=server}} >> {{kind=client && duration > 50ms}}'
        ' | select(name, span.server.address, span.url.full, span.db.system.name, span.db.system, span.db.query.text, span.db.statement)',
        12, y,
        "Outgoing HTTP and database calls made while serving a request: the usual causes. Keycloak "
        ".well-known/openid-configuration or certs = key discovery on a cold instance; db spans = slow SQL "
        "(look for a missing index or an N+1, ADR-010).",
        {"Name": "Call", "server.address": "Host", "url.full": "URL", "db.system.name": "DB", "db.system": "DB (old)",
         "db.query.text": "SQL", "db.statement": "Statement"},
        [], [],
        ["Trace Name", "Duration", "server.address", "Start time", "Span ID", "Name", "url.full",
         "db.system.name", "db.system", "db.query.text", "db.statement"]),
]
y += 10

P.append(row("Endpoints", y)); y += 1
P.append(table("Endpoints (selected range)", [
    loki(f"sum by (endpoint) (count_over_time({REAL} | {ENDPOINT} [$__range]))", "A", qtype="instant"),
    loki(f'sum by (endpoint) (count_over_time({REAL} | StatusCode=~"4.." | {ENDPOINT} [$__range]))', "B", qtype="instant"),
    loki(f'sum by (endpoint) (count_over_time({REAL} | StatusCode=~"5.." | {ENDPOINT} [$__range]))', "C", qtype="instant"),
    loki(f"quantile_over_time(0.5, {REAL}{WARM} | {ENDPOINT} | unwrap Duration [$__range]) by (endpoint)", "D", qtype="instant"),
    loki(gated_quantile(0.99, f"{REAL}{WARM} | {ENDPOINT}", "$__range", "endpoint", 100), "E", qtype="instant"),
    loki(f"max_over_time({REAL} | {ENDPOINT} | unwrap Duration [$__range]) by (endpoint)", "F", qtype="instant"),
    loki(f"sum by (endpoint) (count_over_time({REAL}{WARM} | Duration > {P99_BUDGET_MS} | {ENDPOINT} [$__range]))", "G", qtype="instant"),
], 0, y, h=12, sort="Requests",
    desc=f"Grouped by route template. Median and p99 use warm requests; p99 is blank below 100 requests. A high Max "
         f"with a low Median is usually one cold start. 'Over budget' = warm requests above {P99_BUDGET_MS} ms.",
    transformations=[
        {"id": "merge", "options": {}},
        {"id": "organize", "options": {"excludeByName": {"Time": True},
                                       "renameByName": {"endpoint": "Endpoint", "Value #A": "Requests", "Value #B": "4xx",
                                                        "Value #C": "5xx", "Value #D": "p50 warm", "Value #E": "p99 warm (100+ req)",
                                                        "Value #F": "Max", "Value #G": "Over budget"}}},
    ],
    overrides=[
        {"matcher": {"id": "byName", "options": "Endpoint"}, "properties": [{"id": "custom.width", "value": 480}]},
        {"matcher": {"id": "byRegexp", "options": "5xx|Over budget"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": {"mode": "absolute", "steps": [{"color": "green", "value": None}, {"color": "red", "value": 0.5}]}}]},
        {"matcher": {"id": "byRegexp", "options": "p50.*|p99.*|Max"}, "properties": [
            {"id": "unit", "value": "ms"}, {"id": "decimals", "value": 0}, {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": {"mode": "absolute", "steps": ms_thresholds(P50_TARGET_MS, P99_BUDGET_MS)}}]},
    ]))
y += 12

P.append(row("Runtime and database", y)); y += 1
P += [
    ts("Memory", [
        prom(f"sum(dotnet_process_memory_working_set_bytes{{{JOB}}})", "working set", "A"),
        prom(f"sum(dotnet_gc_last_collection_memory_committed_size_bytes{{{JOB}}})", "GC committed", "B"),
    ], 0, y, w=8, unit="bytes", fill=0),
    ts("In-flight work", [
        prom(f"sum(http_server_active_requests{{{JOB}}})", "active requests", "A"),
        prom(f"sum(kestrel_active_connections{{{JOB}}})", "kestrel connections", "B"),
        prom(f"sum(kestrel_queued_connections{{{JOB}}})", "queued connections", "C"),
        prom(f"sum(microsoft_entityframeworkcore_active_dbcontexts{{{JOB}}})", "active DbContexts", "D"),
    ], 8, y, w=8, fill=0, desc="Queued connections > 0, or DbContexts climbing without traffic = thread pool or DB trouble."),
    # Npgsql has renamed its pool metrics across versions; the regex matches both names.
    ts("Postgres connection pool", [
        prom(f'sum by (state) ({{__name__=~"db_client_connections?_(usage|count)", {JOB}}})', "{{state}}", "A"),
        prom(f'max({{__name__=~"db_client_connections?_max", {JOB}}})', "max", "B"),
    ], 16, y, w=8, fill=0, desc="Used connections near max = pool exhaustion: requests queue for a connection.",
        overrides=[{"matcher": {"id": "byName", "options": "max"}, "properties": [
            {"id": "color", "value": {"mode": "fixed", "fixedColor": "red"}}, {"id": "custom.lineStyle", "value": {"fill": "dash"}}]}]),
]
y += 8

# Scraped by Alloy and remote-written to Prometheus (alloy/flowstate.alloy). "Host" is read from inside the Alloy
# container: CPU, memory and load are the host's; disk is the filesystem Docker's volumes live on.
DISK_USED = 'max(1 - node_filesystem_avail_bytes{job="integrations/unix", mountpoint="/"} / node_filesystem_size_bytes{job="integrations/unix", mountpoint="/"})'
MEM_USED = 'max(1 - node_memory_MemAvailable_bytes{job="integrations/unix"} / node_memory_MemTotal_bytes{job="integrations/unix"})'
CPU_USED = 'avg(1 - rate(node_cpu_seconds_total{job="integrations/unix", mode="idle"}[$__rate_interval]))'
UP_MAP = [{"type": "value", "options": {"1": {"text": "UP", "color": "green"}, "0": {"text": "DOWN", "color": "red"}}}]
UP_THRESHOLDS = [{"color": "red", "value": None}, {"color": "green", "value": 1}]
PCT_THRESHOLDS = [{"color": "green", "value": None}, {"color": "orange", "value": 0.75}, {"color": "red", "value": 0.85}]

P.append(row("Host and dependencies", y)); y += 1
disk = stat("Disk used", [prom(DISK_USED, instant=True)], 0, y, w=4, unit="percentunit", decimals=0,
            thresholds=PCT_THRESHOLDS, no_value="–",
            desc="Docker's data disk: Postgres, Keycloak and telemetry volumes, images, logs. Alert at 85 %.")
pg_up = stat("Postgres", [prom('max(pg_up{job="integrations/postgres"})', instant=True)], 8, y, w=4,
             mappings=UP_MAP, thresholds=UP_THRESHOLDS, no_value="DOWN", color_mode="background",
             desc="Alloy's Postgres exporter can connect and query.")
kc_up = stat("Keycloak", [prom('max(up{job="keycloak"})', instant=True)], 12, y, w=4,
             mappings=UP_MAP, thresholds=UP_THRESHOLDS, no_value="DOWN", color_mode="background",
             desc="Keycloak's metrics endpoint answers. Down = nobody can sign in, and new API tokens can't be validated.")
P += [
    disk,
    stat("Memory used", [prom(MEM_USED, instant=True)], 4, y, w=4, unit="percentunit", decimals=0,
         thresholds=PCT_THRESHOLDS, no_value="–", desc="Host memory in use (excluding reclaimable cache)."),
    pg_up,
    kc_up,
    stat("Database size", [prom('sum(pg_database_size_bytes{job="integrations/postgres", datname="flowstate"})', instant=True)],
         16, y, w=4, unit="bytes", no_value="–"),
    stat("DB connections", [prom('sum(pg_stat_database_numbackends{job="integrations/postgres"}) / max(pg_settings_max_connections{job="integrations/postgres"})', instant=True)],
         20, y, w=4, unit="percentunit", decimals=0, thresholds=PCT_THRESHOLDS, no_value="–",
         desc="Server connections in use, all databases, of max_connections."),
]
y += 4
P += [
    ts("Host CPU, memory and disk", [
        prom(CPU_USED, "CPU", "A"),
        prom(MEM_USED, "memory", "B"),
        prom(DISK_USED, "disk", "C"),
    ], 0, y, unit="percentunit", fill=0, threshold_lines=[(0.85, "red")],
        desc="Whole VPS, all containers. CPU near 100 % with slow requests = the host is the bottleneck."),
    ts("Postgres connections by state", [
        prom('sum by (state) (pg_stat_activity_count{job="integrations/postgres"})', "{{state}}", "A"),
    ], 12, y, fill=0, desc="'idle in transaction' that stays up = a request holding a transaction open."),
]
y += 8

# The backup agent's gauges (infrastructure/backup-agent/AgentMetrics.cs), from its last status (every 5 minutes).
BACKUP = 'job="flowstate/backup-agent"'
P.append(row("Backups (ADR-016; details in the backup console)", y)); y += 1
backup_health = stat("Backups", [prom(f'max(backup_agent_health{{{BACKUP}}})', instant=True)], 0, y, w=4,
                     mappings=[{"type": "value", "options": {"0": {"text": "HEALTHY", "color": "green"},
                                                             "1": {"text": "DEGRADED", "color": "orange"},
                                                             "2": {"text": "UNHEALTHY", "color": "red"}}}],
                     thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}, {"color": "red", "value": 2}],
                     no_value="NO DATA", color_mode="background",
                     desc="The agent's overall health. The issues behind it are on the backup console's Overview "
                          "(restore runbook §1). No data = the agent is down or can't reach Alloy.")
backup_status_age = stat("Status age", [prom(f'max(backup_agent_status_age_seconds{{{BACKUP}}})', instant=True)], 16, y, w=4,
                         unit="s", decimals=0, no_value="–",
                         thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 900}],
                         desc="Time since the agent last built its status. Over 15 minutes = the agent is stuck.")
P += [
    backup_health,
    stat("Data secured", [prom(f'max(backup_agent_data_secured_age_seconds{{{BACKUP}}})', instant=True)], 4, y, w=4,
         unit="s", decimals=0, no_value="–",
         thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 900}],
         desc="How long ago the newest commit known to be in the backup repo happened: the data a lost VPS would cost. "
              "Target (RPO): 15 minutes."),
    stat("Last full backup", [prom(f'max(backup_agent_full_backup_age_seconds{{{BACKUP}}})', instant=True)], 8, y, w=4,
         unit="s", decimals=0, no_value="–",
         thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 26 * 3600}],
         desc="A full backup runs every night at 02:00 (Copenhagen). Over 26 hours = a night was missed."),
    stat("Open issues", [prom(f'max(backup_agent_issues{{{BACKUP}}})', instant=True)], 12, y, w=4, decimals=0, no_value="–",
         thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}],
         desc="Issues in the agent's status, degraded or worse. Read them on the backup console."),
    backup_status_age,
]
y += 4

P.append(row("Recent errors and warnings", y)); y += 1
P.append(logs_panel("Errors and warnings", ALL + f' | severity_number >= 13 | {NOT_REQLOG}', 0, y))

health = dashboard("flowstate-health", "Flowstate – Service health",
                   "Is the API up, fast (ADR-010 budget) and error-free? Request numbers from the request log in Loki.", P)

# ================================================================ Usage
_id = 0
U = []
y = 0
U.append(row("Who uses Flowstate, and through which surface", y)); y += 1
AUTHED = REAL + ' | Caller!="anonymous"'
U += [
    stat("Authenticated requests", [loki(cnt(AUTHED, "$__range"), qtype="instant")], 0, y, w=6, decimals=0),
    stat("Agent + token share", [loki(f'({cnt(AUTHED, "$__range", extra=" | Caller=~`agent|token`")} or vector(0)) / {cnt(AUTHED, "$__range")}', qtype="instant")],
         6, y, w=6, unit="percentunit", decimals=0, color_mode="none",
         desc="Share of authenticated requests made with API tokens (CLI, scripts, agents) instead of a browser/MCP session. "
              "Flowstate is agent-first (ADR-001): this should grow."),
    stat("Feature areas used", [loki(f"count(sum by (area) (count_over_time({AUTHED} | {AREA} [$__range])))", qtype="instant")],
         12, y, w=6, decimals=0, color_mode="none"),
    stat("Scanner requests", [loki(cnt(REQ + UNMATCHED, "$__range"), qtype="instant")], 18, y, w=6, decimals=0,
         color_mode="none", desc="Requests to paths with no endpoint. Not product usage."),
]
y += 4
U += [
    ts("Requests by caller", [loki(f"sum by (Caller) (count_over_time({AUTHED} [$__interval]))", legend="{{Caller}}")],
       0, y, w=12, h=9, draw="bars", stack=True, overrides=fixed_colors(CALLER_COLORS)),
    table("Requests per area and caller (selected range)",
          [loki(f"sum by (area, Caller) (count_over_time({AUTHED} | {AREA} [$__range]))", qtype="instant")],
          12, y, w=12, h=9, sort="Total",
          desc="Area = first route segment after /api/v1/ (and the workspace prefix): issues, projects, tokens, mcp…",
          transformations=[
              {"id": "groupingToMatrix", "options": {"columnField": "Caller", "rowField": "area", "valueField": "Value #A",
                                                     "emptyValue": "zero"}},
              {"id": "calculateField", "options": {"mode": "reduceRow", "reduce": {"reducer": "sum"}, "alias": "Total"}},
              {"id": "organize", "options": {"renameByName": {"area\\Caller": "Area"}}},
          ]),
]
y += 9
U += [
    ts("Requests per area", [loki(f"sum by (area) (count_over_time({AUTHED} | {AREA} [$__interval]))", legend="{{area}}")],
       0, y, w=12, draw="bars", stack=True),
    ts("Errors per area (4xx + 5xx, excl. 401)", [loki(f'sum by (area) (count_over_time({AUTHED} | StatusCode=~"4..|5.." | StatusCode!="401" | {AREA} [$__interval]))', legend="{{area}}")],
       12, y, w=12, draw="bars", stack=True,
       desc="Validation errors (400/422), forbidden (403), conflicts (409) and server errors. Agents retrying a bad "
            "request show up here first."),
]
y += 8
U.append(ts("Slowest request per area", [loki(f"max_over_time({AUTHED} | {AREA} | unwrap Duration [$__interval]) by (area)", legend="{{area}}")],
            0, y, w=24, unit="ms", draw="points", threshold_lines=[(P50_TARGET_MS, "orange"), (P99_BUDGET_MS, "red")],
            desc="Single slowest request per area and bucket. Cold starts sit on the purple deploy markers; the "
                 "Service health dashboard splits cold from warm and links slow requests to traces."))
y += 8
U.append(row("Weekly rhythm", y)); y += 1
rhythm = ts("Requests per hour by caller", [loki(cnt(AUTHED, "1h", by=["Caller"]), legend="{{Caller}}")], 0, y, w=24, h=7,
            draw="bars", stack=True, overrides=fixed_colors(CALLER_COLORS),
            desc="When people and agents work. Useful for picking deploy windows; agents may run at night.")
rhythm["interval"] = "1h"
U.append(rhythm)

usage = dashboard("flowstate-usage", "Flowstate – Usage",
                  "Requests by feature area and caller (web/MCP session, API token, agent token), from the request log in Loki.",
                  U, time_from="now-7d", refresh="5m")

# ================================================================ Errors & logs
_id = 0
E = []
y = 0
E.append(row("Errors", y)); y += 1
E += [
    ts("Log lines by level (excl. request log)", [loki(f"sum by (severity_text) (count_over_time({ALL} | {NOT_REQLOG} [$__interval]))", legend="{{severity_text}}")],
       0, y, w=12, draw="bars", stack=True,
       overrides=fixed_colors({"Critical": "dark-red", "Error": "red", "Warning": "orange", "Information": "green", "Debug": "blue"})),
    ts("Unhandled exceptions by type", [loki(f"sum by (exception_type) (count_over_time({EXC} [$__interval]))", legend="{{exception_type}}")],
       12, y, w=12, draw="bars", stack=True),
]
y += 8
distinct = table("Distinct errors (selected range)", [
    loki(f"sum by (service_name, exception_type, exception_message, scope_name) (count_over_time({ALL} | {ERRLVL} [$__range]))", qtype="instant")],
    0, y, h=9, sort="Count",
    desc="Grouped by exception message, across api and migrations. Postgres 42703 / 42P01 = a migration did not run.",
    transformations=[{"id": "organize", "options": {"excludeByName": {"Time": True},
                                                    "renameByName": {"service_name": "Service", "exception_type": "Type",
                                                                     "exception_message": "Message", "scope_name": "Logger",
                                                                     "Value #A": "Count"}}}])
E.append(distinct)
y += 9
E.append(logs_panel("Error logs (expand a line for stack trace and TraceId)", ALL + f" | {ERRLVL}", 0, y, h=12))
y += 12
E.append(logs_panel("Warnings", ALL + f" | {WARNLVL} | {NOT_REQLOG}", 0, y, h=8))
y += 8

E.append(row("Lifecycle and log volume", y)); y += 1
lifecycle = logs_panel("Startups, shutdowns and migrations",
                       ALL + ' | scope_name=~"Microsoft.Hosting.Lifetime|Flowstate.Api|Flowstate.Api.Data.DatabaseExtensions"',
                       0, y, w=12, h=8,
                       desc="One 'Application started' per deploy or restart, and the migrations run before it.")
E += [
    lifecycle,
    ts("Log volume by logger", [loki(f"sum by (scope_name) (count_over_time({ALL} [$__interval]))", legend="{{scope_name}}")],
       12, y, w=12, h=8, draw="bars", stack=True,
       desc="The request log writes one line per request (health probes excluded). Main driver of Loki ingest."),
]

errors = dashboard("flowstate-errors", "Flowstate – Errors & logs",
                   "Exceptions, error/warning logs, deploys, migrations and log volume for the api and migrations services.", E)


# ================================================================ Alert rules

def q_loki(ref, expr, seconds):
    return {"refId": ref, "datasourceUid": LOKI["uid"], "queryType": "instant",
            "relativeTimeRange": {"from": seconds, "to": 0},
            "model": {"refId": ref, "expr": expr, "queryType": "instant", "intervalMs": 1000, "maxDataPoints": 43200}}


def q_prom(ref, expr, seconds):
    return {"refId": ref, "datasourceUid": PROM["uid"],
            "relativeTimeRange": {"from": seconds, "to": 0},
            "model": {"refId": ref, "expr": expr, "instant": True, "range": False, "intervalMs": 1000, "maxDataPoints": 43200}}


def _expr(ref, model):
    return {"refId": ref, "datasourceUid": "__expr__", "relativeTimeRange": {"from": 0, "to": 0},
            "model": {"refId": ref, "intervalMs": 1000, "maxDataPoints": 43200, **model}}


def q_reduce(ref, source, zero_if_empty=True):
    model = {"type": "reduce", "expression": source, "reducer": "last"}
    if zero_if_empty:
        model["settings"] = {"mode": "replaceNN", "replaceWithValue": 0}
    return _expr(ref, model)


def q_threshold(ref, source, op, value):
    return _expr(ref, {"type": "threshold", "expression": source,
                       "conditions": [{"evaluator": {"type": op, "params": [value]}}]})


def q_math(ref, expression):
    return _expr(ref, {"type": "math", "expression": expression})


def rule(uid, title, condition, data, for_, severity, summary, description, dash, panel,
         no_data="OK", exec_err="Error"):
    return {"uid": uid, "title": title, "condition": condition, "data": data, "for": for_,
            "noDataState": no_data, "execErrState": exec_err, "isPaused": False,
            "labels": {"service": "flowstate", "severity": severity},
            "annotations": {"summary": summary, "description": description,
                            "__dashboardUid__": dash["uid"], "__panelId__": str(panel["id"])}}


WARM_REQ = REAL + WARM
alerts = {"apiVersion": 1, "groups": [{"orgId": 1, "name": "flowstate-api", "folder": FOLDER, "interval": "1m", "rules": [
    rule("flowstate-api-down", "Flowstate API down", "C", [
        q_prom("A", f'sum(rate(http_server_request_duration_seconds_count{{{JOB}, {PROBES}}}[5m]))', 300),
        q_reduce("B", "A", zero_if_empty=False),
        q_threshold("C", "B", "lt", 0.001),
    ], "5m", "critical",
        "Flowstate API has not answered a health probe for 5 minutes.",
        "No /alive (or local /health) requests recorded. The container is down or crash-looping, a migration failed (the API waits for "
        "it), or telemetry stopped reaching Prometheus (alloy). Check Dokploy and 'Startups, shutdowns and migrations'.",
        health, alive, no_data="Alerting", exec_err="Alerting"),
    rule("flowstate-errors-logged", "Flowstate errors logged", "C", [
        q_loki("A", f"sum(count_over_time({ALL} | {ERRLVL} [10m]))", 600),
        q_reduce("B", "A"),
        q_threshold("C", "B", "gt", 0),
    ], "0s", "warning",
        "Flowstate logged {{ $values.B.Value }} error line(s) in the last 10 minutes.",
        "Covers 500 responses, failed migrations and background work. See 'Distinct errors' on the Errors & logs "
        "dashboard for message, stack trace and TraceId.",
        errors, distinct),
    rule("flowstate-restart-loop", "Flowstate API restart loop", "C", [
        q_loki("A", f"sum(count_over_time({STARTED} [15m]))", 900),
        q_reduce("B", "A"),
        q_threshold("C", "B", "gt", 2),
    ], "0s", "critical",
        "Flowstate API started {{ $values.B.Value }} times in 15 minutes.",
        "Three or more 'Application started' lines in 15 minutes: the container is crash-looping (missing config, "
        "OOM, Keycloak unreachable). A normal deploy starts once.",
        errors, lifecycle),
    rule("flowstate-latency-budget", "Flowstate latency budget breached", "D", [
        q_loki("A", f"quantile_over_time(0.99, {WARM_REQ} | unwrap Duration [1h]) by (service_name)", 3600),
        q_reduce("B", "A"),
        q_loki("E", f"sum by (service_name) (count_over_time({WARM_REQ} [1h]))", 3600),
        q_reduce("F", "E"),
        q_math("D", f"$B > {P99_BUDGET_MS} && $F >= 100"),
    ], "15m", "warning",
        'Flowstate warm p99 is {{ printf "%.0f" $values.B.Value }} ms over the last hour (budget ' + str(P99_BUDGET_MS) + ' ms).',
        f"ADR-010: every endpoint < {P99_BUDGET_MS} ms p99. Warm requests only (reused connections), so a deploy's "
        "cold-start burst can't trigger it; needs 100+ requests in the hour. Open 'Endpoints' and 'Requests over "
        "budget' on the Service health dashboard for the route and the trace.",
        health, latency),
    rule("flowstate-disk-full", "Flowstate disk almost full", "C", [
        q_prom("A", DISK_USED, 600),
        q_reduce("B", "A", zero_if_empty=False),
        q_threshold("C", "B", "gt", 0.85),
    ], "10m", "critical",
        "Docker's data disk on the VPS is {{ humanizePercentage $values.B.Value }} full.",
        "Postgres stops accepting writes when the disk fills. Prune old images (`docker image prune -a`), check the "
        "telemetry volumes, or grow the disk.",
        health, disk),
    rule("flowstate-postgres-down", "Flowstate Postgres down", "C", [
        q_prom("A", 'max(pg_up{job="integrations/postgres"})', 300),
        q_reduce("B", "A", zero_if_empty=False),
        q_threshold("C", "B", "lt", 1),
    ], "2m", "critical",
        "Postgres is not answering.",
        "Alloy's Postgres exporter can't connect. The API fails every request that touches the database. Check the "
        "postgres container in Dokploy. No data = Alloy itself is down; 'Flowstate API down' fires too then.",
        health, pg_up, no_data="Alerting", exec_err="Alerting"),
    rule("flowstate-keycloak-down", "Flowstate Keycloak down", "C", [
        q_prom("A", 'max(up{job="keycloak"})', 300),
        q_reduce("B", "A", zero_if_empty=False),
        q_threshold("C", "B", "lt", 1),
    ], "5m", "critical",
        "Keycloak has not answered a metrics scrape for 5 minutes.",
        "Nobody can sign in, and the API can't fetch signing keys after a restart. Check the keycloak container in "
        "Dokploy and its logs.",
        health, kc_up, no_data="Alerting", exec_err="Alerting"),
    rule("flowstate-backups-unhealthy", "Flowstate backups unhealthy", "C", [
        q_prom("A", f'max(backup_agent_health{{{BACKUP}}})', 300),
        q_reduce("B", "A"),
        q_threshold("C", "B", "gt", 1.5),
    ], "10m", "critical",
        "The backup agent reports Unhealthy.",
        "Data may not be reaching the backup repo (WAL gap, S3 down, no full backup in 26 h, failed drill or verify). "
        "Open the backup console (restore runbook §1); its Overview lists the issues. Missing data is "
        "'Flowstate backup agent silent'.",
        health, backup_health),
    rule("flowstate-backup-agent-silent", "Flowstate backup agent silent", "C", [
        q_prom("A", f'max(backup_agent_status_age_seconds{{{BACKUP}}})', 600),
        q_reduce("B", "A", zero_if_empty=False),
        q_threshold("C", "B", "gt", 900),
    ], "5m", "critical",
        "The backup agent has not reported a status for over 15 minutes.",
        "The backup-agent container is down, stuck, or can't reach Alloy. Postgres keeps running, but nothing new is "
        "backed up: WAL piles up in Postgres until the replication slot is dropped (4 GB). Check the container "
        "(`docker ps -a | grep backup-agent`, `docker logs`) and redeploy.",
        health, backup_status_age, no_data="Alerting", exec_err="Alerting"),
]}]}

for name, d in [("health", health), ("usage", usage), ("errors", errors)]:
    with open(os.path.join(OUT, f"{name}.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"wrote grafana/dashboards/{name}.json")

with open(ALERTS, "w", encoding="utf-8", newline="\n") as f:
    json.dump(alerts, f, ensure_ascii=False, indent=2)
    f.write("\n")
print("wrote grafana/provisioning/alerting/alert-rules.json")
