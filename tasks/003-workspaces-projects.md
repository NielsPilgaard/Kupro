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

See [ADR-009](../docs/adr/ADR-009-multi-tenancy.md).

## Scope

- Entities: Workspace, Member (kind: human/agent, role), Project (name, key e.g. `FLOW`).
- EF global query filter by workspace; cross-tenant leak tests.
- Invite by email (sending stubbed/logged until email vendor chosen).

## Acceptance criteria

- [ ] User in workspace A cannot read anything from workspace B (tests).
- [ ] Project key unique per workspace, immutable after first issue.
