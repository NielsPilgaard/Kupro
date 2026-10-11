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
| 3 | [013 Production deploy on OVHcloud + Dokploy](013-deploy.md) | P1 | 0 | 001, 002 | Todo |
| 11 | [011 Delivery plan view v1](011-delivery-plan.md) | P1 | 1 | 007, 010 | Todo |
| 12 | [009 Search](009-search.md) | P2 | 1 | 004 | Todo |
| 13 | [008 Comments, attachments, activity feed](008-comments-attachments.md) | P2 | 1 | 004 | Todo |
| 14 | [012 Realtime updates](012-realtime.md) | P2 | 1 | 007 | Todo |
| 15 | [015 Meeting agent — AI in the room](015-meeting-agent.md) | P3 | 2 | 004, 006, 010, 012 | Todo |

## Completed

| Task | Phase | Status |
|---|---|---|
| [000 Frontend ideas, branding and design guide](completed/000-frontend-ideas.md) | 0 | Done |
| [001 Repo scaffold, dev environment, CI](completed/001-repo-scaffold.md) | 0 | Done |
| [002 Authentication and API tokens](completed/002-auth.md) | 0 | Done |
| [003 Workspaces, projects, members](completed/003-workspaces-projects.md) | 1 | Done |
| [004 Issues core](completed/004-issues-core.md) | 1 | Done |
| [005 CLI](completed/005-cli.md) | 1 | Done |
| [006 MCP server](completed/006-mcp.md) | 1 | Done |
| [014 Performance budget enforcement](completed/014-perf-budget.md) | 1 | Done |
| [007 Web UI — list, board, issue detail, command palette](completed/007-web-core-ui.md) | 1 | Done |
| [010 Sprints](completed/010-sprints.md) | 1 | Done |

## Rationale

- **013 early**: deploying right after auth catches hosting/backup problems while the app is small ([ADR-004](../docs/adr/ADR-004-hosting.md)).
- **005, 006 before UI**: agent parity is principle #1 ([ADR-001](../docs/adr/ADR-001-agent-parity.md)); the CI parity test in 006 guards every later task.
- **014 before UI**: budget enforced from the first endpoints, not retrofitted ([ADR-010](../docs/adr/ADR-010-performance-budget.md)).
- **010 is P1** only because 011 (the differentiator) depends on it.
- **P2 tasks** are independent of each other and can run in parallel once their dependencies are Done.

When adding a task: give it a row here with a priority, and move the row to Completed when its status becomes Done.
