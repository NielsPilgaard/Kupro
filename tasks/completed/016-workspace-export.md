---
title: 'Task 016: Workspace export'
description: >-
  Owners export a whole workspace (JSON, CSV and attachment files in one ZIP) through web, REST and CLI; MCP opts out.
  Backs the data-portability promise in the terms, privacy policy and DPA.
status: 'Done'
phase: 1
depends_on: [003, 004, 008]
purpose: 'Implementation spec for task 016.'
---

# Task 016: Workspace export

## TL;DR

The draft terms, privacy policy and DPA ([public pages](../../docs/design/public-pages/index.html)) promise that owners
can export everything, at any time and for 30 days after a subscription ends. GDPR data portability and the DPA's
end-of-processing clause lean on it. Built: one ZIP per export, built in the background, downloadable for 7 days.

## Scope (as built)

- **Who**: workspace owners only. Agent tokens are capped at Member, so agents can't take a copy of the workspace.
- **What**: one ZIP with
  - `workspace.json` (`"format": 1`): the workspace and every workspace table, written as stored, so new fields are
    exported automatically. `users` holds only the name and email of the members who are people.
  - `issues.csv` and `comments.csv`: UTF-8 with BOM, CRLF, RFC 4180 quoting. Fields starting with `= + - @` get a leading
    apostrophe so spreadsheets don't run them as formulas.
  - `attachments/<issue key>/<attachment id>-<file name>`. A file missing from storage is listed in
    `attachments/MISSING.txt` instead of failing the export.
  - `README.txt`.
  - Transcripts are included only if the workspace keeps them after a meeting
    ([task 015](../015-meeting-agent.md)). API tokens, billing data and the export records are not.
- **How** (`Features/Exports`):
  - `POST /workspaces/{ws}/exports` starts one (202), or returns the one already building.
  - `GET …/exports` lists them, and `GET …/exports/{id}` gives the state plus a 15-minute presigned download link.
  - `ExportJob` builds waiting exports one at a time, and resumes them after a restart. It writes a temp file, uploads it
    to object storage and emails the owner (EN/DA).
  - Exports are deleted 7 days after they start.
- **Surfaces**:
  - Web: Settings, Export section per owned workspace.
  - CLI: `kupro workspace export [-o file.zip] [--no-wait]` and `kupro workspace exports`.
  - MCP opts out: the result is a file to save, and the coarse tool cap ([ADR-001](../../docs/adr/ADR-001-agent-parity.md) IMP-003) is reached.
- **After the subscription ends**: export only needs the Owner role, not write access, so it keeps working once billing
  makes workspaces read-only.

## Acceptance criteria

- [x] An owner can start, list and download an export through web, REST and CLI; parity test passes (MCP opt-out).
- [x] Non-owners (agent tokens) get 403, other workspaces 404 (tenancy test, [ADR-009](../../docs/adr/ADR-009-multi-tenancy.md)).
- [x] Completeness guard: a test fails if a tenant-owned table is neither exported nor listed in `ExportWriter.NotExported`.
- [x] Round trip: the export contains every issue, comment and attachment; `workspace.json` structure is checked by the
      test (no separate JSON schema: entities are written as stored).
- [x] Danish text (æøå) survives in JSON and CSV; the CSVs have a BOM for Excel; formulas are neutralised.
- [x] Export files are deleted after 7 days.
- [x] The `[export not built yet]` placeholders are gone from the legal drafts and the look book.

## Out of scope

- Import (from Kupro, Linear or Jira): separate task.
- A per-person export of one user's data across workspaces (GDPR Art. 15): handled by email to the privacy address until volume says otherwise.
