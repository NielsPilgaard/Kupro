---
title: 'Task 027: Import from GitHub Issues, Monday and YouTrack'
description: >-
  Wave 3 connectors for the import framework: GitHub Issues (with sub-issues, issue types and Projects fields),
  Monday boards, and YouTrack (Cloud and self-hosted), with their default mappings.
status: 'In progress'
phase: 1
depends_on: [023]
purpose: 'Implementation spec for task 027.'
---

# Task 027: Import from GitHub Issues, Monday and YouTrack

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md); the pipeline is [task 023](023-import-framework.md). Three
connectors that can ship one at a time: each is a separate PR.

## Progress

GitHub Issues is in: `Kupro.Import/GitHub` (`GitHubConnector`, client), registered in `ImportSources` and as
`kupro import github [--url <ghes>] [--local]`, tested in `GitHubConnectorTests` against `FakeGitHub` and end to end in
`ImportCommitTests.GitHub`. Notes on what it does:

- The address is github.com unless `--url` names a GitHub Enterprise Server (its API under `/api/v3`). Pages follow
  `Link`; the primary rate limit waits for `x-ratelimit-reset`, a secondary one for `Retry-After`.
- Describe lists the token's repositories with issues on (key = repository name), org issue types (disabled ones
  left out, plus a plain "Issue" type), four statuses (open, closed, closed as not planned, closed as duplicate) and
  each repository's assignable users (bots flagged).
- The read holds one repository's issues at a time, so a sub-issue gets its parent from the parent's sub-issue list.
  Item key `owner/repo#89` keeps the number and is an alias, as is the issue URL. Pull requests are skipped. The first
  assignee is the assignee; with more, all logins go in the `assignees` field. Milestones are versions; dated ones
  become milestones by default.
- Comments come from the repository-wide comment list, events from the repository-wide event list: closed (with its
  reason), reopened, assigned, unassigned, renamed, milestoned and labeled. Label sets are worked out backwards from
  the labels now. Delta reads use `since` (issues, comments) and drop older events.

Not done: Projects (v2) fields (Status, Iteration, a number field), issue dependencies (blocked by), rewriting `#89`
in text to Kupro keys, the site page (waits on the wizard), and the recorded fixtures and live run (a sandbox, a manual
step).

YouTrack is in: `Kupro.Import/YouTrack` (`YouTrackConnector`, client), registered in `ImportSources` and as
`kupro import youtrack --url <address> [--local]`, tested in `YouTrackConnectorTests` against `FakeYouTrack`.

- The address is the one people open (`https://acme.youtrack.cloud`, `https://host/youtrack`); the API is under `/api`.
  Lists page with `$skip`/`$top`.
- Describe reads each project's custom fields: State values become statuses (resolved ones with category `done`),
  Type values item types (plain "Issue" for a project without Type), and the other enum, user, number, date and text
  fields become source fields. Archived projects are listed but read only when named.
- Items keep `PROJ-123`. Type, State, Priority, Assignee, Estimation (minutes → seconds) and Due Date fill the item;
  Spent time goes to the `spentTime` field (hours, details block only). Tags are labels. A Subtask link gives the
  parent; Depend becomes blocks, Duplicate duplicates, the rest relates, each link once. A file shown by name in
  Markdown points at its signed URL, so the import re-hosts it. Sprints come from agile boards; an issue in several
  sprints is in the latest.
- Activities (`activitiesPage`, custom fields and summary) are changes. A delta queries `updated:` a day early (the
  query reads dates in the token user's time zone) and keeps what changed since the cursor.
- Default mapping (`ImportMappingDefaults.YouTrack`): resolved states are Done, or Canceled when named for dropped work
  (Duplicate, Won't fix, Obsolete, Can't Reproduce, Incomplete); unresolved ones go by name.

Not done for YouTrack: the site page (waits on the wizard), an end-to-end commit test, and the recorded fixtures and
live run (a sandbox, a manual step). Monday is not started.

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
