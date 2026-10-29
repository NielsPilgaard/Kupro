---
title: 'ADR-019: Import from other trackers'
status: 'Accepted'
date: '2026-10-28'
authors: 'Niels'
tags: ['import', 'migration', 'domain', 'integrations']
supersedes: ''
superseded_by: ''
description: >-
  Teams switch to Kupro through a re-runnable, one-way import: server-side connectors (Jira, Azure DevOps first) and
  CSV turn a source into one open snapshot format, the owner maps people, types, states and fields in a wizard, and
  each run applies a previewed plan. Source keys, comments, attachments, timestamps and the changelog are kept;
  Kupro edits win over later source changes. The work item model stays unchanged.
---

# ADR-019: Import from other trackers

## TL;DR

- One-way and re-runnable until the owner clicks **Finish migration**: run it again (or hourly) and it pulls what changed, matched by source ID.
- Pipeline: **connector → snapshot (open JSON format) → mapping (owner choices, saved) → plan (dry run) → commit**. Connectors run in the API, or in the CLI for servers behind a VPN.
- Source users become members or **placeholder** members. Source keys (`PROJ-123`) are kept. Anything that can't be kept is reachable through an alias table.
- Kept: comments, attachments, original timestamps, and the changelog replayed into the activity feed. Unmapped fields go to labels or a details block. [ADR-008](ADR-008-work-item-model.md) is not changed.
- Wave 1: Jira (Cloud + Data Center), Azure DevOps (Services + Server), CSV, Kupro export. Wave 2: Linear, Trello. Wave 3: GitHub Issues, Monday, YouTrack.

## Status

**Accepted** — decided with the owner 2026-10-28.

## Context

The pitch is "a European alternative to Jira, Azure DevOps, Linear, Trello and Monday" ([product.md](../product.md)).
Teams with years of history in another tool will not switch if they have to start from scratch, and they will not trust
a one-shot import they can't rehearse. Atlassian ends Data Center on 28 March 2029, so its self-hosted customers
(public sector in particular) must move somewhere before then.

Sources are far more flexible than Kupro: custom types and workflows, four-level hierarchies, story points, custom
fields. Kupro deliberately is not ([ADR-008](ADR-008-work-item-model.md)). So the import has to squeeze a source into
Kupro's shape, show the owner how before writing anything, and never silently lose data.

## Decision

### Mode

- **One-way, re-runnable.** Kupro only reads from the source. Each run matches items by source ID and updates or
  creates them, so a team can run both tools for weeks and switch when ready.
- **Delta runs**: after the first run, a connector reads only items changed since the last cursor (with a 10-minute
  overlap for eventually consistent search APIs). Upserts make the overlap harmless.
- **Cadence**: "Sync now" (web, CLI), plus an opt-in hourly auto-sync until Finish. Auto-sync turns off after 30
  days without a change on either side, with an email to the owner. No webhooks.
- **Finish migration** ends the import: auto-sync stops, the stored source token is deleted, and undo is no longer offered.

### Pipeline

1. **Connector** reads the source through its API and writes a **snapshot** in the Kupro import format: projects,
   people, items (source type, status, fields as typed values), hierarchy, relations, sprints/iterations, versions,
   comments, attachment references, change history. The format is public and versioned (`"format": 1`), so the CLI,
   an agent or a third-party script can produce a snapshot, and Kupro accepts one uploaded directly.
2. **Mapping**: the owner's choices (people, projects, types, states, fields, effort factor, scope filter), with
   defaults the connector proposes. Saved on the import and reused on every run. Changing it applies from the next run.
3. **Plan**: a dry run against current Kupro data: counts to create/update, re-parented items, conflicts, skipped
   items, attachment volume. Shown before the first commit and in every run report.
4. **Commit**: applies the plan in batches, resumable after a restart, like `ExportJob` ([task 016](../../tasks/completed/016-workspace-export.md)).

Connectors live in a shared `Kupro.Import` library referenced by the API and the CLI. It must be AOT-safe
(source-generated JSON, no reflection) because the CLI is AOT ([ADR-018](ADR-018-cli-distribution.md)).

### Source access

- **Personal API tokens**, pasted in the wizard with per-source "where to get it" steps: Jira Cloud (email + API
  token), Jira Data Center (personal access token), Azure DevOps (PAT, Work Items read), YouTrack (permanent token),
  Linear (API key), Monday (API token). Trello uses one Kupro API key and the user's click on Trello's authorize link.
- OAuth "Connect" buttons are a later polish, not a requirement.
- Tokens are encrypted with ASP.NET Data Protection (keys persisted in Postgres) and deleted on Finish, on Undo, or
  after 90 days without a run.
- **Behind a VPN**: `kupro import <source> --local` runs the same connector on the user's machine and uploads the
  snapshot. The token never reaches Kupro.

### People

- The wizard's People step matches every source user to a member by email, then by name. Per user the owner picks: an
  existing member, invite (email sent on commit, opt-in per person), or **placeholder**.
- **Placeholder** is a new member status ([ADR-013](ADR-013-seats-billing.md)): a display name and optional email,
  no login, no seat, not counted in the guest cap. It can be an assignee, author and actor like any member.
- Inviting a placeholder turns that same row into `invited`, then `active`, so its history follows. If the person is
  already a member, the owner merges the placeholder into them, and every reference moves.

### Shape

- **Projects**: one source project (Jira project, ADO project, Linear team, Trello board, Monday board) → one Kupro
  project by default. The owner can merge several into one or target an existing project.
- **Types**: per-source default map, editable (e.g. Jira Epic → Epic, Story/Task → Task, Bug → Task + `bug` label;
  ADO Epic → Epic, Feature → Feature, User Story/PBI/Requirement → Task).
- **Hierarchy fit**: after mapping, a Task with children is promoted to Feature when its parent allows (Jira
  Epic > Story > Sub-task becomes Epic > Feature > Task). An item still too deep goes under its nearest valid
  ancestor, with a `relates` relation to its old parent. For ADO's four levels the owner picks which level folds
  (default: ADO Task becomes a Task under the Feature, related to its Story). Trello checklist items and Monday
  subitems without their own data become a Markdown checklist in the description. The plan counts every re-parented item.
- **States**: mapped by the source's own category where it has one (Jira status category, ADO state category, Linear
  state type, YouTrack resolved flag), refined by name hints ("review" → In Review, "won't do" → Canceled). Trello
  lists and Monday status labels are mapped by hand in the wizard. All defaults are editable.
- **Priority**: per-source default map, editable.
- **Other fields**: the owner ticks which select-type fields (components, area paths, custom selects) become labels.
  A source version or release with a date becomes a milestone. Everything else goes into a collapsed "Imported from
  <source>" block at the end of the description: a field/value table and a link to the original. Nothing is dropped.
- **Effort**: source time estimates first (Jira original estimate, ADO Original Estimate, YouTrack Estimation).
  Otherwise points × an "hours per point" factor the owner must enter (no default). Blank imports no effort. The plan
  shows total hours against project capacity. Original points go into the details block.
- **Sprints**: Kupro sprints are per project ([ADR-008](ADR-008-work-item-model.md)). A source sprint spanning
  projects becomes one sprint per project with the same name and dates. ADO iteration paths → sprints.
- **Markup**: Jira ADF (Cloud), Jira wiki markup (Data Center) and ADO HTML convert to Markdown. A construct the
  converter doesn't know is kept as a fenced block, not dropped. `@mentions` are rewritten through the People map,
  links to source issues are rewritten to Kupro keys, and inline images point to re-hosted attachments.

### Keys

- Sources with project keys (Jira, Linear, YouTrack) keep them: `PROJ-123` stays `PROJ-123`, and
  `Project.IssueCounter` is set to the highest imported number.
- Sources with numbers but no key keep the number under the project key the owner picks: ADO `#4567` becomes
  `WEB-4567` (ADO IDs are unique per organisation, so they can't collide within a project), a GitHub issue `#89`
  becomes `API-89`, a Trello card keeps its board number. Monday items get new numbers.
- **Alias table** (`issue_alias`: source key, URL or ID → issue) for every source reference: the bare `#4567`,
  `owner/repo#89`, source URLs, a source key longer than Kupro allows, and a re-run collision (a native
  `PROJ-124` created between runs makes the import take the next free number and record the alias).
- Issue lookup by key, search, the command palette, `kupro issue view` and later the Git integration resolve aliases.

### History

- Comments with original author and time. Attachments and inline images are copied into Kupro's object storage
  ([ADR-004](ADR-004-hosting.md)): source links die when the source subscription ends.
- Original created, updated and completed times are kept.
- **Changelog replay**: source changes to fields Kupro has (state, assignee, title, priority, parent, sprint,
  effort, dates, labels) become normal `updated` activity entries, translated through the mapping. Changes to fields
  Kupro lacks become `source_changed` entries with the raw field name and from/to text. Description edits are
  recorded as "description edited", without the text. Actors go through the People map.
- Every imported entry is tagged with a new `Surface.Import` ("via Jira import"). Each issue also gets one `imported`
  entry. Re-runs add only changes they haven't seen before (by source change ID).

### Re-runs and conflicts

- Each imported item remembers the last value imported per field. A re-run updates a field only if Kupro still holds
  that value. If someone changed it in Kupro, Kupro wins and the run report lists the conflict with both values.
- New source comments and changes are appended. An item deleted in the source is left alone and flagged in the report.
- **Undo import** deletes what the import created: issues, comments, attachments, labels and milestones it made, and
  placeholders nothing else references. Offered until Finish, with a warning listing issues edited in Kupro since.

### Who and where

- **Owners only**, like export: an import stores a source token and creates members.
- **Surfaces** ([ADR-001](ADR-001-agent-parity.md)): web wizard and REST do everything; CLI `kupro import ...` does
  everything except the interactive People step, which it takes as a mapping file. MCP gets one read-only `imports`
  tool (list imports, read plans, run reports and conflicts) so an agent can help triage. Starting a run and handling
  tokens stay off MCP, so secrets never enter a model's context.
- **No size limits**. One import runs at a time per workspace, in its own job queue with capped concurrency across
  workspaces, so a large import can't starve exports or the meeting agent. Throttling follows each source's rate
  limits (`Retry-After`).
- **Licensing**: AGPL core ([ADR-007](ADR-007-licensing.md) IMP-002: nothing is enterprise until a paying customer asks).
- **Vendors**: a source the customer connects is not a Kupro vendor: Kupro only reads from the customer's own account,
  and no Kupro or customer data is sent to it. It is not added to the [ADR-006](ADR-006-eu-vendor-policy.md) register.

### Sources

| Wave | Sources | Priority |
|---|---|---|
| 1 | Snapshot upload, CSV (column mapping), Kupro export ZIP ([task 016](../../tasks/completed/016-workspace-export.md)) | P1, [task 023](../../tasks/023-import-framework.md) |
| 1 | Jira Cloud + Jira Data Center | P1, [task 024](../../tasks/024-import-jira.md) |
| 1 | Azure DevOps Services + Server | P1, [task 025](../../tasks/025-import-azure-devops.md) |
| 2 | Linear, Trello | P2, [task 026](../../tasks/026-import-linear-trello.md) |
| 3 | GitHub Issues, Monday, YouTrack | P2, [task 027](../../tasks/027-import-github-monday-youtrack.md) |

## Consequences

### Positive

- **POS-001**: A team can rehearse the switch on real data, keep working in the old tool, and cut over when ready.
- **POS-002**: Old keys, links and history keep working, so the switch doesn't break commit messages, docs or chat.
- **POS-003**: The open snapshot format makes every future source, and any customer's own script, a converter
  away. It also covers self-hosted sources behind a VPN without storing their tokens.
- **POS-004**: The delivery plan has data on day one: effort, dates, hierarchy and milestones come across.

### Negative

- **NEG-001**: Changelog replay grows the activity table by 10–50× for imported issues. The issue feed needs cursor
  paging, since it currently keeps only the 500 most recent entries ([task 008](../../tasks/completed/008-comments-attachments.md)).
- **NEG-002**: No size limits means storage cost scales with what trials import. An object-storage growth alert is
  the brake ([ADR-014](ADR-014-observability.md)).
- **NEG-003**: Each connector tracks a moving third-party API (Jira Cloud's search endpoint moved in 2025). Fixtures
  in CI don't catch that, so each connector needs a periodic live check against a sandbox.
- **NEG-004**: Placeholder members add a member status that every member query and the seat count must handle.

## Alternatives Considered

### Two-way live sync

- **ALT-001**: **Description**: Changes flow both ways while teams coexist (like Unito or Exalate).
- **ALT-002**: **Rejection Reason**: Kupro's fixed workflow and missing custom fields can't round-trip, conflicts need
  resolution UX, and every source needs webhooks. Months per source for a temporary phase.

### CLI-only or file-upload-only import

- **ALT-003**: **Description**: Convert on the user's machine, or accept only the source's own export files.
- **ALT-004**: **Rejection Reason**: Requires installing the CLI, which is too much for Trello and Monday teams. Source
  export files are lossy (no comments, attachments or history in most) and can't be re-run. Kept as fallbacks
  (`--local`, CSV, snapshot upload).

### Allow Task sub-tasks in the model

- **ALT-005**: **Description**: Change ADR-008 so a Task can have Task children, matching Jira sub-tasks and ADO tasks.
- **ALT-006**: **Rejection Reason**: A model change made for import, touching roll-ups, board, plan and agent
  validation. The fit algorithm keeps the grouping through promotion and relations.

### Invite every source user, or collapse them to the importer

- **ALT-007**: **Rejection Reason**: Invites spam people who left years ago and fail without emails (Jira Cloud and
  Trello often hide them). Collapsing loses assignees and misattributes history.

## Implementation Notes

- **IMP-001**: Tables, all tenant-owned ([ADR-009](ADR-009-multi-tenancy.md)): `import` (source kind, base URL,
  encrypted token, mapping JSON, state `active`/`finished`/`undone`, auto-sync flag, delta cursor), `import_run`
  (kind dry-run/commit, state, counts, report JSON), `imported_item` (import, source kind + source ID → Kupro
  entity kind + ID, last imported field values JSON, seen change IDs high-water mark), `issue_alias`.
  `imported_item` has a unique `(workspace_id, import_id, source_kind, source_id)` index.
- **IMP-002**: Writes go through the feature services' bulk paths, not raw inserts, so rules and tenancy hold
  (AGENTS.md rule 2). The services take explicit timestamps and actors for imported rows. Realtime events
  ([task 012](../../tasks/completed/012-realtime.md)) are coalesced per batch: one "issues changed" per project, not one per issue.
- **IMP-003**: Connector contract: `ISourceConnector` with `DescribeAsync` (projects, people, types, statuses, fields
  for the wizard), `ReadAsync(scope, cursor)` streaming snapshot parts, and `FetchAttachmentAsync`. Every connector
  is tested against recorded API fixtures; no live calls in CI.
- **IMP-004**: Jira Cloud search uses `/rest/api/3/search/jql` (token pagination, eventually consistent, so the
  delta overlap). An ADO WIQL query returns at most 20,000 IDs, so connectors query in ID ranges.
- **IMP-005**: The MCP tool count goes from 22 to 23 with `imports` (ADR-001 IMP-003).

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md) — surfaces, the read-only MCP tool, tool budget
- [ADR-008](ADR-008-work-item-model.md) — the fixed model imports fit into, unchanged
- [ADR-009](ADR-009-multi-tenancy.md) — import tables are tenant-owned
- [ADR-013](ADR-013-seats-billing.md) — placeholder member status, no seat
- [ADR-006](ADR-006-eu-vendor-policy.md) — import sources are not vendors
- [ADR-018](ADR-018-cli-distribution.md) — the AOT CLI that also runs connectors locally

## References

- **REF-001**: [Atlassian Data Center end of life](https://www.atlassian.com/licensing/data-center-end-of-life) — 28 March 2029.
- **REF-002**: [Azure DevOps work tracking limits](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits) — 20,000 query results.
