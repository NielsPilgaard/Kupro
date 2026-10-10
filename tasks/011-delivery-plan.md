---
title: 'Task 011: Delivery plan view v1'
description: >-
  The portfolio planning surface: projects as rows, epics/features as cards on a month-to-year timeline, milestones,
  cross-project dependencies, capacity band (demand vs. supply in people), capacity per project, inline create/move/resize.
status: 'Todo'
phase: 1
depends_on: [007, 010]
purpose: 'Implementation spec for task 011.'
---

# Task 011: Delivery plan view v1

Spec: [delivery-plan.md](../docs/delivery-plan.md). Resolve its open questions first. Build a throwaway prototype
before the real implementation.

## Acceptance criteria

- [ ] Lay out a 2027 plan for 10 projects: create features by dragging in a project row, typing a title, pressing Enter.
- [ ] Projects with no dated work in the horizon are flagged.
- [ ] Set capacity per project ("3 from Jan, 5 from Jul") in the row header and via CLI/MCP.
- [ ] Monthly capacity deficits (demand > supply) visibly flagged, per project and total; updates live while dragging.
- [ ] Cross-project dependency violations highlighted.
- [ ] 500 cards render and drag at 60 fps.
- [ ] `get_plan` MCP tool answers "do we need to hire for Q2 2027?" with per-month demand / supply / gap.
