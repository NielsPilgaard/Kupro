---
title: 'ADR-013: Seats and Billing Model'
status: 'Proposed'
date: '2026-10-10'
authors: 'Niels'
tags: ['business', 'billing', 'tenancy']
supersedes: ''
superseded_by: ''
description: >-
  Hosted Flowstate is billed per workspace by seat. A seat is an active human member with role Owner or Member,
  counted from the Member table. Guest and agent pricing, and where billing code lives, are open questions.
---

# ADR-013: Seats and Billing Model

## TL;DR

- Billing unit = workspace. One subscription per workspace, billed by Alunta ([ADR-006](ADR-006-eu-vendor-policy.md)).
- Seat = member with kind `human`, role Owner or Member, status `active`. Counted from the Member table, with no separate seat entity.
- Members have a status (`invited` / `active` / `deactivated`) from task 003, so seat counting needs no migration later.
- Open: are guests and agents free (OQ-1, OQ-2), and does billing code live in core or `ee/` (OQ-3)?
- Billing implementation stays after v1 ([product.md](../product.md)); only the data model is decided now.

## Status

**Proposed**

## Context

Flowstate targets teams and will be sold as a seat-priced SaaS. Auth and membership are built per user from the start
([ADR-005](ADR-005-auth-keycloak.md), task 003), but nothing defines what a seat is. That definition shapes the
Member entity (task 003) and is awkward to retrofit once workspaces have data.

Being agent-first makes this harder. Agents are workspace members of kind `agent`
([ADR-008](ADR-008-work-item-model.md) OQ-3) but act on behalf of a user ([ADR-001](ADR-001-agent-parity.md)).
Pricing agents like humans would penalise the core use case. Leaving them unlimited invites one human seat
running a fleet of agents.

## Decision

- **Billing unit**: the workspace ([ADR-009](ADR-009-multi-tenancy.md)). A user in several workspaces takes a seat in each.
- **Seat**: `kind = human AND role IN (Owner, Member) AND status = active`.
- **Member status**:
  - `invited`: no seat until the invite is accepted.
  - `active`: counts as a seat.
  - `deactivated`: no access and no seat, but history and attribution are kept. Members are deactivated, never deleted.
- **Self-hosted**: no seat limit. Entitlement checks resolve to "unlimited" when no billing module is present.

## Open questions

- **OQ-1**: Guests: free, free up to a ratio (e.g. 5 per paid seat), or paid?
  Leaning: free with a ratio cap.
- **OQ-2**: Agents: free and unlimited, free up to N per paid seat, or metered by usage?
  Leaning: free up to N per paid seat in v1, then revisit with usage data. Agent adoption is the differentiator, so it shouldn't be taxed early.
- **OQ-3**: Where billing code lives: the AGPL core or proprietary `ee/` ([ADR-007](ADR-007-licensing.md))?
  Leaning: core owns seat counting and an `IEntitlements` seam; the Alunta integration and plan definitions live in `ee/`.

## Consequences

### Positive

- **POS-001**: Seat count is a single indexed query over Member, with no sync between two sources of truth.
- **POS-002**: Deactivation keeps activity history intact while freeing the seat.

### Negative

- **NEG-001**: Status adds a check to every authorization path (deactivated members must be rejected).
- **NEG-002**: Until OQ-2 is settled, agent-heavy workspaces have no clear price.

## Alternatives Considered

### Per-user billing across workspaces

- **ALT-001**: **Description**: One subscription per user, covering every workspace they belong to.
- **ALT-002**: **Rejection Reason**: Teams buy tools, not individuals; it doesn't match how startups pay.

### Flat price per workspace

- **ALT-003**: **Description**: One price regardless of team size.
- **ALT-004**: **Rejection Reason**: Under-charges larger teams and makes seat-based enterprise upsell harder.

## Implementation Notes

- **IMP-001**: Task 003 adds `Member.Status` and deactivate (not delete) semantics.
- **IMP-002**: Deactivating a member revokes their API tokens ([ADR-005](ADR-005-auth-keycloak.md)), and their agent tokens with them.
- **IMP-003**: Seat count index: `(workspace_id, kind, status)`.
- **IMP-004**: Billing task is written once OQ-1 to OQ-3 are resolved; it is not part of v1.

## Related Decisions

- [ADR-005](ADR-005-auth-keycloak.md) — identity and tokens; roles live in Flowstate
- [ADR-006](ADR-006-eu-vendor-policy.md) — Alunta as billing vendor
- [ADR-007](ADR-007-licensing.md) — open-core boundary (OQ-3)
- [ADR-008](ADR-008-work-item-model.md) — agents as members
- [ADR-009](ADR-009-multi-tenancy.md) — workspace as tenant and billing unit
