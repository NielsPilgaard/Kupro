---
title: 'Task 008: Comments, attachments, activity feed'
description: >-
  Markdown comments, image/file attachments in OVHcloud Object Storage via presigned URLs, and activity feed UI.
status: 'Todo'
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

- [ ] Paste screenshot into description -> uploaded and rendered.
- [ ] Attachments readable only by workspace members.
