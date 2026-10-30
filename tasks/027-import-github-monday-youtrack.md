---
title: 'Task 027: Import from GitHub Issues, Monday and YouTrack'
description: >-
  Wave 3 connectors for the import framework: GitHub Issues (with sub-issues, issue types and Projects fields),
  Monday boards, and YouTrack (Cloud and self-hosted), with their default mappings.
status: 'Todo'
phase: 1
depends_on: [023]
purpose: 'Implementation spec for task 027.'
---

# Task 027: Import from GitHub Issues, Monday and YouTrack

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md); the pipeline is [task 023](023-import-framework.md). Three
connectors that can ship one at a time: each is a separate PR.

## Scope

### GitHub Issues

- Access: fine-grained PAT with Issues (read) and Metadata (read), plus organization Projects (read) when a
  Projects (v2) board is mapped. Fine-grained PATs can't read user-owned projects, so those need a classic PAT with
  `read:project`. GitHub Enterprise Server by base URL.
- Repository → project (owner picks the key); issue number kept (`#89` → `API-89`), `owner/repo#89` and the URL as
  aliases, so the Git integration ([product.md](../docs/product.md)) resolves old references. Pull requests are skipped.
- Issue types (org-level) → type map; sub-issues → hierarchy with fit; without types, top-level issues with
  sub-issues → Feature.
- Open/closed + `state_reason` (completed, not planned) → Todo / Done / Canceled; a Projects (v2) Status field, if
  the owner picks a project, refines states, its Iteration field → sprints and a number field → effort.
- Labels → labels; milestones with a due date → milestones; assignees (first one → assignee, others in the details block).
- Comments, timeline events (labelled, assigned, closed, reopened, milestoned) → changelog. Markdown already.
- Rate limits: primary and secondary limits, `Retry-After` and `x-ratelimit-reset`.

### Monday

- Access: personal API token, GraphQL API, complexity budget per minute.
- Board → project; groups → Epic, label or nothing (owner picks); items → Tasks; subitems → Tasks under their item
  (item promoted to Feature) or a Markdown checklist (owner picks).
- Columns are untyped to Kupro: the wizard maps them. Status column → states (hand mapping of its labels); people
  column → assignee; date or timeline column → start/due; numbers column → effort or points; tags and dropdowns →
  labels; everything else → details block.
- Updates and replies → comments; activity logs → changelog where retained by Monday.
- New numbers per item, with the Monday item URL as alias.

### YouTrack

- Access: permanent token; YouTrack Cloud (`*.youtrack.cloud`) and self-hosted share the REST API.
- Project short name kept as the key (`PROJ-123`).
- Fields are per-project custom fields: Type, State (resolved flag drives Done/Canceled by name), Priority, Assignee,
  Estimation (period → hours), Spent time (details block only), Due Date.
- Links: subtask → hierarchy with fit, depends on → blocks, duplicates and relates map directly.
- Agile board sprints → sprints. Tags → labels.
- Comments, attachments, and `/api/issues/{id}/activities` → changelog. Text is Markdown already.

### Site

- `kupro.app/switch/<source>` page per connector (EN/DA).

## Acceptance criteria

- [ ] Recorded fixtures for each source import with no errors; plan and commit agree.
- [ ] GitHub: issue numbers kept, `owner/repo#89` resolves, PRs skipped, `not planned` → Canceled.
- [ ] Monday: column mapping drives states, dates and effort; subitem choice respected.
- [ ] YouTrack: keys kept, Estimation becomes hours, subtask trees fit ADR-008.
- [ ] One live run per source before marking Done, noted in the PR.

## Out of scope

- GitHub Discussions, pull requests, Projects (v2) views as anything but field sources.
- Monday dashboards, automations, docs. YouTrack Helpdesk and Knowledge Base.
