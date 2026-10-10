---
title: 'Task 012: Realtime updates'
description: >-
  SignalR hub pushing issue changes to connected clients in the same workspace, patching TanStack Query caches.
status: 'Todo'
phase: 1
depends_on: [007]
purpose: 'Implementation spec for task 012.'
---

# Task 012: Realtime updates

## Scope

- One group per workspace; hub requires `[Authorize]`.
- Events emitted after commit from feature services.
- Client patches caches; on conflict server wins + toast.

## Acceptance criteria

- [ ] Change made via CLI appears in an open browser < 1 s.
