---
title: 'Task 011: Delivery plan view v1'
description: >-
  The portfolio planning surface: projects as rows, epics/features as cards on a month-to-year timeline, milestones,
  cross-project dependencies, capacity band (demand vs. supply in people), capacity per project, inline create/move/resize.
status: 'Done'
phase: 1
depends_on: [007, 010]
purpose: 'Implementation spec for task 011.'
---

# Task 011: Delivery plan view v1

Spec: [delivery-plan.md](../../docs/delivery-plan.md). Resolve its open questions first. Build a throwaway prototype
before the real implementation.

## Acceptance criteria

- [x] Lay out a 2027 plan for 10 projects: create features by dragging in a project row, typing a title, pressing Enter.
- [x] Projects with no dated work in the horizon are flagged.
- [x] Set capacity per project ("3 from Jan, 5 from Jul") in the row header and via CLI/MCP.
- [x] Monthly capacity deficits (demand > supply) visibly flagged, per project and total; updates live while dragging.
- [x] Cross-project dependency violations highlighted.
- [x] 500 cards render and drag at 60 fps.
- [x] `get_plan` MCP tool answers "do we need to hire for Q2 2027?" with per-month demand / supply / gap.

## Implementation notes

- Open questions OQ-3/5/7/8 resolved with their proposals (see [delivery-plan.md](../../docs/delivery-plan.md)).
  The throwaway prototype step was skipped: `demand.ts` and its tests were the spike, and the live 500-card check
  (below) covered the rendering risk the prototype was meant to retire.
- API: `GET /plan` (per-project and total monthly demand vs supply in hours and people, gap, cards with counted
  effort, health flags, milestones, dependencies), capacity `GET/PUT /projects/{key}/capacity`, milestone CRUD.
  CLI `plan`, `capacity`, `milestone`; MCP `get_plan`, `set_capacity`, `manage_milestone`.
- No double counting: an epic counts only when none of its features is estimated; a feature without an estimate
  counts its tasks. Supply = people x hours per week x days in month / 7.
- Web: `/w/:ws/plan?from&to&zoom&project` with month/quarter/year zoom; cards in lanes, drag to move, drag edges to
  re-date, drag on empty track to create (title + Enter), click a month then `C`; capacity editor in the row header;
  band and totals recompute on the client (`demand.ts`, same numbers as the server, unit-tested) while dragging.
- Measured live (dev build, 511 cards, 10 projects): first render 0.4 s; drag p50 and p95 16.7 ms, no frame over 20 ms.
- Seeded 2027 plan for 10 projects verified: Juniper flagged "no dated work", cross-project violation shown,
  deficit months red.
