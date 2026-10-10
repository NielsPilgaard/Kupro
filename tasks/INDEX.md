---
title: 'Task Index'
description: >-
  All task specs with priority, phase, dependencies and status, in recommended build order. Pick the first Todo row
  whose dependencies are Done.
status: 'Active'
purpose: 'Lookup table from task number to priority and build order; the only place task priority is recorded.'
---

# Task Index

## TL;DR

P0 = foundation, blocks everything. P1 = v1 must-have, backs a product principle ([product.md](../docs/product.md)).
P2 = v1 scope, but can ship last. P3 = after v1. Work rows top to bottom; status lives in each task's frontmatter.

## Priority

| Priority | Meaning |
|---|---|
| P0 | Critical path — nothing else can start without it |
| P1 | v1 must-have: agent parity, speed, delivery plan, running in prod |
| P2 | v1 scope, not on the critical path |
| P3 | After v1 — start only when v1 is in prod |

## Open tasks (build order)

| # | Task | Priority | Phase | Depends on | Status |
|---|---|---|---|---|---|
| 1 | [001 Repo scaffold, dev environment, CI](001-repo-scaffold.md) | P0 | 0 | — | Todo |
| 2 | [002 Authentication and API tokens](002-auth.md) | P0 | 0 | 001 | Todo |
| 3 | [013 Production deploy on OVHcloud + Dokploy](013-deploy.md) | P1 | 0 | 001, 002 | Todo |
| 4 | [003 Workspaces, projects, members](003-workspaces-projects.md) | P0 | 1 | 002 | Todo |
| 5 | [004 Issues core](004-issues-core.md) | P0 | 1 | 003 | Todo |
| 6 | [005 CLI](005-cli.md) | P1 | 1 | 004 | Todo |
| 7 | [006 MCP server](006-mcp.md) | P1 | 1 | 004, 005 | Todo |
| 8 | [014 Performance budget enforcement](014-perf-budget.md) | P1 | 1 | 004 | Todo |
| 9 | [007 Web UI — list, board, issue detail, command palette](007-web-core-ui.md) | P1 | 1 | 000, 004 | Todo |
| 10 | [010 Sprints](010-sprints.md) | P1 | 1 | 004, 007 | Todo |
| 11 | [011 Delivery plan view v1](011-delivery-plan.md) | P1 | 1 | 007, 010 | Todo |
| 12 | [009 Search](009-search.md) | P2 | 1 | 004 | Todo |
| 13 | [008 Comments, attachments, activity feed](008-comments-attachments.md) | P2 | 1 | 004 | Todo |
| 14 | [012 Realtime updates](012-realtime.md) | P2 | 1 | 007 | Todo |
| 15 | [015 Meeting agent — AI in the room](015-meeting-agent.md) | P3 | 2 | 004, 006, 010, 012 | Todo |

## Completed

| Task | Phase | Status |
|---|---|---|
| [000 Frontend ideas, branding and design guide](completed/000-frontend-ideas.md) | 0 | Done |

## Rationale

- **013 early**: deploying right after auth catches hosting/backup problems while the app is small ([ADR-004](../docs/adr/ADR-004-hosting.md)).
- **005, 006 before UI**: agent parity is principle #1 ([ADR-001](../docs/adr/ADR-001-agent-parity.md)); the CI parity test in 006 guards every later task.
- **014 before UI**: budget enforced from the first endpoints, not retrofitted ([ADR-010](../docs/adr/ADR-010-performance-budget.md)).
- **010 is P1** only because 011 (the differentiator) depends on it.
- **P2 tasks** are independent of each other and can run in parallel once their dependencies are Done.

When adding a task: give it a row here with a priority, and move the row to Completed when its status becomes Done.
