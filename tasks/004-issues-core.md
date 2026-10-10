---
title: 'Task 004: Issues core'
description: >-
  Issue entity and service: types, hierarchy, fixed workflow, effort, priority, labels, dates, relations, keys.
status: 'Todo'
phase: 1
depends_on: [003]
purpose: 'Implementation spec for task 004.'
---

# Task 004: Issues core

See [ADR-008](../docs/adr/ADR-008-work-item-model.md).

## Scope

- `IssueService`: create (single + bulk), get by key, list with filters + pagination, update (single + bulk), move state.
- Hierarchy validation, relation CRUD, labels.
- Activity log entry for every mutation (actor: human or agent).
- `Idempotency-Key` support on mutations.
- Controllers + OpenAPI with stable `operationId`s.

## Acceptance criteria

- [ ] All endpoints < 100 ms p50 on 10k-issue seed.
- [ ] Problem Details errors with stable codes.
- [ ] Integration tests per service method.
