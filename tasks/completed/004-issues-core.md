---
title: 'Task 004: Issues core'
description: >-
  Issue entity and service: types, hierarchy, fixed workflow, effort, priority, labels, dates, relations, keys.
status: 'Done'
phase: 1
depends_on: [003]
purpose: 'Implementation spec for task 004.'
---

# Task 004: Issues core

See [ADR-008](../../docs/adr/ADR-008-work-item-model.md).

## Scope

- `IssueService`: create (single + bulk), get by key, list with filters + pagination, update (single + bulk), move state.
- Hierarchy validation, relation CRUD, labels.
- Activity log entry for every mutation (actor: human or agent).
- `Idempotency-Key` support on mutations.
- Controllers + OpenAPI with stable `operationId`s.

## Acceptance criteria

- [x] All endpoints < 100 ms p50 on 10k-issue seed.
- [x] Problem Details errors with stable codes.
- [x] Integration tests per service method.

## Implementation notes

- Routes: `/api/v1/workspaces/{workspace}/issues` (+ `/bulk`, `/{key}`, `/{key}/move`, `/{key}/relations`) and
  `/labels`. Issues are addressed by key everywhere; bulk create/update take up to 100 items, all-or-nothing, and
  errors name the item (`issues[3]: ...`).
- Updates change only the fields sent; `clear: [assignee, effort, ...]` empties fields. Labels: `labels` replaces,
  `addLabels`/`removeLabels` edit; unknown label names are created (`bug` is red).
- List: filters `project`, `state`, `type`, `assignee` (`me`/`none`/id), `label`, `parent`; keyset pagination with an
  opaque cursor for `sort=updated|created|priority|rank` (Postgres row-value comparisons).
- Board order is a fractional `rank`; `move` places an issue between two neighbours.
- Every mutation writes an `activity` row (actor member, surface from `X-Flowstate-Client`, field changes as jsonb)
  in the same SaveChanges.
- `Idempotency-Key` (per user, 24 h) replays the first response with `Idempotent-Replayed: true`; a different body
  with the same key is `422 idempotency_key_reused`. Documented as a header on every mutation in the OpenAPI spec.
- Perf probe on a 10k-issue workspace (in-process, Testcontainers Postgres): list/filter/get p50 5-10 ms,
  p95 < 18 ms. Formal enforcement is task 014.
