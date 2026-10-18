---
title: 'Task 010: Sprints'
description: >-
  Per-project sprints with start/end dates, sprint board, assigning issues, and closing with carry-over of unfinished work.
status: 'Done'
phase: 1
depends_on: [004, 007]
purpose: 'Implementation spec for task 010.'
---

# Task 010: Sprints

See [ADR-008](../../docs/adr/ADR-008-work-item-model.md).

## Scope

- Sprint CRUD, one active per project.
- Sprint board = board filtered by sprint; total effort (hours) shown. No per-person capacity in v1.
- Close sprint: prompt to move unfinished issues to next sprint or backlog.

## Acceptance criteria

- [x] Full sprint cycle via UI and via CLI/MCP.

## Implementation notes

- `Sprint` per project (planned, active, closed); a partial unique index allows one active sprint per project.
- Issues join a sprint through `sprint` on create/update (`active` or an id; `clear: [sprint]` removes it) and are
  filtered with `?sprint=active|none|<id>`. Only open sprints of the issue's own project are accepted.
- Close moves unfinished issues to the next planned sprint (earliest start) or the backlog; each move is logged on the
  issue. Without a planned sprint, `next` fails with `no_next_sprint`.
- Surfaces: REST `/sprints`, CLI `kupro sprint list|get|create|update|start|close` and `--sprint` on issues, MCP
  `list_sprints` and `manage_sprint` (create/start/close; renaming is opted out of MCP).
- UI: Sprints in the sidebar; per-project tabs; active sprint board with done/total hours and a close prompt
  (next sprint or backlog); planned sprints with Start; sprint field in the issue panel.
