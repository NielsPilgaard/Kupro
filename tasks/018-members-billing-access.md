---
title: 'Task 018: Members page and billing access'
description: >-
  A members page in the web app (list, invite, change role, deactivate), a per-member billing-access flag that Owners
  grant, and free guests capped at 5 per seat. Implements the billing-access and guest decisions in ADR-013.
status: 'Todo'
phase: 1
depends_on: [003]
purpose: 'Implementation spec for task 018.'
---

# Task 018: Members page and billing access

## TL;DR

The rules are in [ADR-013](../docs/adr/ADR-013-seats-billing.md) ("Guests" and "Billing access"). This task builds:
- the members page in the web app (the API and CLI already have it, task 003);
- `Member.CanManageBilling`, granted by Owners, which Owners always have;
- the guest cap: free guests, up to 5 per seat;
- Guest presented in the UI as the free, view-and-comment role.

## Roles after this task

| Role | Can | Seat |
|---|---|---|
| Owner | Everything, including members, workspace settings, export, billing | Yes |
| Member | Create and edit issues, projects, sprints, plans, meetings | Yes |
| Guest | Read everything, comment, attach files | No (free, up to 5 per seat) |
| + billing flag | On top of any human role: see usage and cost, manage the subscription | No change |

A finance person with no project work joins as Guest with the billing flag, and costs nothing.

## Scope

- **Data**: `Member.CanManageBilling` (bool, default false), one migration. Only human members can have it; agents never
  inherit it from their owner (same cap as the role, [task 003](completed/003-workspaces-projects.md)).
- **Rule** in `WorkspaceService`: billing access = `Role == Owner || CanManageBilling`. Resolved with the membership in
  the existing one-query workspace filter and exposed on `WorkspaceAccess` / `WorkspaceContext`. A
  `[BillingAccess]` attribute (next to `[WorkspaceRole]`) guards billing endpoints.
- **Guest cap** in `WorkspaceService`, `Members:GuestsPerSeat` (5 on hosted; unset = no cap, for self-hosted):
  - Guests = human members with role Guest and status `invited` or `active`. Cap = seats × GuestsPerSeat.
  - Invite as Guest, role change to Guest, and accepting a Guest invite check the cap; past it → 409
    `guest_limit_reached` with the current count and cap. Check and insert in one transaction, so two parallel
    invites can't both slip past.
  - Deactivating a seat or demoting an Owner/Member to Guest can leave a workspace over the cap: existing guests keep
    access, only new ones are blocked. The demotion itself counts as a new guest and is checked.
  - `WorkspaceResponse` gains `guests` and `guestLimit` (null when uncapped).
  - Hosted sets `Members__GuestsPerSeat=5` in Dokploy; add it to [deploy.md](../docs/runbooks/deploy.md).
- **API**:
  - `MemberResponse` and `WorkspaceResponse` gain `canManageBilling` (for the response: the member's flag; for the
    workspace: whether *you* have billing access, so the SPA can show the billing section).
  - `InviteMemberRequest` and `UpdateMemberRequest` gain an optional `canManageBilling`. Owner only, as today.
  - Setting the flag on an agent → 400 `billing_access_humans_only`.
- **CLI**: `flowstate member invite --billing` and `flowstate member update <id> --billing true|false`; `member list`
  gets a BILLING column. Role on `update` becomes optional so the flag can change on its own.
- **MCP**: no new tools. Member administration stays opted out ([ADR-001](../docs/adr/ADR-001-agent-parity.md),
  `[NoAgentSurface]` on `MembersController`); `list_members` returns the new field.
- **Web — members page** (`/w/$workspace/members`, linked from the workspace nav and settings):
  - Everyone sees active members, agents (with their owner), the seat count and "guests 7 of 15".
  - Owners also see pending invites and deactivated members, and can invite (email, role, billing), change role,
    toggle billing access and deactivate (with a confirm that names what happens: access and tokens revoked, history kept).
  - The last-owner rule surfaces as an inline error, not a toast only.
  - Role picker shows each role with a one-line description and whether it takes a seat.
- **Copy** (EN/DA, [ADR-011](../docs/adr/ADR-011-i18n.md), glossary in [branding.md](../docs/branding.md)): Guest stays
  "Guest" / "Gæst" in the UI, described as "Can view and comment. Free." The API value stays `guest`.
- **Task 017 hook**: usage and cost views and the 80 % / 100 % emails go to everyone with billing access, not just
  Owners. Task 017 uses the rule from this task.

## Out of scope

- Billing itself (plans, checkout, invoices, card details). Card details never touch Flowstate: they live in Alunta's
  hosted checkout and portal ([ADR-013](../docs/adr/ADR-013-seats-billing.md) IMP-007). The billing task adds the
  pages that `[BillingAccess]` guards.
- Idle-seat flags (`LastActiveAt`, ADR-013 IMP-006): the members page leaves room for an "idle" badge.
- A strict read-only role without comments. Guest covers stakeholders; add one only if a customer asks.

## Acceptance criteria

- [ ] Owner can grant and revoke billing access over API, CLI and web; Member and Guest get 403.
- [ ] A Guest with billing access passes `[BillingAccess]`, is not a seat, and still can't edit issues (tests).
- [ ] An agent token of an Owner or billing member does not pass `[BillingAccess]` (test).
- [ ] Setting the flag on an agent member returns 400 `billing_access_humans_only`.
- [ ] Members page: list, invite, change role, toggle billing, deactivate; Owner-only controls hidden for others.
- [ ] Seat count unchanged: active human Owners and Members only.
- [ ] Guest cap: with 1 seat, the 6th guest invite (pending invites included) gets 409 `guest_limit_reached`; two
      parallel invites at 4 of 5 let exactly one through; no cap when `GuestsPerSeat` is unset (tests).
- [ ] Over the cap after a seat is deactivated: existing guests keep access, new guest invites are refused.
- [ ] Parity test passes with no new opt-outs; i18n check passes for EN and DA.
- [ ] E2E: Owner invites a Guest with billing access; seat count stays the same.
