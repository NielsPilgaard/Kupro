---
title: 'Task 016: Workspace export'
description: >-
  Owners export a whole workspace (JSON, CSV and attachment files in one ZIP) through web, REST, CLI and MCP. Backs the
  data-portability promise in the terms, privacy policy and DPA.
status: 'Todo'
phase: 1
depends_on: [003, 004, 008]
purpose: 'Implementation spec for task 016.'
---

# Task 016: Workspace export

## TL;DR

The draft terms, privacy policy and DPA ([public pages](../docs/design/public-pages/index.html)) promise that owners can
export everything, at any time and for 30 days after a subscription ends. GDPR data portability and the DPA's end-of-processing
clause lean on it. This task builds it: one ZIP per export, built in the background, downloadable for 7 days.

## Scope

- **Who**: workspace owners only. Members and guests get 403; agents with an owner's token may export (parity).
- **What**: one ZIP with
  - `workspace.json`: the full workspace in one document with `"format": 1`: workspace, projects, members (name, email,
    role, capacity), issues with labels and links, sprints, plan data, comments, activity, meetings and their proposals.
    Ids and keys as in the API; DTO shapes reused where they exist.
  - `issues.csv` (one row per issue, flat columns, UTF-8 with BOM so Excel opens Danish text) and `comments.csv`.
  - `attachments/<issue key>/<file name>`: the original files from object storage.
  - `README.txt`: what each file is, the format version, and the export time.
- **Not included**: API tokens and their hashes, Keycloak credentials, transcripts and snippets (deleted with the
  meeting by default, [task 015](015-meeting-agent.md)), billing data (ours, not the workspace's).
- **How**:
  - `POST /workspaces/{ws}/exports` starts a background job and returns the export with state `building`.
  - `GET /workspaces/{ws}/exports` lists recent exports; `GET …/exports/{id}` shows state, size and a download link.
  - `GET …/exports/{id}/file` streams the ZIP.
  - The owner who started it gets an email when it is ready (Email feature, EN/DA).
  - Files live in object storage (Attachments' `ObjectStorage`) and are deleted after 7 days by a job.
- **Parity** ([ADR-001](../docs/adr/ADR-001-agent-parity.md)):
  - CLI `flowstate workspace export [--wait] [-o file.zip]` starts an export, waits, and downloads it.
  - MCP `export_workspace` starts one, and `get_export` returns its state and download link. MCP opts out of
    downloading the ZIP itself.
  - Web: a settings section for owners, with an Export button and a list of recent exports.
- **Limits**: one running export per workspace; starting another returns the running one. Streamed ZIP writing, so
  memory stays flat for large workspaces. Reads use `AsNoTracking` and paged queries
  ([ADR-010](../docs/adr/ADR-010-performance-budget.md)); the job runs outside the request budget.
- **After the subscription ends**: export keeps working while the workspace is read-only (terms §9). Billing isn't built
  yet; this task only makes sure export doesn't depend on write access.

## Acceptance criteria

- [ ] An owner can start, list and download an export through web, REST, CLI and MCP; parity test passes.
- [ ] Non-owners and other workspaces get 403/404 (tenancy test, [ADR-009](../docs/adr/ADR-009-multi-tenancy.md)).
- [ ] Completeness guard: a test lists every tenant-owned entity type in the `DbContext` and fails if one is neither
      exported nor named in the "not included" list, so new features can't be forgotten.
- [ ] Round trip: a seeded workspace's export contains every issue, comment and attachment, and `workspace.json`
      validates against a checked-in JSON schema.
- [ ] Danish text (æøå) survives in JSON and CSV, and the CSV opens correctly in Excel.
- [ ] Export files are deleted after 7 days.
- [ ] When this is done, remove the `[export not built yet]` placeholders from the legal drafts and the open item in the look book.

## Out of scope

- Import (from Flowstate, Linear or Jira): separate task.
- A per-person export of one user's data across workspaces (GDPR Art. 15): handled by email to the privacy address until volume says otherwise.
