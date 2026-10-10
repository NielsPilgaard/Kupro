---
title: 'Task 003: Workspaces, projects, members'
description: >-
  Workspace creation, membership with Owner/Member/Guest roles, projects with keys, and the tenant query filter.
status: 'Todo'
phase: 1
depends_on: [002]
purpose: 'Implementation spec for task 003.'
---

# Task 003: Workspaces, projects, members

See [ADR-009](../docs/adr/ADR-009-multi-tenancy.md), [ADR-013](../docs/adr/ADR-013-seats-billing.md) (member status, seats).

## Scope

- Entities: Workspace, Member (kind: human/agent, role, status: invited/active/deactivated), Project (name, key e.g. `FLOW`).
- EF global query filter by workspace; cross-tenant leak tests.
- Invite by email (sending stubbed/logged until email vendor chosen); invitee is `invited` until they accept.
- Deactivate member (never delete): access and API tokens revoked, history and attribution kept.
- Seat count in the workspace service (no billing yet); index `(workspace_id, kind, status)`.

## Acceptance criteria

- [ ] User in workspace A cannot read anything from workspace B (tests).
- [ ] Project key unique per workspace, immutable after first issue.
- [ ] Deactivated member's session and API tokens are rejected; their past activity still shows their name.
- [ ] Seat count includes only active human Owners/Members.
