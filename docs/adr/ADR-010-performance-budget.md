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
- Prod: OpenTelemetry latency histograms + alert on p99 > 500 ms.

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

## Related Decisions

- [ADR-002](ADR-002-backend-stack.md) — backend
- [ADR-003](ADR-003-frontend-stack.md) — optimistic UI
