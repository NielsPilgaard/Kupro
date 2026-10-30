---
title: 'Task 023: Import framework, CSV and snapshot import'
description: >-
  The shared import pipeline every source plugs into: open snapshot format, saved mapping, dry-run plan, resumable
  commit, re-runs with per-field conflicts, placeholder members, key aliases, changelog replay, undo and finish. Ships
  with three sources that need no third-party API: snapshot upload, CSV and a Kupro export ZIP.
status: 'Todo'
phase: 1
depends_on: [013, 016]
purpose: 'Implementation spec for task 023.'
---

# Task 023: Import framework, CSV and snapshot import

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md). This task builds everything except the API connectors:
the pipeline, the wizard, people and placeholders, aliases, history replay, re-runs, undo, and three sources (snapshot
upload, CSV, Kupro export). Jira ([task 024](024-import-jira.md)) and Azure DevOps ([task 025](025-import-azure-devops.md))
then only add a connector, a markup converter and default mappings.

## Scope

### Snapshot format

- `Kupro.Import` class library, referenced by `Kupro.Api` and `Kupro.Cli`, AOT-safe (source-generated JSON).
- Snapshot model (`"format": 1`): `source` (kind, base URL), `projects`, `people` (source ID, name, email?),
  `itemTypes`, `statuses` (with source category), `fields` (ID, name, kind: text/number/date/select/multi-select/user),
  `items` (source ID, key?, project, type, status, title, description + markup kind, parent, assignee, creator,
  priority, labels, field values, estimate seconds?, points?, start/due, sprint, created/updated/resolved times),
  `relations`, `sprints`, `versions`, `comments`, `attachments` (references, fetched lazily), `changes` (source
  change ID, item, actor, time, field, from, to).
- Written and read as NDJSON parts inside a ZIP, so a 200k-issue snapshot streams instead of loading into memory.
- Public docs: `docs/import-format.md` with a JSON Schema in `docs/import-format.schema.json`; a test checks the
  schema against the model.
- `ISourceConnector` contract ([ADR-019](../docs/adr/ADR-019-import.md) IMP-003) and an in-memory fake connector for tests.

### Data (`Features/Imports`)

- Tables from ADR-019 IMP-001: `import`, `import_run`, `imported_item`, `issue_alias`. All tenant-owned and added to
  the export ([task 016](completed/016-workspace-export.md)) or to `ExportWriter.NotExported` with a reason (the
  encrypted token is not exported).
- Data Protection keys persisted to Postgres. The token is encrypted at rest and never returned by any endpoint.
- `MemberStatus.Placeholder` with `Member.DisplayName` and optional `Member.PlaceholderEmail`. Seat count, guest cap,
  idle-seat flag and authorization ignore placeholders (they can't sign in). Members page shows them in their own
  group with "Invite" and "Merge into…" ([ADR-013](../docs/adr/ADR-013-seats-billing.md)).
- Merge: moves every reference (assignee, creator, comment author, activity actor, sprint/plan rows if any) in one
  transaction, then deletes the placeholder row. A test fails if a table with a member foreign key is not covered.

### Pipeline

- **Describe**: the connector returns projects, people, types, statuses and fields for the wizard.
- **Mapping** (saved JSON on `import`): project targets (new / existing / merge), people, type map, state map,
  priority map, which fields become labels, which versions become milestones, effort (hours per point, or none),
  scope filter (projects; closed items older than N months, default all), ADO-style level folding.
- **Plan** (dry run): runs the whole mapping and hierarchy fit without writing, and reports counts, conflicts,
  re-parented items, folded checklists, skipped items, attachment count and bytes.
- **Commit**: `ImportJob`, in its own queue: one import per workspace at a time, `Imports:MaxConcurrent` across
  workspaces (default 2). Batches of 500 items, resumable from `imported_item` after a restart. Honours `Retry-After`.
- Writes go through the feature services' bulk paths (ADR-019 IMP-002) with explicit actor, timestamps and
  `Surface.Import`. Realtime events coalesce per batch. Search vectors update via the stored computed column.
- **Hierarchy fit**, **states**, **fields → labels/details block**, **effort**, **keys and aliases**, **sprints per
  project**, **mention and link rewriting**: as in ADR-019, implemented once here and driven by the mapping.
- **Markdown**: a `MarkupConverter` interface (ADF, Jira wiki, HTML implementations come with their connectors). This
  task ships the HTML-to-Markdown and plain-text ones.
- **History**: comments, attachments (copied to object storage, inline images rewritten to `attachment:ID`),
  original timestamps, changelog replay (`updated` for mapped fields, `source_changed` for the rest, description
  edits as a marker only), one `imported` entry per issue, `reimported` entries with diffs on re-runs.
- **Re-runs**: delta from the saved cursor with a 10-minute overlap; per-field "Kupro wins" using
  `imported_item.last_values`; conflicts, source deletions and alias collisions in the run report.
- **Auto-sync**: an hourly scheduler for imports with the flag on; turns itself off after 30 days with no change on
  either side and emails the owner (EN/DA).
- **Undo** (until Finish): deletes everything the import created, listed per entity in `imported_item`. Warns with the
  list of issues edited in Kupro since. Refused, with the list, while native comments, attachments, relations or child
  issues reference imported issues ([ADR-019](../docs/adr/ADR-019-import.md)). **Finish**: stops auto-sync, deletes
  the token, keeps aliases and history. Tokens are also deleted after 90 days without a run.

### Issue feed and lookups

- Activity feed gets cursor paging ("Show older") instead of the fixed 500 limit, on REST, CLI and web. Keeps the
  latency budget ([ADR-010](../docs/adr/ADR-010-performance-budget.md)) on an issue with 5,000 entries.
- `FeedItemKind.Imported` and `SourceChanged`. The web renders "via Jira import" and source field names as given.
- Issue lookup by key falls back to `issue_alias` (exact match on key, URL or `#id`). Search and the command palette
  match aliases. A request for an alias key returns the issue (no redirect dance for the CLI).

### Sources in this task

- **Snapshot upload**: a snapshot ZIP from the CLI's `--local` mode, an agent or a customer script.
- **CSV**: upload, delimiter/encoding detection (UTF-8 with or without BOM, Windows-1252), header row, column-mapping
  step (title, description, type, status, priority, assignee name/email, labels, parent key, estimate, points,
  start, due, created, key). Known header sets (Jira CSV export, Linear CSV export) are pre-mapped. Re-runnable when
  a key or ID column is mapped.
- **Kupro export ZIP** ([task 016](completed/016-workspace-export.md), format 1): moves a workspace, or a self-hosted
  one, into another. Keys, members (as placeholders or matched), sprints, plan data, comments, attachments and
  activity come across as stored.

### Surfaces

- REST under `/workspaces/{ws}/imports`: create (source kind, base URL, token or upload), describe, get/put mapping,
  plan, run, list runs, get report, set auto-sync, undo, finish. Owners only; agent tokens 403; other workspaces 404.
- CLI: `kupro import csv <file>`, `kupro import snapshot <file>`, `kupro import kupro <export.zip>`, `kupro import
  list`, `kupro import plan <id>`, `kupro import run <id> [--no-wait]`, `kupro import report <id>`, `kupro import
  mapping <id> [--set file.json]`, `kupro import undo <id>`, `kupro import finish <id>`. The People step is a mapping
  file: `kupro import mapping <id>` prints the proposed one.
- MCP: one read-only `imports` tool (list, plan, report, conflicts). Tool budget 22 → 23 (ADR-001 IMP-003).
- Web: Settings → Import (owners), a wizard: Source → Connect → Scope → People → Types & states → Fields & effort →
  Plan → Import → Report. The empty state of a new workspace offers "Coming from Jira or Azure DevOps? Import". All
  strings in the locale files (EN/DA).

### Ops

- Grafana: an import dashboard panel (runs, items/s, failures) and an alert on object-storage growth per day
  (`infrastructure/observability/`, [ADR-014](../docs/adr/ADR-014-observability.md)).
- Deploy runbook: `Imports:MaxConcurrent`, Data Protection key storage.

## Acceptance criteria

- [ ] A CSV of 1,000 rows with parents, labels and estimates imports through web and CLI. The plan matches what commit
      writes, and the parity test passes (MCP: read-only tool).
- [ ] A Kupro export ZIP imported into an empty workspace reproduces issues, keys, comments, attachments, sprints and
      plan data (round-trip test against task 016's writer).
- [ ] Hierarchy fit: a fixture with four levels and a Task with children produces only valid ADR-008 shapes. Every
      re-parented item has a `relates` relation to its old parent and is counted in the plan.
- [ ] Re-run: a source change to a field untouched in Kupro is applied; one edited in Kupro is kept and reported as a
      conflict; nothing is duplicated; a native issue taking the next number causes an alias, not a failure.
- [ ] Placeholders: no seat, can't sign in, count in no cap. Inviting one keeps its history. Merging one moves every
      reference (guard test over member foreign keys).
- [ ] Changelog replay: a fixture's state changes show in the issue feed as normal updates with source actor and
      time; unmapped field changes show as source changes. Feed paging returns all 5,000 entries of a large issue
      within budget.
- [ ] An alias key works in `GET /issues/{key}`, search, the command palette and `kupro issue view`.
- [ ] A commit killed mid-run resumes without duplicates. A second import in the same workspace waits.
- [ ] Undo removes everything the import created and nothing else; it is refused while native records reference
  imported issues. Finish deletes the token (checked in the DB).
- [ ] Tenancy test covers every import table and endpoint ([ADR-009](../docs/adr/ADR-009-multi-tenancy.md)).
- [ ] A Danish CSV in Windows-1252 keeps æøå.

## Out of scope

- API connectors (tasks 024–027).
- OAuth "Connect" buttons.
- Writing back to the source (comments, "moved to Kupro" links).
- Two-way sync, webhooks.
