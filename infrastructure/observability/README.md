# Observability stack

Flowstate's logs, traces and metrics stay on our own host: the apps export OTLP to Grafana Alloy, which
fans out to Loki, Tempo and Prometheus, with Grafana on top ([ADR-014](../../docs/adr/ADR-014-observability.md)).
The AppHost (`src/Flowstate.AppHost/Observability.cs`) starts the same containers under `aspire run` and in the
compose from `aspire publish`. Everything here is mounted read-only; Grafana provisions itself from these files.

| File | What |
|---|---|
| `alloy/flowstate.alloy` | Alloy: OTLP in (4317/4318) out to Loki, Tempo and Prometheus; scrapes host, Postgres and Keycloak metrics and remote-writes them to Prometheus |
| `alloy/export.alloy` | Where OTLP goes after batching. `aspire run` mounts `export.dev.alloy` instead, which also forwards everything to the Aspire dashboard |
| `loki.yaml`, `tempo.yaml`, `prometheus.yaml` | Single-node storage, 30-day retention, usage reporting off |
| `grafana/provisioning/datasources/` | Prometheus, Loki, Tempo with fixed uids `prometheus`, `loki`, `tempo`, trace ↔ log links |
| `grafana/provisioning/alerting/notifications.yaml` | Email contact point and default notification policy |
| `grafana/provisioning/alerting/alert-rules.json` | Alert rules: API down, errors logged, restart loop, latency budget breached (generated) |
| `grafana/dashboards/health.json` | Service health: up/down, requests, 5xx rate, latency vs the ADR-010 budget (cold vs warm, requests over budget and their traces), endpoints table, runtime and DB pool (generated) |
| `grafana/dashboards/usage.json` | Usage: requests per feature area and caller (session / token / agent), agent share, errors and slowest request per area, weekly rhythm (generated) |
| `grafana/dashboards/errors.json` | Errors & logs: exceptions, distinct errors, error and warning logs, startups and migrations, log volume (generated) |
| `build_dashboards.py` | Generates the dashboards and alert rules |

Dashboards live in the **Flowstate** folder. All selectors use `service_namespace="flowstate"`.

## Local

`aspire run` starts the stack. Grafana is on <http://localhost:3180>, signed in as admin without a login; the Aspire
dashboard keeps working because Alloy forwards to it too. Alert emails land in Mailpit (<http://localhost:8125>).
Alloy's UI (the `alloy` resource's URL in the Aspire dashboard) shows the pipeline graph and each component's health:
the first place to look when a signal is missing. Check a config change with `docker run --rm -v
"$PWD/infrastructure/observability/alloy:/cfg" grafana/alloy fmt /cfg/flowstate.alloy`.

Data lives in Docker volumes (`flowstate-loki-data`, `-tempo-data`, `-prometheus-data`, `-grafana-data`) and survives
restarts. If Grafana shows two **Flowstate** folders (left over from an earlier provisioning layout), delete the empty
one or remove the `flowstate-grafana-data` volume.

## Production

Grafana's variables, domain, alert test and the PingPuffin check are in the
[deploy runbook](../../docs/runbooks/deploy.md). Grafana publishes no host port; Dokploy routes its domain to port 3000.

## Changing a dashboard or alert

Edit `build_dashboards.py`, run `python infrastructure/observability/build_dashboards.py`, commit the regenerated JSON
(CI fails on drift). Grafana picks up dashboard changes within 30 s locally; in production after the next deploy.
Alert rules and dashboards are read-only in the UI — copy any experiment back into the script.

## Where the numbers come from

Request counts, status codes, latency, feature areas and callers are queried from the request log in Loki, one line
per request (`src/Flowstate.Api/Common/RequestLogging.cs`), not from the `http_server_*` metrics. At low traffic
most metric series are created with their final value, and `increase()`/`rate()` miss it. Fields on each line:
`Method`, `Path`, `Route` (template, e.g. `/api/v1/workspaces/{workspace}/issues`), `Caller`, `StatusCode`,
`Duration` (ms), plus `RequestId` and `TraceId` from the log scope. `RequestLoggingTests` pins that shape; rename a
field and panels go blank.

- **Caller**: `session` = Keycloak login (web app, MCP client via OAuth); `token` = personal API token (CLI,
  scripts); `agent` = agent token; `anonymous`.
- **Feature area** = the first route segment after `/api/v1/` and the workspace prefix: `issues`, `projects`,
  `members`, `tokens`, `mcp`… New features show up without editing the dashboards.
- **Scanners**: paths that match no endpoint are logged with `Route="(unmatched)"` and kept out of the numbers. They
  get 401, not 404, because the fallback authorization policy runs first.
- **Levels** use OTel `severity_number` (Error ≥ 17, Warning 13–16) and `severity_text`, not `detected_level`,
  whose casing differs between Loki versions.
- **Metrics** arrive through Prometheus' OTLP receiver: `job="flowstate/api"`, with `deployment_environment_name` and
  `service_version` promoted to labels.
- **Infrastructure metrics** are scraped by Alloy and remote-written: `job="integrations/unix"` (`node_*`, read from inside the
  Alloy container: CPU, memory and load are the host's, disk is the filesystem Docker's volumes live on),
  `job="integrations/postgres"` (`pg_*`) and `job="keycloak"` (`up`, `jvm_*`, Keycloak's own). They feed the "Host and
  dependencies" row and the disk-full, Postgres-down and Keycloak-down alerts.

The "API alive" panel and the "API down" alert use the Prometheus metric for the health probe: `/alive`, which the
compose healthcheck hits every 10 s (`/health` under `aspire run`). Health probes are not request-logged.

## Reading the latency panels

ADR-010's budget is p50 < 100 ms and p99 < 500 ms; the latency graphs draw both as dashed lines.

- **Warm vs cold.** A request is *warm* when it arrived on a reused connection: Kestrel's `RequestId` is
  `<ConnectionId>:<seq>` and warm means seq > `00000001`. A fresh instance has no connections, so the cold-start
  burst after a deploy (Keycloak key discovery, EF model build, JIT) is all seq 1. Browsers also open new
  connections on a warm instance, so "new connection" holds cold starts plus some fast requests. Budget numbers and
  the budget alert use warm requests only.
- **Sample size.** p50 needs 20 warm requests and p99 needs 100, per stat, bucket or endpoint; below that the stat
  shows "too few" and the column stays blank. A p99 over 30 requests is just the slowest request.
- **Causes.** "Requests over budget" (Tempo, server spans over 500 ms) and "Slow calls inside requests" (outgoing
  HTTP and SQL spans over 50 ms) link each slow request to its trace. Tempo keeps 30 days.
- Deploys are the purple annotations on every graph, labelled with `service.version`.

## Querying from an agent

`.mcp.json` registers [mcp-grafana](https://github.com/grafana/mcp-grafana) (Docker, read-only). By default it
talks to the local Grafana at `http://host.docker.internal:3180` and needs no token. For production, create a
service account with role **Viewer** and a token, then start the agent with `GRAFANA_URL=https://grafana.<domain>`
and `GRAFANA_SERVICE_ACCOUNT_TOKEN=<token>`. Agents can query Loki (selector `{service_namespace="flowstate"}`),
Tempo (search, trace by id, TraceQL metrics) and Prometheus, e.g. to diagnose a failed deploy from the error logs.
