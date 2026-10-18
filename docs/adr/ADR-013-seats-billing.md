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
  that speech and LLM tokens both draw from. Guests are free up to 5 per seat; billing access is a per-member flag
  Owners grant. Agent pricing and where billing code lives are open questions.
---

# ADR-013: Seats and Billing Model

## TL;DR

- Billing unit = workspace. One subscription per workspace, billed by Alunta ([ADR-006](ADR-006-eu-vendor-policy.md)).
- Seat = member with kind `human`, role Owner or Member, status `active`. Counted from the Member table, with no separate seat entity.
- Members have a status (`invited` / `active` / `deactivated`) from task 003, so seat counting needs no migration later.
- No free plan: a 30-day trial of everything, then paid seats. The workspace is read-only (exportable) until it subscribes.
- Meeting agent: in the trial and the paid plan. Monthly AI budget per workspace (5 h per paid seat pooled, 10 h for the
  whole trial); when spent, the microphone stops and typed text uses keyword matching. No customer API keys on hosted.
- Guests (view and comment) are free, up to 5 per seat. Billing access is a flag on any human member; Owners always have it.
- Open: are agents free (OQ-2), and does billing code live in core or `ee/` (OQ-3)?
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
- **Guests** (decided 2026-10-18): free, up to 5 per seat. A guest can read everything, comment and attach files,
  but not create or edit work, so a guest can't stand in for a paid member. The cap protects the server: every guest
  is a session, a realtime connection and requests that pay nothing. Active guests and pending guest invites count;
  an invite or role change past the cap is refused. Dropping below the cap later (a seat deactivated) keeps existing
  guests and only blocks new ones. Trials use the same ratio. Self-hosted has no cap.
- **Billing access** (decided 2026-10-18): a `CanManageBilling` flag on a human member, not a role, so it combines
  with any role and doesn't add a seat. Owners always have it. It allows seeing usage and cost and managing the
  subscription; card details stay in Alunta's hosted pages and never reach Flowstate. A finance person with no project
  work joins as a guest with the flag. Agents never get it.
- **Member status**:
  - `invited`: no seat until the invite is accepted.
  - `active`: counts as a seat.
  - `deactivated`: no access and no seat, but history and attribution are kept. Members are deactivated, never deleted.
- **Trial, no free plan** (decided 2026-10-15): a new workspace gets 30 days of the full product, meeting agent
  included, with no card. Then it needs paid seats. Unpaid, it turns read-only (export still works, task 016) and is
  deleted 30 days later, with emails before each step.
- **Self-hosted**: no seat limit. Entitlement checks resolve to "unlimited" when no billing module is present.

### Meeting agent usage (decided 2026-10-15)

Speech-to-text and LLM calls cost money per use ([ADR-015](ADR-015-meeting-agent-vendors.md)); seats don't. One
workspace leaving the agent on all day must not cost more than it pays.

- **In the trial and the paid plan.** The meeting agent is a core feature, so the trial includes it.
- **One budget, two meters.** Each workspace has a monthly AI budget, kept in euro-cents at configured rates. Speech
  seconds (per microphone stream) and LLM input and output tokens (from the API's `usage`) both draw from it, so the
  margin holds whatever the mix of speech and typed text, and whichever model is configured.
- **Allowance**, shown to people as meeting hours: 5 hours per paid seat a month, pooled across the workspace; 10 hours
  in total for a trial, whatever its size, so inviting extra people doesn't buy hours. Both are configuration. Owners are
  warned at 80 %.
- **When it runs out**: the microphone stops. Text sent through the API, CLI or MCP still produces proposals with the
  offline keyword extractor, which uses no LLM. The feature gets less clever; it never disappears. Resets each month.
- **Guards on every call and meeting**: a `max_tokens` limit on LLM output, a pause after 10 minutes without speech, a
  3-hour meeting limit, and a server-wide daily spending ceiling that turns the agent off for everyone.
- **Not now**: extra-hour packs (only if customers ask), customer API keys on hosted Flowstate (secret storage, and a
  key could point at a non-EU vendor), a higher seat tier with more hours (later, OQ-4). Self-hosters configure their
  own endpoints and keys and have no budget.

## Open questions

- **OQ-1**: Resolved 2026-10-18: guests are free, up to 5 per seat (see Decision).
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
- **IMP-004**: Billing task is written once OQ-2 and OQ-3 are resolved; it is not part of v1.
- **IMP-005**: The AI budget, meters and guards are [task 017](../../tasks/017-meeting-agent-budget.md). Until billing
  exists, every workspace gets the paid allowance for its seat count, and a Scaleway billing alert is the outer brake.
- **IMP-008**: The billing-access flag, a `[BillingAccess]` guard, the guest cap (`Members:GuestsPerSeat`, 5 on
  hosted, unset on self-hosted) and the members page are [task 018](../../tasks/018-members-billing-access.md).
  Task 017's usage view and budget emails use that guard.

## Related Decisions

- [ADR-005](ADR-005-auth-keycloak.md) — identity and tokens; roles live in Flowstate
- [ADR-006](ADR-006-eu-vendor-policy.md) — Alunta as billing vendor
- [ADR-007](ADR-007-licensing.md) — open-core boundary (OQ-3)
- [ADR-008](ADR-008-work-item-model.md) — agents as members
- [ADR-009](ADR-009-multi-tenancy.md) — workspace as tenant and billing unit
- [ADR-015](ADR-015-meeting-agent-vendors.md) — the speech and LLM vendors the AI budget pays for
