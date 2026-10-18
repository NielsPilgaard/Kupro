---
title: 'Task 012: Realtime updates'
description: >-
  SignalR hub pushing issue changes to connected clients in the same workspace, patching TanStack Query caches.
status: 'Done'
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

- [x] Change made via CLI appears in an open browser < 1 s.

## Implementation notes

- Hub `/api/v1/realtime` (`WorkspaceHub`, `[Authorize]`): a connection calls `Join(slug)`, which runs the same
  membership check as the REST routes; non-members get `false`. One group per workspace, single instance (Postgres
  LISTEN/NOTIFY fan-out is for later, ADR-002). Browsers send the token as `access_token` on the hub path only;
  request logging records paths, not query strings.
- `RealtimeEvents` is called by IssueService, CommentService, SprintService and PlanService after SaveChanges:
  `Issues` carries the changed rows (so lists patch in place), `Changed` names a topic (issue, sprints, plan) to
  refetch. Pushes are best effort: a failure is logged, the write stands, clients refetch on reconnect.
- Web `useRealtime` in the app shell patches every cached issue list, refetches the touched detail, activity, plan
  and sprints, and refetches everything after a reconnect. Server wins; when someone else changes the issue open in
  the panel, a toast says who and via which surface.
- Measured live: a `kupro issue move` by an agent token showed on the open board before the CLI process had
  exited (well under 1 s), with the panel, feed and toast updated.
- Not yet: membership changes don't drop an already joined connection until it reconnects.
