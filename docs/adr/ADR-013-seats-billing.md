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
  counted from the Member table. The meeting agent is in every plan, limited by a monthly AI budget per workspace
  that speech and LLM tokens both draw from. Guest and agent pricing, and where billing code lives, are open questions.
---

# ADR-013: Seats and Billing Model

## TL;DR

- Billing unit = workspace. One subscription per workspace, billed by Alunta ([ADR-006](ADR-006-eu-vendor-policy.md)).
- Seat = member with kind `human`, role Owner or Member, status `active`. Counted from the Member table, with no separate seat entity.
- Members have a status (`invited` / `active` / `deactivated`) from task 003, so seat counting needs no migration later.
- Meeting agent: in every plan, free included. Monthly AI budget per workspace (5 h per paid seat pooled, 2 h free); when
  spent, the microphone stops and typed text uses keyword matching. No customer API keys on hosted.
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

### Meeting agent usage (decided 2026-10-15)

Speech-to-text and LLM calls cost money per use ([ADR-015](ADR-015-meeting-agent-vendors.md)); seats don't. One
workspace leaving the agent on all day must not cost more than it pays.

- **In every plan.** The meeting agent is a core feature, so the free plan has it too, with a lower allowance.
- **One budget, two meters.** Each workspace has a monthly AI budget, kept in euro-cents at configured rates. Speech
  seconds (per microphone stream) and LLM input and output tokens (from the API's `usage`) both draw from it, so the
  margin holds whatever the mix of speech and typed text, and whichever model is configured.
- **Allowance**, shown to people as meeting hours: 5 hours per paid seat a month, pooled across the workspace; 2 hours a
  month per free workspace. Both are configuration. Owners are warned at 80 %.
- **When it runs out**: the microphone stops. Text sent through the API, CLI or MCP still produces proposals with the
  offline keyword extractor, which uses no LLM. The feature gets less clever; it never disappears. Resets each month.
- **Guards on every call and meeting**: a `max_tokens` limit on LLM output, a pause after 10 minutes without speech, a
  3-hour meeting limit, and a server-wide daily spending ceiling that turns the agent off for everyone.
- **Not now**: extra-hour packs (only if customers ask), customer API keys on hosted Flowstate (secret storage, and a
  key could point at a non-EU vendor), a higher seat tier with more hours (later, OQ-4). Self-hosters configure their
  own endpoints and keys and have no budget.

## Open questions

- **OQ-1**: Guests: free, free up to a ratio (e.g. 5 per paid seat), or paid?
  Leaning: free with a ratio cap.
- **OQ-2**: Agents: free and unlimited, free up to N per paid seat, or metered by usage?
  Leaning: free up to N per paid seat in v1, then revisit with usage data. Agent adoption is the differentiator, so it shouldn't be taxed early.
- **OQ-3**: Where billing code lives: the AGPL core or proprietary `ee/` ([ADR-007](ADR-007-licensing.md))?
  Leaning: core owns seat counting and an `IEntitlements` seam; the Alunta integration and plan definitions live in `ee/`.

- **OQ-4**: A higher seat tier with a larger meeting allowance. Not at launch; revisit with usage data.

## Consequences

### Positive

- **POS-001**: Seat count is a single indexed query over Member, with no sync between two sources of truth.
- **POS-002**: Deactivation keeps activity history intact while freeing the seat.
- **POS-003**: The AI budget caps the cost of a workspace at a known share of its price, whatever model ADR-015 picks.

### Negative

- **NEG-001**: Status adds a check to every authorization path (deactivated members must be rejected).
- **NEG-002**: Until OQ-2 is settled, agent-heavy workspaces have no clear price.
- **NEG-003**: Allowances in hours are approximate: an hour of dense speech with a large workspace costs more than a quiet one.

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
- **IMP-005**: The AI budget, meters and guards are [task 017](../../tasks/017-meeting-agent-budget.md). Until billing
  exists, every workspace gets the paid allowance for its seat count, and a Scaleway billing alert is the outer brake.

## Related Decisions

- [ADR-005](ADR-005-auth-keycloak.md) — identity and tokens; roles live in Flowstate
- [ADR-006](ADR-006-eu-vendor-policy.md) — Alunta as billing vendor
- [ADR-007](ADR-007-licensing.md) — open-core boundary (OQ-3)
- [ADR-008](ADR-008-work-item-model.md) — agents as members
- [ADR-009](ADR-009-multi-tenancy.md) — workspace as tenant and billing unit
- [ADR-015](ADR-015-meeting-agent-vendors.md) — the speech and LLM vendors the AI budget pays for
