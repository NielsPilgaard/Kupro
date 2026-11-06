---
title: 'Task 032: PgBouncer in Aspire and the compose stack'
description: >-
  Put PgBouncer in transaction mode between the API and Postgres, in the AppHost for both `aspire run` and the generated
  compose file. Start only when a measure below asks for it. Migrations, Keycloak, the backup agent and the metrics
  exporter stay on direct connections.
status: 'Todo'
phase: 1
depends_on: ['013']
purpose: 'Implementation spec for task 032.'
---

# Task 032: PgBouncer in Aspire and the compose stack

Topology: [ADR-012](../docs/adr/ADR-012-aspire.md). Hosting: [ADR-004](../docs/adr/ADR-004-hosting.md). Budget:
[ADR-010](../docs/adr/ADR-010-performance-budget.md).

## TL;DR

- One PgBouncer container, transaction mode, used by `api` only. Same wiring locally and in production.
- **The search threshold breaks silently.** It travels as a startup option, which PgBouncer won't pass on. Solve that
  first; see [Search threshold](#search-threshold).
- Don't start without a trigger. With one API instance, Npgsql already pools, so PgBouncer adds little until one of the
  triggers below holds.

## When to start

Any of these, measured in production:

- A second API instance, or another service that needs its own pool on the `kupro` database.
- Grafana's "DB connections" stat stays above 70% of `max_connections` under normal traffic.
- Postgres memory pressure traced to idle backends.

**Check this first, it may be enough.** `max_connections` is 100 ([postgresql.conf](../infrastructure/postgres/postgresql.conf)),
and the API's connection string sets no `Max Pool Size`, so Npgsql's default of 100 applies. Keycloak, the backup agent
and the exporter share the same limit. A `Max Pool Size` on the API may solve the problem at no cost.

## Topology

| Client | Connects to | Why |
|---|---|---|
| `api` | `pgbouncer` | The point of this task |
| `migrations` | `postgres` | Long DDL transactions; should not hold a pool slot or be cut by pool timeouts |
| `keycloak` | `postgres` | Own JDBC pool, own database, not ours to tune |
| `backup-agent` | `postgres` | Uses the socket volume and `Pooling = false` ([ADR-016](../docs/adr/ADR-016-postgres-backups.md)) |
| Postgres metrics exporter | `postgres` | Must see the real server, including when PgBouncer is down |

## AppHost

- New extension in `src/Kupro.AppHost/PgBouncer.cs`, in the style of `Backups.cs` and `Observability.cs`. Config in
  `infrastructure/pgbouncer/`, mounted with the repo-relative bind mounts the compose environment already has.
- Pin an image tag, like the other containers. `edoburu/pgbouncer` (`1.X.Y-pZ` tags) is the first candidate. Check that it
  is still maintained, or build from `infrastructure/pgbouncer/Dockerfile` as we do for Postgres.
- `.WithLifetime(ContainerLifetime.Persistent)`, like `postgres`. Persistent containers share a network of their own (see
  the Mailpit comment in `Program.cs`), so check that a session-lifetime container can't reach them before choosing
  otherwise. `.WaitFor(postgres)`.
- No published host port in compose: `service.Ports.Clear()`, as for `api`. The restart policy comes from the existing
  `ConfigureComposeFile` loop.
- Give it a compose healthcheck, and have `api` wait for it.
- `api` gets `ConnectionStrings__kupro` built from the PgBouncer endpoint. `migrations` keeps `WithReference(database)`.
- `deploy/` is generated: run `aspire publish`, don't hand-edit.

## Settings

- `pool_mode = transaction`. `default_pool_size` well under `max_connections`, leaving room for Keycloak and the backup
  agent. Size it from the production numbers that triggered this task, and record them.
- Auth: Postgres is `scram-sha-256`, and PgBouncer's `auth_file` must hold SCRAM secrets or plain-text passwords. Take the
  password from the environment. No secret in a checked-in file.
- Npgsql: `No Reset On Close=true` on the API's connection string, required when Npgsql's own pool stays on in transaction
  mode. Keep that pool, with a small `Max Pool Size`, so the budget doesn't pay for a new client connection per request.
  Measure it against `Pooling=false`.
- `max_prepared_statements`: leave it at 0 as long as auto-prepare stays off ([ADR-010](../docs/adr/ADR-010-performance-budget.md)
  IMP-001). Confirm with a test what Npgsql sends with auto-prepare off.

## Search threshold

`WithSearchSettings` in `src/Kupro.Api/Data/DatabaseExtensions.cs` adds `Options=-c pg_trgm.word_similarity_threshold=0.5`
to every connection ([ADR-010](../docs/adr/ADR-010-performance-budget.md) IMP-004).

- PgBouncer only allows startup parameters it can track, so by default the connection is refused.
- `track_extra_parameters` can't take `options`.
- Adding `options` to `ignore_startup_parameters` makes the connection work and **drops the setting**. The threshold goes
  back to 0.6: search still answers, with no error, but misses one-letter typos in short words.
- A `SET` per session doesn't survive transaction mode either.

Recommended: `connect_query` on the PgBouncer database entry, which runs once per server connection. The setting then
lives in two places, the API's connection string for direct use and PgBouncer's config, so the acceptance test below has
to go through PgBouncer. The alternative is `ALTER DATABASE ... SET` in `MigrateDatabaseAsync`, next to the backup agent
grant, so a restored database gets it too. Pick one, and update IMP-004.

## Session state check

Re-run this when the task starts, since the code will have moved. On 2026-11-05:

- `pg_advisory_xact_lock` in `MeetingService` and `WorkspaceService`: transaction scoped, safe. Session-level advisory locks
  are not, so ban them.
- No `LISTEN`, `SET` or `PREPARE` in `src/`. The planned fan-out in `WorkspaceHub`'s comment uses `LISTEN/NOTIFY`, which
  would need a direct connection.
- Also not available in transaction mode: `WITH HOLD` cursors and temp tables without `ON COMMIT DROP`.

## Observability

- Add a PgBouncer exporter as a scrape target through `WithScrapeTargets`, and record it in
  [ADR-014](../docs/adr/ADR-014-observability.md).
- Add panels in `infrastructure/observability/build_dashboards.py`, then regenerate: clients waiting, server connections
  by state, longest wait. Add an alert on sustained waiting clients. The existing "Postgres connection pool" panel then
  shows the API's side only: update its description.

## Acceptance criteria

- [ ] `aspire run` and the generated `deploy/docker-compose.yaml` both send the API through PgBouncer, and migrations,
  Keycloak, the backup agent and the exporter directly to Postgres.
- [ ] A test through PgBouncer in transaction mode: a one-letter-typo search on a short word finds the issue.
- [ ] The integration suite passes with the API on PgBouncer, at least once, and the implementation notes say how.
- [ ] Two concurrent writers still serialize on `pg_advisory_xact_lock` through PgBouncer.
- [ ] A Postgres restart recovers without restarting the API. So does a volume swap restore, following the
  [restore runbook](../docs/runbooks/restore.md).
- [ ] Implementation notes record p50 and p95 for `GET issues` and `GET plan` with and without PgBouncer, measured as in
  ADR-010 IMP-001, and the budget still holds.
- [ ] Docs: PgBouncer in the compose stack list in [ADR-004](../docs/adr/ADR-004-hosting.md), in the register in
  [ADR-006](../docs/adr/ADR-006-eu-vendor-policy.md) as self-hosted OSS, the pooling decision and its numbers in
  [ADR-010](../docs/adr/ADR-010-performance-budget.md). Any new production parameter in the
  [deploy runbook](../docs/runbooks/deploy.md).
