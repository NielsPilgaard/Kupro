---
title: 'Task 023: Import framework, CSV and snapshot import'
description: >-
  The shared import pipeline every source plugs into: open snapshot format, saved mapping, dry-run plan, resumable
  commit, re-runs with per-field conflicts, placeholder members, key aliases, changelog replay, undo and finish. Ships
  with three sources that need no third-party API: snapshot upload, CSV and a Kupro export ZIP.
status: 'In progress'
phase: 1
depends_on: [016]
purpose: 'Implementation spec for task 023.'
---

# Task 023: Import framework, CSV and snapshot import

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md). This task builds everything except the API connectors:
the pipeline, the wizard, people and placeholders, aliases, history replay, re-runs, undo, and three sources (snapshot
upload, CSV, Kupro export). Jira ([task 024](024-import-jira.md)) and Azure DevOps ([task 025](025-import-azure-devops.md))
then only add a connector, a markup converter and default mappings.

**State**: building ahead of 013 (the owner relaxed the dependency on 2026-11-06). Nothing in the code needs
production; local S3 and Testcontainers cover it. Closing the task still needs 013: one large import run on production
storage and jobs (last item in Acceptance criteria).

**Progress**: the snapshot format is done: `Kupro.Import` (model, chunked NDJSON ZIP writer and reader,
`ISourceConnector`, the snapshot-upload connector), [import-format.md](../docs/import-format.md) with its schema, and
`SnapshotTests`. Data is done too: the four import tables (`Features/Imports`, class `WorkspaceImport` for `import`,
since `Import` collides with the `Kupro.Import` namespace), `ImportTokens` with Data Protection keys in their own bucket, and
placeholder members with invite and merge on REST, CLI and the Members page. Pipeline has started: the mapping with its
defaults, `HierarchyFit` and `ImportPlanner` (counts and samples over a snapshot, nothing written), then the first
commit: the stored snapshot, `ImportJob` with its own queue, `ImportCommit` (projects, people, labels, sprints,
milestones, keys and aliases, issues with the details block, relations; resumable) and `MarkupConverters` (HTML, plain).
History is in as well: comments, attachments, changelog replay and mention/link rewriting (`ImportCommit.History`,
`ImportReferences`). An attachment whose bytes the snapshot doesn't hold is counted and keeps its source link until the
server-side connectors fetch it. Re-runs work too: per-field updates against `imported_item.last_values`, `import_conflict`
with Keep/Use source, deltas that rebuild the tree from stored parents, and a `partial` flag in the format so only a whole
snapshot reports source deletions. A delta must list a holder together with its checklist items. No surface reaches any
of this yet: `ImportService` is called from tests. The plan runs a re-run's own comparison on a context it never saves,
so its counts are the commit's. Invites go out when a commit ends, the owner is emailed (EN/DA) after the first commit
and after a failed one (a failed plan only shows where it was asked for), and Undo, Finish and the 90-day token purge
are in `ImportService`/`ImportJob`. Imports with a token run their connector (`ImportSources`: Azure DevOps, Linear,
Trello) into a fresh snapshot with files when a commit starts, from the cursor less a 10-minute overlap; a rejected
token fails the run and pauses auto-sync until it is replaced; auto-sync queues hourly and turns itself off after 30
quiet days. Trello needs Kupro's API key in `Imports__TrelloKey`, not yet an AppHost parameter (slice 6, with the
runbook). The feed shows `imported`, `reimported` and `sourceChanged` items with the source and a skipped person's
source name ("via Jira import", web too), and a source key or URL finds its issue in `GET /issues/{key}`, the feed,
`get_issue` and search (so the palette). The feed pages: `GET .../activity?before=<oldest item id>&limit=`, `kupro issue activity --before`, and "Show older" on the web. Next: slice 4 (CSV, Kupro export ZIP).

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
- Data Protection keys persisted to their own Object Storage bucket (`KeyStorage__*`), never Postgres. The token is
  encrypted at rest and never returned by any endpoint. Without key storage `ImportTokens.Enabled` is false and
  importing with a token answers 503, like attachments without storage.
- `MemberStatus.Placeholder` with `Member.DisplayName` and optional `Member.PlaceholderEmail`. Seat count, guest cap,
  idle-seat flag and authorization ignore placeholders (they can't sign in). Members page shows them in their own
  group with "Invite" and "Merge into…" ([ADR-013](../docs/adr/ADR-013-seats-billing.md)).
- Merge: moves every reference (assignee, creator, comment author, activity actor, sprint/plan rows if any) in one
  transaction, then deletes the placeholder row. A test fails if a table with a member foreign key is not covered.

### Pipeline

- **Describe**: the connector returns projects, people, types, statuses and fields for the wizard.
- **Mapping** (saved JSON on `import`): project targets (new / existing / merge), people (member / invite /
  placeholder / skip), type map, state map, priority map, which fields become labels, which versions become
  milestones, effort (hours per point, or none), scope filter (projects; closed items older than N months, default
  all), and `fold` (`bottom` / `top`) for four-level sources ([ADR-019](../docs/adr/ADR-019-import.md) Shape).
- **Plan** (dry run): an `import_run` of kind dry-run over the stored snapshot, so re-planning after a mapping edit
  doesn't read the source. Runs the whole mapping and hierarchy fit without writing, and reports counts, conflicts,
  re-parented items, folded checklists, skipped items, statuses flagged for a look, attachment count and bytes, with
  up to 5 sample items per count. For a four-level source it reports counts for both `fold` values.
- **Commit**: `ImportJob`, in its own queue: one import per workspace at a time, `Imports:MaxConcurrent` across
  workspaces (default 2). Batches of 500 items, resumable from `imported_item` after a restart. Honours `Retry-After`.
- Writes go through the feature services' bulk paths (ADR-019 IMP-002) with explicit actor, timestamps and
  `Surface.Import`. Realtime events coalesce per batch. Search vectors update via the stored computed column. Invalidate
  the plan cache after each commit ([ADR-010](../docs/adr/ADR-010-performance-budget.md) IMP-003).
- **Hierarchy fit**, **states**, **fields → labels/details block**, **effort**, **keys and aliases**, **sprints per
  project**, **mention and link rewriting**: as in ADR-019, implemented once here and driven by the mapping.
- **Markdown**: a `MarkupConverter` interface (ADF, Jira wiki, HTML implementations come with their connectors). This
  task ships the HTML-to-Markdown and plain-text ones.
- **History**: comments, attachments (copied to object storage, inline images rewritten to `attachment:ID`),
  original timestamps, changelog replay (`updated` for mapped fields, `source_changed` for the rest, description
  edits as a marker only), one `imported` entry per issue, `reimported` entries with diffs on re-runs.
- **Re-runs**: delta from the saved cursor with a 10-minute overlap; per-field "Kupro wins" using
  `imported_item.last_values`; conflicts, source deletions and alias collisions in the run report.
- **Conflicts**: stored per item and field until resolved: Keep Kupro's or Use source. A resolved conflict reopens
  only when the source value changes again.
- **Skipped people**: one shared placeholder per import authors their comments and changes, with the source name kept
  on each entry; their assignments are left empty.
- **Rejected token**: the run fails, auto-sync pauses, the owner is emailed; a replaced token resumes from the cursor.
- **Email** (EN/DA): first commit finished, any run failed, auto-sync turned off.
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
  plan, run, list runs, get report, set auto-sync, replace token, list and resolve conflicts, undo, finish. Owners
  only; agent tokens 403; other workspaces 404.
- CLI: `kupro import csv <file>`, `kupro import snapshot <file>`, `kupro import kupro <export.zip>`, `kupro import
  create <source> --url <url>` (a server-side connector import; token from stdin or `KUPRO_IMPORT_TOKEN`, never an
  argument), `kupro import list`, `kupro import plan <id>`, `kupro import run <id> [--no-wait]` (also "sync now"),
  `kupro import report <id>`, `kupro import mapping <id> [--set file.json]`, `kupro import sync <id> --auto on|off`,
  `kupro import token <id>` (stdin), `kupro import conflicts <id> [--keep <conflict> | --use-source <conflict>]`,
  `kupro import undo <id>`, `kupro import finish <id>`. The People step is a mapping file: `kupro import mapping <id>`
  prints the proposed one. Each connector task adds `kupro import <source> --local`.
- MCP: one read-only `imports` tool (list, plan, report, conflicts). Tool budget 19 → 20 (ADR-001 IMP-003). Starting
  runs, resolving conflicts and tokens stay off MCP (ADR-019 Who and where); the parity test records the opt-out.
- Web: Settings → Import (owners). **The layout waits on the owner's pick in [task 033](033-import-ui-direction.md)**;
  don't write `web/` code for it before then. Whatever the direction, it covers: source, connect, scope, people,
  types & states (with the `fold` choice as two equal options for four-level sources), fields & effort, plan,
  import, report, conflicts, and the list of imports with Sync now, auto-sync, Undo and Finish. Command palette:
  "Import from…", "Sync import now", "Open import report". The empty state of a new workspace offers "Coming from
  Jira or Azure DevOps? Import". All strings in the locale files (EN/DA).

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
- [x] Re-run: a source change to a field untouched in Kupro is applied; one edited in Kupro is kept and reported as a
      conflict; nothing is duplicated; a native issue taking the next number causes an alias, not a failure.
- [x] Conflicts: Use source applies the source value, Keep Kupro's closes it; a resolved conflict reopens only when
      the source value changes again.
- [ ] Four levels: `fold` bottom and top both produce valid ADR-008 shapes and match their plan counts; with top, the
      top-level items exist as Epics with their comments, history and aliases.
- [x] A skipped person's comments and changes exist, authored by the shared placeholder with the source name kept.
- [x] Placeholders: no seat, can't sign in, count in no cap. Inviting one keeps its history. Merging one moves every
      reference (guard test over member foreign keys).
- [x] Changelog replay: a fixture's state changes show in the issue feed as normal updates with source actor and
      time; unmapped field changes show as source changes. Feed paging returns all 5,000 entries of a large issue
      within budget.
- [ ] An alias key works in `GET /issues/{key}`, search, the command palette and `kupro issue view`.
- [x] A commit killed mid-run resumes without duplicates. A second import in the same workspace waits.
- [x] Undo removes everything the import created and nothing else; it is refused while records it didn't create
  (native or from another import) reference its issues. Finish deletes the token (checked in the DB).
- [ ] Tenancy test covers every import table and endpoint ([ADR-009](../docs/adr/ADR-009-multi-tenancy.md)).
- [ ] A Danish CSV in Windows-1252 keeps æøå.
- [ ] After 013: one import of 10,000+ items with attachments runs on production, survives a container restart
      mid-commit, and the storage-growth alert stays quiet. Noted in the PR.

## Out of scope

- API connectors (tasks 024–027).
- OAuth "Connect" buttons.
- Writing back to the source (comments, "moved to Kupro" links).
- Two-way sync, webhooks.
- Defaulting bots to skip ([ADR-019](../docs/adr/ADR-019-import.md) People): the format has no bot flag, so they propose
  as placeholders and the owner skips them by hand. The owner put this off on 2026-11-08.
