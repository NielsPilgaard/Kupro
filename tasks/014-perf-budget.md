---
title: 'Task 014: Performance budget enforcement'
description: >-
  Seed generator, load test in CI against the latency budget, OpenTelemetry metrics and alerting in production.
status: 'Todo'
phase: 1
depends_on: [004]
purpose: 'Implementation spec for task 014.'
---

# Task 014: Performance budget enforcement

See [ADR-010](../docs/adr/ADR-010-performance-budget.md).

## Acceptance criteria

- [ ] CI fails if any endpoint p99 > 500 ms on seeded dataset.
- [ ] Prod dashboard shows p50/p99 per endpoint.
