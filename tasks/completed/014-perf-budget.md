---
title: 'Task 014: Performance budget enforcement'
description: >-
  Seed generator, load test in CI against the latency budget, OpenTelemetry metrics and alerting in production.
status: 'Done'
phase: 1
depends_on: [004]
purpose: 'Implementation spec for task 014.'
---

# Task 014: Performance budget enforcement

See [ADR-010](../../docs/adr/ADR-010-performance-budget.md).

## Acceptance criteria

- [x] CI fails if any endpoint p99 > 500 ms on seeded dataset.
- [ ] Prod dashboard shows p50/p99 per endpoint.

## Implementation notes

- `PerfBudgetTests` seeds 10,000 issues (4 projects, mixed states, types, labels, assignees) through the bulk API and
  measures every read endpoint and the hot writes (60 samples each, after warm-up). It fails above p50 100 ms or
  p99 500 ms. It runs in the normal CI test step and writes a table to the job summary (`FLOWSTATE_PERF_REPORT`).
- In-process against Testcontainers Postgres: it catches query regressions (missing index, N+1, unbounded reads),
  not network or Kestrel overhead; production latency is watched by the Grafana dashboard (ADR-014).
- First run: worst endpoint `GET issues?limit=200` at p50 36 ms / p99 40 ms; everything else under 20 ms.
