---
title: 'Kupro import format'
description: >-
  The open snapshot format Kupro imports from: a ZIP with a snapshot.json header and NDJSON parts for items,
  relations, comments, attachments and history. For anyone writing a converter or script that moves another tracker
  into Kupro.
status: 'Active'
purpose: 'Reference for producing a snapshot Kupro accepts; the JSON Schema is the exact contract.'
---

# Kupro import format

## TL;DR

- A snapshot is a ZIP: `snapshot.json` (format number, source, projects, people, types, statuses, fields, sprints,
  versions) plus NDJSON parts with one record per line: `items/`, `relations/`, `comments/`, `attachments/`,
  `changes/`. Optional attachment bytes go under `files/`.
- Every reference is the source's own ID as a string. Kupro maps them during import; you don't pick Kupro IDs.
- The exact contract is [import-format.schema.json](import-format.schema.json) (JSON Schema 2020-12). This page
  explains it.
- This is format 1. New optional properties may be added within format 1, so ignore ones you don't know. Anything
  else gets a new format number, and Kupro states which formats it reads.

Why the format exists and how an import uses it: [ADR-019](adr/ADR-019-import.md).

## Layout

```text
snapshot.json
items/000001.ndjson
items/000002.ndjson
relations/000001.ndjson
comments/000001.ndjson
attachments/000001.ndjson
changes/000001.ndjson
files/000001
```

- A part may be split over any number of files. Kupro reads a part's files in name order, so number them with
  leading zeros. Kupro writes about a megabyte per file. Each file must be under 64 MB once unpacked.
- Lines are UTF-8 JSON objects separated by `\n`. Blank lines are skipped.
- Property names are camelCase. Times are ISO 8601 with an offset (`2025-03-01T09:00:00Z`), dates are
  `yyyy-MM-dd`. Leave out a property you have no value for, or send `null` if the schema allows it.
- Every part is optional. A snapshot with only `snapshot.json` and `items/` is valid.

## snapshot.json

```json
{
  "format": 1,
  "source": { "kind": "jira", "baseUrl": "https://acme.atlassian.net" },
  "createdAt": "2026-11-06T12:00:00Z",
  "projects": [{ "id": "10", "key": "WEB", "name": "Web" }],
  "people": [{ "id": "u1", "name": "Sofie Larsen", "email": "sofie@acme.example" }],
  "itemTypes": [{ "id": "story", "name": "Story", "level": 0 }, { "id": "epic", "name": "Epic", "level": 1 }],
  "statuses": [{ "id": "3", "name": "Done", "category": "done" }],
  "fields": [{ "id": "cf_team", "name": "Team", "kind": "select", "options": ["Blue", "Red"] }],
  "sprints": [{ "id": "s1", "name": "Sprint 1", "start": "2025-03-03", "end": "2025-03-14" }],
  "versions": [{ "id": "v1", "project": "10", "name": "1.0", "date": "2025-04-01" }]
}
```

- `source.kind`: `jira`, `azure-devops`, `linear` and the other built-in sources, or your own name, which is shown
  as given ("via acme-tool import").
- `people`: everyone the records refer to. In the wizard's People step each one becomes an existing member, an
  invite or a placeholder. An email makes matching automatic. A `handle` is the name mentions use (`@sofie`), so
  mentions in descriptions and comments are rewritten to the person's Kupro name. Set `bot: true` on an app or
  integration account and the People step proposes to skip it.
- `itemTypes[].level` and `statuses[].category` feed the default type and state mapping. Leave them out and the
  owner maps by hand.
- `sprints[].project`: leave it out when a sprint spans projects. Kupro then creates one sprint per project.
- `projects[].capacity`: headcount steps for the delivery plan, e.g. `[{ "from": "2027-01-01", "people": 3 }]`. Kupro
  keeps the month and sets them on a project the import creates; an existing project keeps its own.
- `counts` and `cursor` are written by Kupro. A script can leave them out.
- `partial`: `true` when the snapshot holds only what changed since an earlier one. Leave it out for a whole snapshot:
  an item an earlier run imported and this snapshot lacks is then reported as deleted in the source, and left alone.

## Records

| Part | One line is | Required |
|---|---|---|
| `items/` | a work item | `id`, `project`, `type`, `status`, `title`, `created`, `updated` |
| `relations/` | a link between two items | `from`, `to`, `kind` (`blocks`, `relates`, `duplicates`) |
| `comments/` | a comment | `id`, `item`, `body`, `created` |
| `attachments/` | an attachment | `id`, `item`, `fileName`, `created` |
| `changes/` | one field change from history | `id`, `item`, `at`, `field` |

```json
{"id":"101","key":"WEB-2","project":"10","type":"story","status":"3","title":"Pay with MobilePay","description":"<p>Card first</p>","descriptionMarkup":"html","parent":"100","assignee":"u1","priority":"High","labels":["payments"],"fields":{"cf_team":"Blue"},"estimateSeconds":21600,"sprint":"s1","created":"2025-03-01T09:00:00Z","updated":"2025-03-03T09:00:00Z","resolved":"2025-03-03T09:00:00Z"}
```

- **Items**:
  - `parent` is the item's parent; don't also send it as a relation.
  - `key` is kept where Kupro can keep it (Jira-style keys). Otherwise it becomes an alias that still finds the issue.
  - `description` and comment `body` give their markup kind: `plain`, `markdown`, `html`, `adf` (Jira Cloud's
    document JSON, as a string) or `jira-wiki`.
  - `fields` holds values of the `fields` defined in the header: a string for text, date, select and user (the
    person's ID), a number, or an array of strings for multi-select.
  - `estimateSeconds` is a time estimate; `points` is story points, which the owner converts with an hours-per-point
    factor.
  - `links` holds URLs outside the source (commits, pull requests). They go into the issue's "Imported from" block.
- **Attachments**:
  - With `file`, the bytes are in the ZIP at that path, which must be under `files/`.
  - Without it, Kupro fetches `url` through the source's connector. That only works for a built-in source with a
    token, so a script should include the files.
  - `url` is also how Kupro finds inline images in descriptions and comments and points them at the copy.
- **Changes** replay as the issue's history:
  - `field` is an item property (`status`, `assignee`, `title`, `priority`, `parent`, `sprint`, `labels`, `due`, …)
    or a field ID.
  - `from`/`to` hold IDs where the field refers to something (a status, a person), otherwise the raw value.
  - `fromText`/`toText` are what the source showed.
  - `id` must be unique per item. Re-imports skip changes they have already seen.

## Importing a snapshot

Upload it in Settings → Import → Snapshot, or run `kupro import snapshot <file.zip>`. Re-importing a newer
snapshot of the same source updates the issues it created before. Kupro matches them by source ID, and edits made in
Kupro since are kept ([ADR-019](adr/ADR-019-import.md#re-runs-and-conflicts)).

For a Jira, Azure DevOps, Linear or Trello server Kupro can't reach (behind a VPN), `kupro import <source> --url
<address> --local` reads it from your machine and uploads the snapshot for you; the source token stays on your machine.
Add `--import <id>` to re-run into the same import, or `--out <file.zip>` to write the snapshot without uploading it.
