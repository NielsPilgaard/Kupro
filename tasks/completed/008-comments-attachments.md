---
title: 'Task 008: Comments, attachments, activity feed'
description: >-
  Markdown comments, image/file attachments in OVHcloud Object Storage via presigned URLs, and activity feed UI.
status: 'Done'
phase: 1
depends_on: [004]
purpose: 'Implementation spec for task 008.'
---

# Task 008: Comments, attachments, activity feed

## Scope

- Comments CRUD (author can edit/delete), markdown.
- Attachments: presigned upload to S3-compatible storage (MinIO in dev, OVHcloud in prod); paste image into editor.
- Activity feed merged with comments on issue detail.

## Acceptance criteria

- [x] Paste screenshot into description -> uploaded and rendered.
- [x] Attachments readable only by workspace members.

## Implementation notes

- Comments: `POST /issues/{key}/comments`, `PATCH|DELETE /comments/{id}` (author only, `not_comment_author` 403);
  guests may comment. Feed: `GET /issues/{key}/activity` merges history and comments, oldest first, newest 500 of
  each, with display-ready values (enum names, member names, issue keys, sprint names). Activity now stores enums as
  names; older numeric entries still render.
- Attachments: `POST /attachments` returns a 10-minute presigned PUT URL, the client uploads straight to storage,
  `POST /attachments/{id}/complete` checks the object (size <= 25 MB), `GET /attachments/{id}` returns a 5-minute
  download URL. Markdown references them as `attachment:ID`, so only members (through the tenant filter) can resolve
  them. Images download inline; everything else, SVG included, as a file.
- Storage: AWSSDK.S3 against any S3-compatible endpoint (`Storage__*` settings; attachments return 503
  `attachments_unavailable` until configured). MinIO no longer publishes images, so dev and tests use
  [Silo](https://silo.pgsty.io/) (`pgsty/silo`, AGPL-3.0 MinIO fork, self-hosted, dev only) on port 8190 with CORS
  for the SPA origin. Production needs an OVHcloud bucket, keys and a CORS rule for the app origin in task 013.
- Parity: CLI `issue activity|comment|attach`, `comment edit|delete`, `attachment get [-o]`; MCP
  `get_issue_activity`, `comment`, `get_attachment`. Upload create/complete opt out of MCP (MCP clients can't PUT
  bytes); agents use `flowstate issue attach`.
- Web: feed and composer in the issue panel; paste or drop files into the description or a comment to upload.
  Also fixed a crash when opening the panel: newer Chromium returns a promise from `scrollIntoView`, which an
  effect in the list returned.
