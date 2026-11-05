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
- Cache: only the delivery plan, in memory, behind `WorkspaceCache` (IMP-003).

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
- **IMP-002**: `PerfBudgetTests` runs `ANALYZE` after seeding, because autovacuum does that in production. It runs after
  the parallel test classes, and an endpoint fails only when it is over budget on three attempts: load on the machine
  slows every sample for seconds at a time, a regression stays.
- **IMP-003**: `WorkspaceCache` is the only way to reach `IFusionCache` (FusionCache, memory only, task 030). It prefixes
  each key with the workspace id and the workspace's generation, and runs each factory in its own scope pinned to the
  workspace, without the request's ExecutionContext.

  - **Invalidation advances the generation.** With `Remove` or `Expire`, a refresh already running stored its old data and
    the next read got it (measured on 2.9.0). A generation leaves that refresh writing to a key nobody reads. Tags
    passed the same check, but their expiry entries live in the same size-limited memory, so evicting one could bring old
    data back.
  - **Where it invalidates:** `RealtimeEvents`, before the push, for issue, sprint and plan changes. Writes with no realtime
    event call `Invalidate` themselves: `WorkspaceService.UpdateAsync` and `ProjectService`. A new write that the plan reads
    must do one or the other; `PlanCacheTests` lists them.
  - **Only the plan is cached.** It is the heaviest read and many clients refetch it after one write. Both views share one
    entry per horizon and project selection. Issue lists stay uncached, since every write invalidates them and filters make
    for many keys. Token and membership checks stay uncached, so revocation stays immediate.
  - **Cache another endpoint** only when a measure shows it over budget or a high share of database load, its reads far
    outnumber its writes, and every write to its data can be listed in a test like `PlanCacheTests`.
  - **Limits:** one API instance, since the generations are in memory. A second instance needs a backplane and shared
    generations. 64 MB size limit, sized from an estimate per value. 5 minute TTL as a safety net, fail-safe for 15 minutes.

  On 10,000 issues with 1,000 dated epics, measured as in IMP-001, 200 samples:

  | | Before p50 / p95 ms, queries | Hit | After a write |
  |---|---|---|---|
  | `GET plan` | 11.6 / 13.8, 8 | 1.9 / 2.1, 1 | 12.3 / 21.1, 8 |
  | `GET plan?view=summary` | 9.8 / 11.4, 8 | 1.1 / 1.2, 1 | |
  | MCP `get_plan` (summary) | 11.7 / 14.0, 10 | 2.6 / 3.0, 3 | 12.9 / 18.8, 10 |
  | MCP `get_plan` (full) | 15.8 / 32.2, 10 | 10.7 / 23.6, 3 | |

  The queries left on a hit are the access checks. MCP full stays slow on a hit because it serializes 244 KB.

## Related Decisions

- [ADR-002](ADR-002-backend-stack.md) — backend
- [ADR-009](ADR-009-multi-tenancy.md) — tenant isolation the cache keys follow
- [ADR-003](ADR-003-frontend-stack.md) — optimistic UI
- [ADR-014](ADR-014-observability.md) — production latency dashboards and alert
