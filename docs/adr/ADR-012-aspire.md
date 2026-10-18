---
title: 'ADR-012: Local Orchestration with Aspire, Compose via aspire publish'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['infrastructure', 'dev-environment', 'deployment']
supersedes: ''
superseded_by: ''
description: >-
  .NET Aspire AppHost is the single source of truth for the service topology. `aspire run` starts the local dev stack
  (API, SPA, PostgreSQL, Keycloak) with the Aspire dashboard; `aspire publish` generates the production Docker Compose
  file deployed by Dokploy. No hand-written compose file.
---

# ADR-012: Local Orchestration with Aspire, Compose via aspire publish

## TL;DR

- `src/Flowstate.AppHost` declares the topology: `api`, `web` (Vite), `postgres`, `keycloak` (+ dev realm import), and the observability stack ([ADR-014](ADR-014-observability.md)).
- Local dev: `aspire run` — one command, dashboard with logs/traces/metrics (OpenTelemetry) out of the box.
- Prod: `aspire publish` with the Docker Compose publisher → `deploy/docker-compose.yaml` + `.env` template → Dokploy ([ADR-004](ADR-004-hosting.md)).
- `src/Flowstate.ServiceDefaults` holds shared OTel, health checks and resilience defaults.
- Same generated compose file is the self-hosting package.

## Status

**Accepted**

## Context

[ADR-004](ADR-004-hosting.md) deploys a Docker Compose stack and wants dev/prod parity. A hand-written dev compose plus a
hand-written prod compose drift apart. Aspire describes the topology once in C#, runs it locally with a good dashboard,
and emits compose for production. Aspire is MIT-licensed OSS — no vendor ([ADR-006](ADR-006-eu-vendor-policy.md)).

## Decision

- AppHost uses `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Keycloak`, the JavaScript/Vite hosting integration for
  `web` in run mode (configured for Bun, [ADR-003](ADR-003-frontend-stack.md)), `web/Dockerfile` in publish mode, and `Aspire.Hosting.Docker` (`AddDockerComposeEnvironment`) for publish.
- Connection strings, Keycloak URLs and secrets flow through Aspire references/parameters — no duplicated config in
  `appsettings.*.json`.
- Generated compose is committed under `deploy/`; CI regenerates and fails on drift. Prod-only tweaks (volumes, restart
  policy, Traefik labels for Dokploy) go in AppHost publish callbacks, never by editing the generated file.
- Secrets are compose `.env` parameters, set in Dokploy env (never committed).

## Consequences

### Positive

- **POS-001**: One topology definition for dev and prod; no compose drift.
- **POS-002**: Local OTel dashboard for free — helps hit the latency budget ([ADR-010](ADR-010-performance-budget.md)).
- **POS-003**: New contributor / agent onboarding = `aspire run`.

### Negative

- **NEG-001**: Compose publisher is young; some prod config needs publish callbacks rather than plain YAML.
- **NEG-002**: Requires .NET SDK + Aspire CLI locally even for frontend-only work.
- **NEG-003**: Self-hosters get generated YAML — must stay readable; review diffs in `deploy/`.
- **NEG-004**: SPA build-time config means per-environment images until IMP-005 is done.

## Alternatives Considered

### Hand-written compose for dev and prod

- **ALT-001**: **Rejection Reason**: Two files to keep in sync; no dashboard; duplicated config.

### Aspire for dev, hand-written prod compose

- **ALT-002**: **Rejection Reason**: Keeps the drift problem Aspire publish solves.

## Implementation Notes

- **IMP-001**: Pin Aspire package + CLI versions together (`global.json`/`Directory.Packages.props`).
- **IMP-002**: SPA image = `web/Dockerfile`, build context repo root: `oven/bun` stage (`bun install --frozen-lockfile`,
  copy `openapi/`, `bun run build`) → `nginx:alpine` serving `dist/` with `web/nginx.conf` (SPA fallback, immutable
  hashed assets). Traefik (Dokploy) in front for TLS ([ADR-004](ADR-004-hosting.md)).
- **IMP-003**: EF migrations: run as a separate migration step/service in publish, not on API startup ([ADR-002](ADR-002-backend-stack.md)).
- **IMP-004**: Integration tests keep Testcontainers ([ADR-002](ADR-002-backend-stack.md) IMP-002); Aspire
  `DistributedApplicationTestingBuilder` is for E2E (Playwright) only.
- **IMP-005**: `VITE_KEYCLOAK_URL` is a build arg — baked into the image, so one image per environment. Before shipping
  the self-hosting package, move to runtime config (nginx serves `config.json` rendered from env at container start).

## Related Decisions

- [ADR-004](ADR-004-hosting.md) — compose stack this ADR generates
- [ADR-002](ADR-002-backend-stack.md) — API, migrations, Testcontainers
- [ADR-003](ADR-003-frontend-stack.md) — SPA orchestrated as `web`
- [ADR-005](ADR-005-auth-keycloak.md) — Keycloak resource + realm import
- [ADR-014](ADR-014-observability.md) — telemetry stack in dev and prod (the Aspire dashboard is dev-only)
- [ADR-017](ADR-017-public-site.md) — the `site` resource

## References

- **REF-001**: Aspire Docker Compose publishing — https://aspire.dev
