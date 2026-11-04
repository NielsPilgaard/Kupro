---
title: 'ADR-010: Performance Budget'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['performance', 'nfr']
supersedes: ''
superseded_by: ''
description: >-
  Every API action must complete in under 500 ms p99 server-side (target < 100 ms p50), and the UI must reflect user
  actions within one frame via optimistic updates. Enforced by load tests in CI and latency alerts in production.
---

# ADR-010: Performance Budget

## TL;DR

- Server: < 500 ms p99, < 100 ms p50 for every endpoint, measured at the API.
- UI: user action visible < 50 ms (optimistic); initial app load < 1.5 s on broadband.
- CI: load test (k6 or NBomber) against seeded DB (10k issues/workspace) fails the build on budget breach.
- Prod: per-endpoint p50/p99 dashboard + alert on p99 > 500 ms ([ADR-014](ADR-014-observability.md)).

## Status

**Accepted**

## Context

"Extremely fast" is a core promise. Budgets only hold when measured continuously.

## Decision

As above. Rules of thumb: every list query paginated and projected; every filter backed by an index; no N+1 (EF query
count asserted in integration tests for hot endpoints).

## Consequences

### Positive

- **POS-001**: Regressions caught at PR time.

### Negative

- **NEG-001**: Load test infrastructure to maintain in CI.

## Implementation Notes

- **IMP-001**: Npgsql auto-prepare stays off (task 029). After five executions Postgres may switch a prepared statement to
  a generic plan, which assumes an average-sized tenant. One statement in `GET issues/{key}` went from 0.09 to 5.5 ms by
  scanning all 10,488 of a large tenant's rows, and that cost grows with tenant size, so the largest customers pay most.
  Re-measure before reconsidering, and on analyzed tables: without planner stats the gain looks like 3x and is not real.

  | p50 ms, off → on (`Max Auto Prepare=64`, `Auto Prepare Min Usages=2`) | No stats | Analyzed | Analyzed, 300 small tenants beside a 10k one |
  |---|---|---|---|
  | `GET issues?limit=200` | 9.9 → 3.3 | 4.0 → 3.6 | 3.9 → 3.7 |
  | `GET issues/CORE-1` | 8.4 → 6.4 | 4.0 → 2.2 | 4.1 → **6.5** |
  | `PATCH issues/{key}` | | 8.5 → 5.5 | 7.7 → **9.9** |
  | `POST issues` | | 10.3 → 7.6 | 10.4 → **12.0** |
  | `GET issues?q=issue` (every row matches) | 40 → **70** | 39 → 39 | 39 → 39 |

  Release build, API in-process, Postgres in Docker on a laptop, one client: relative cost, not production latency.
- **IMP-002**: `PerfBudgetTests` runs `ANALYZE` after seeding, because autovacuum does that in production.

## Related Decisions

- [ADR-002](ADR-002-backend-stack.md) — backend
- [ADR-003](ADR-003-frontend-stack.md) — optimistic UI
- [ADR-014](ADR-014-observability.md) — production latency dashboards and alert
