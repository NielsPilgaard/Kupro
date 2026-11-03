---
title: 'Task 030: In-memory plan cache with FusionCache'
description: >-
  Add FusionCache as an L1 read-through cache behind a tenant-pinning wrapper, and cache the delivery plan first. Plan
  writes invalidate it from one place, and a test proves every plan-affecting write does.
status: 'Todo'
phase: 1
depends_on: []
purpose: 'Implementation spec for task 030.'
---

# Task 030: In-memory plan cache with FusionCache

Budget: [ADR-010](../../docs/adr/ADR-010-performance-budget.md). Tenancy: [ADR-009](../../docs/adr/ADR-009-multi-tenancy.md).

## TL;DR

- `ZiggyCreatures.FusionCache` (MIT), L1 only: one API instance, so no Redis and no backplane. A library, so no
  vendor register entry ([ADR-006](../../docs/adr/ADR-006-eu-vendor-policy.md)).
- Cache only the plan. It is the heaviest read: on 10,000 issues with 1,000 dated epics, `GET plan` is 11 ms and 8 queries
  for 244 KB, and many clients refetch it after one write. Measure, then decide on other endpoints in a later task.
- Issue lists stay uncached: every write invalidates them, and filters make for many keys.
- API token and membership checks stay uncached, so revocation stays immediate.

## The wrapper

`WorkspaceCache`, a scoped service, is the only way to reach `IFusionCache`. It follows the `TenantCache` pattern from
another project of the owner's:

- Each factory runs in its own DI scope with `ExecutionContext` flow suppressed, so eager refresh and soft timeouts can
  outlive the request. A factory reaching for request state fails on every call, not only on a rare background refresh.
- Restore `Activity.Current` in the factory, so its queries stay on the request's trace.
- Factories are static and take `(IServiceProvider scoped, TState state, CancellationToken)`. They resolve the feature
  service from the scope, because queries live in feature services ([ADR-002](../../docs/adr/ADR-002-backend-stack.md)).
- Pin the new scope's `WorkspaceContext` with a new internal method, such as `UseBackgroundWorkspace(id)`. `Set` needs a
  resolved member, which a background refresh doesn't have. An unpinned context returns empty rows, so a missed pin would
  cache an empty result: the wrapper must always pin.
- **The wrapper prefixes each key with the workspace id.** Callers can't forget it, because they never build the full key.

## Caching the plan

- Split `PlanService.GetPlanAsync`: validate the horizon and resolve defaults first, then read through the cache.
- Key: resolved `from`, `to`, sorted project keys and today's date. The default range and date-based results change with
  the date.
- Don't cache error results.
- `PlanResponse` is shared between requests, so nothing may mutate it after it is cached.
- Options: safety TTL of 5 minutes, `EagerRefreshThreshold` 0.9, `FactorySoftTimeout` 1 s. Bound memory with a size limit
  on the cache's own `MemoryCache`: an entry can pass 200 KB.
- Fail-safe helps only when the plan query is slow or fails by itself. In a Postgres outage the membership check fails
  before the cache is reached, so don't count on it for outages.

## Invalidation

This is the part that can go wrong silently. Invalidate after commit, from one place:

- In `RealtimeEvents`, `IssuesAsync` and `ChangedAsync` for the `Issue`, `Sprints` and `Plan` topics. `Issue` is needed
  because relations feed the plan's dependencies. Invalidate before the push, so a failed push can't skip it, and never
  let invalidation fail a committed write.
- **Two writes emit no event today and need an explicit call:** `WorkspaceService` updating hours per week, and
  `ProjectService` (name, colour and key appear on the plan).
- Task [023](../023-import-framework.md) must invalidate after import commits; add a line to its spec.
- **In-flight refresh race:** a refresh started before a write can finish after the invalidation and store old data.
  Check how FusionCache behaves. If the race is real, put a per-workspace generation counter in the key, incremented on
  invalidation, so an old refresh writes to a key nobody reads. That also removes the need for tags.
- The web client has a related gap: on a relation change, `touched` in `web/src/api/realtime.ts` doesn't refetch `getPlan`.
  Fix it here.

## Acceptance criteria

- [ ] Table-driven test: for each plan-affecting write (create issue, change effort, dates, state; relation add and
  remove; capacity; milestone create, update, delete; sprint close; hours per week; project rename), `GET plan` shows the
  change on the very next read.
- [ ] A cached plan is never served across workspaces: two workspaces with identical parameters each see their own.
- [ ] A deactivated member or revoked token is refused while the plan is cached.
- [ ] A test with a delayed factory shows a write during refresh is not overwritten by old data.
- [ ] A background refresh runs correctly after the request's scope is disposed.
- [ ] `PerfBudgetTests` covers `GET plan`. Implementation notes record p50, p95 and query count before and after, both REST
  and MCP `get_plan`.
- [ ] The wrapper is recorded in [ADR-010](../../docs/adr/ADR-010-performance-budget.md), including when to cache
  another endpoint.
