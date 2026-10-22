---
title: 'Task 003: Workspaces, projects, members'
description: >-
  Workspace creation, membership with Owner/Member/Guest roles, projects with keys, and the tenant query filter.
status: 'Done'
phase: 1
depends_on: [002]
purpose: 'Implementation spec for task 003.'
---

# Task 003: Workspaces, projects, members

See [ADR-009](../../docs/adr/ADR-009-multi-tenancy.md), [ADR-013](../../docs/adr/ADR-013-seats-billing.md) (member status, seats).

## Scope

- Entities: Workspace, Member (kind: human/agent, role, status: invited/active/deactivated), Project (name, key e.g. `KUP`).
- EF global query filter by workspace; cross-tenant leak tests.
- Invite by email (sending stubbed/logged until email vendor chosen); invitee is `invited` until they accept.
- Deactivate member (never delete): access and API tokens revoked, history and attribution kept.
- Seat count in the workspace service (no billing yet); index `(workspace_id, kind, status)`.

## Acceptance criteria

- [x] User in workspace A cannot read anything from workspace B (tests).
- [x] Project key unique per workspace, immutable after first issue (rule in `ProjectService`; tested end-to-end in task 004).
- [x] Deactivated member's session and API tokens are rejected; their past activity still shows their name.
- [x] Seat count includes only active human Owners/Members.

## Implementation notes

- Workspace routes are `/api/v1/workspaces/{workspace}/...` (slug). A global action filter resolves the slug to the
  caller's active membership in one query, sets the scoped `WorkspaceContext`, and answers 404 `workspace_not_found`
  for non-members so other tenants are indistinguishable from missing ones. `[WorkspaceRole(...)]` sets a minimum role.
- `KuproDbContext` applies a named `tenant` query filter to every `ITenantOwned` entity; with no workspace resolved
  the filter matches nothing. The DbContext factory is scoped (not pooled) so contexts see the request's workspace.
- API tokens can be scoped to a workspace; agent tokens must be, and create/reuse an agent member (kind `agent`,
  owner = the creating user, role capped at Member). Deactivating a member deactivates their agents and revokes their
  tokens for that workspace in one transaction.
- Invites match the user's verified email (`email_verified` from Keycloak); email sending is logged only.
- Project colours are named (`verdigris`, `copper`, ...) and mapped to design tokens in the SPA.
