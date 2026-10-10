---
title: 'Task 010: Sprints'
description: >-
  Per-project sprints with start/end dates, sprint board, assigning issues, and closing with carry-over of unfinished work.
status: 'Todo'
phase: 1
depends_on: [004, 007]
purpose: 'Implementation spec for task 010.'
---

# Task 010: Sprints

See [ADR-008](../docs/adr/ADR-008-work-item-model.md).

## Scope

- Sprint CRUD, one active per project.
- Sprint board = board filtered by sprint; total effort (hours) shown. No per-person capacity in v1.
- Close sprint: prompt to move unfinished issues to next sprint or backlog.

## Acceptance criteria

- [ ] Full sprint cycle via UI and via CLI/MCP.
