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
| P1 | v1 must-have: agent parity, speed, delivery plan, meeting agent, running in prod |
| P2 | v1 scope, not on the critical path |
| P3 | After v1 — start only when v1 is in prod |

## Open tasks (build order)

| # | Task | Priority | Phase | Depends on | Status |
|---|---|---|---|---|---|
| 3 | [013 Production deploy on OVHcloud + Dokploy](013-deploy.md) | P1 | 0 | 001, 002 | Todo |
| 15 | [015 Meeting agent — AI in the room](015-meeting-agent.md) | P1 | 2 | 004, 006, 010, 012 | In progress |
| 20 | [020 CLI install script and `kupro install mcp`](020-cli-distribution.md) | P1 | 1 | 005, 006 (013 to close) | In progress |
| 22 | [022 Open-source readiness](022-open-source-readiness.md) | P1 | 1 | — | In progress |
| 23 | [023 Import framework, CSV and snapshot import](023-import-framework.md) | P1 | 1 | 016 (013 to close) | In progress |
| 24 | [024 Import from Jira (Cloud and Data Center)](024-import-jira.md) | P1 | 1 | 023 | Todo |
| 25 | [025 Import from Azure DevOps (Services and Server)](025-import-azure-devops.md) | P1 | 1 | 023 | In progress |
| 26 | [026 Import from Linear and Trello](026-import-linear-trello.md) | P2 | 1 | 023 | Todo |
| 27 | [027 Import from GitHub Issues, Monday and YouTrack](027-import-github-monday-youtrack.md) | P2 | 1 | 023 | Todo |
| 32 | [032 PgBouncer in Aspire and the compose stack](032-pgbouncer.md) | P3 | 1 | 013 | Todo |

## What an agent can pick up

Checked 2026-11-06. The build order above is not the same as what is buildable: **023 and the connectors 024, 026 and
027 are buildable by an agent now**. 025's connector is done and the rest of it waits on 023. Every other open row
waits on a person, directly or through a dependency. Don't spend a session re-deriving
this; read this table, and update it when a step is done. "Depends on" lists what blocks *starting*; a dependency
that only blocks closing a task is written "(N to close)".

| Task | State | What unblocks an agent |
|---|---|---|
| 013 | Code is in the repo; everything left is manual steps (domain, VPS, Dokploy, first deploy) | The owner working through go-live |
| 015 | Built; the accuracy check and real-meeting trial are manual steps | Recorded standups |
| 022 | Only manual steps left: the CLA test from a second account, and the social preview upload | Both are the owner's |
| 020 | Code is in the repo; what is left needs the public bucket, a first release and the production realm | 013 Done and the bucket (manual steps) |
| 023 | Buildable: being built ahead of 013. Closing it needs one production run | 013 Done, for the close only |
| 024, 026, 027 | Buildable: a connector needs only 023's snapshot model and `ISourceConnector`, which are done. Recorded fixtures and the close need sandbox accounts (manual steps) | The sandboxes, for the close |
| 025 | Connector (reading) done. Default mappings, fold, markup rewriting, keys, CLI and site page wait on 023's pipeline and surfaces | 023's mapping model, `MarkupConverter` and import CLI; the sandbox for the close |
| 032 | P3, and starts only on a production measure | v1 in prod and a trigger from the task |

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
| [011 Delivery plan view v1](completed/011-delivery-plan.md) | 1 | Done |
| [009 Search](completed/009-search.md) | 1 | Done |
| [008 Comments, attachments, activity feed](completed/008-comments-attachments.md) | 1 | Done |
| [012 Realtime updates](completed/012-realtime.md) | 1 | Done |
| [016 Workspace export](completed/016-workspace-export.md) | 1 | Done |
| [018 Members page and billing access](completed/018-members-billing-access.md) | 1 | Done |
| [021 Backup and restore rehearsal (local)](completed/021-backup-rehearsal.md) | 0 | Done |
| [017 Meeting agent AI budget](completed/017-meeting-agent-budget.md) | 2 | Done |
| [019 Public website](completed/019-public-site.md) | 1 | Done |
| [028 Sprint close review, split carry-over and burndown](completed/028-sprint-close-burndown.md) | 1 | Done |
| [029 Speed, first batch](completed/029-speed-first-batch.md) | 1 | Done |
| [031 Agent token budget](completed/031-agent-token-budget.md) | 1 | Done |
| [030 In-memory plan cache with FusionCache](completed/030-plan-cache.md) | 1 | Done |

## Rationale

- **013 early**: deploying right after auth catches hosting/backup problems while the app is small ([ADR-004](../docs/adr/ADR-004-hosting.md)).
- **005, 006 before UI**: agent parity is principle #1 ([ADR-001](../docs/adr/ADR-001-agent-parity.md)); the CI parity test in 006 guards every later task.
- **014 before UI**: budget enforced from the first endpoints, not retrofitted ([ADR-010](../docs/adr/ADR-010-performance-budget.md)).
- **010 is P1** only because 011 (the differentiator) depends on it.
- **015 is P1**: the meeting agent leads the public site ([ADR-017](../docs/adr/ADR-017-public-site.md)) and 017 depends on it.
- **Import is P1** ([ADR-019](../docs/adr/ADR-019-import.md)): teams with history in another tool don't switch
  without it. 023 is built ahead of 013, but closes only after one run against production storage and jobs; Jira and Azure DevOps
  (024, 025) are the largest switcher pools. Linear, Trello and the rest (026, 027) are P2.
- **P2 tasks** are independent of each other and can run in parallel once their dependencies are Done.

When adding a task: give it a row here with a priority, and move the row to Completed when its status becomes Done.
