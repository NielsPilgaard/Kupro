---
title: 'ADR-013: Seats and Billing Model'
status: 'Proposed'
date: '2026-10-10'
authors: 'Niels'
tags: ['business', 'billing', 'tenancy']
supersedes: ''
superseded_by: ''
description: >-
  Hosted Kupro is billed per workspace by seat: every active human Owner or Member, paid in advance, €9 a month
  or €90 a year, plus a Custom plan for large organisations. Kupro flags seats nobody uses so owners can free them
  before renewal. 30-day trial, no free plan. The meeting agent is in every plan, limited by a monthly AI budget per
  workspace. Guests are free up to 5 per seat; billing access is a per-member flag Owners grant. Agent pricing and
  where billing code lives are open questions.
---

# ADR-013: Seats and Billing Model

## TL;DR

- Billing unit = workspace. One subscription per workspace, billed by Alunta ([ADR-006](ADR-006-eu-vendor-policy.md)).
- Seat = member with kind `human`, role Owner or Member, status `active`. Counted from the Member table, with no
  separate seat entity. A seat costs whether or not the person uses it.
- Plans, paid in advance: **Monthly** €9 / 69 kr a seat, **Yearly** €90 / 690 kr a seat (two months free), and
  **Custom** for large organisations ("get in touch with your requirements"), set up by hand in Alunta.
- Idle seats (no activity in 30 days) are flagged, and owners are told before a yearly renewal so they can deactivate them.
- Members have a status (`invited` / `active` / `deactivated`) from task 003, so seat counting needs no migration later.
- No free plan: a 30-day trial of everything, then paid seats. The workspace is read-only (exportable) until it subscribes.
- Meeting agent: in the trial and the paid plan. Monthly AI budget per workspace (2 h per seat pooled, 2 h for the
  whole trial); when spent, the microphone stops and typed text uses keyword matching. No customer API keys on hosted.
- Guests (view and comment) are free, up to 5 per seat. Billing access is a flag on any human member; Owners always have it.
- Open: are agents free (OQ-2), and does billing code live in core or `ee/` (OQ-3)?
- Billing implementation stays after v1 ([product.md](../product.md)); only the data model is decided now.

## Status

**Proposed**

## Context

Kupro targets teams and will be sold as a seat-priced SaaS. Auth and membership are built per user from the start
([ADR-005](ADR-005-auth-keycloak.md), task 003), but nothing defines what a seat is. That definition shapes the
Member entity (task 003) and is awkward to retrofit once workspaces have data.

Being agent-first makes this harder. Agents are workspace members of kind `agent`
([ADR-008](ADR-008-work-item-model.md) OQ-3) but act on behalf of a user ([ADR-001](ADR-001-agent-parity.md)).
Pricing agents like humans would penalise the core use case. Leaving them unlimited invites one human seat
running a fleet of agents.

## Decision

- **Billing unit**: the workspace ([ADR-009](ADR-009-multi-tenancy.md)). A user in several workspaces takes a seat in each.
- **Seat**: `kind = human AND role IN (Owner, Member) AND status = active`.
- **Plans** (decided 2026-10-18), excluding VAT, paid in advance:
  - **Monthly**: €9 / 69 kr a seat a month.
  - **Yearly**: €90 / 690 kr a seat a year, two months free.
  - **Custom**: for large organisations with their own requirements (a yearly invoice, purchase orders, a negotiated
    price or terms). The pricing page says "get in touch"; the subscription is set up by hand in Alunta.
  - Adding a seat mid-period is charged pro rata from that day. Removing one takes effect at the next renewal, with
    no refund for the rest of the period.
- **Idle seats**: a seat with no activity for 30 days is flagged in member settings and in the members list over the
  API, CLI and MCP ([ADR-001](ADR-001-agent-parity.md)). Owners get an email 30 and 7 days before a yearly renewal
  listing the idle seats, so they can deactivate them first. Activity = any authenticated request by the member, or by
  an agent token acting for them.
- **Guests** (decided 2026-10-18): free, up to 5 per seat. A guest can read everything, comment and attach files,
  but not create or edit work, so a guest can't stand in for a paid member. The cap protects the server: every guest
  is a session, a realtime connection and requests that pay nothing. Active guests and pending guest invites count;
  an invite or role change past the cap is refused. Dropping below the cap later (a seat deactivated) keeps existing
  guests and only blocks new ones. Trials use the same ratio. Self-hosted has no cap.
- **Billing access** (decided 2026-10-18): a `CanManageBilling` flag on a human member, not a role, so it combines
  with any role and doesn't add a seat. Owners always have it. It allows seeing usage and cost and managing the
  subscription; card details stay in Alunta's hosted pages and never reach Kupro. A finance person with no project
  work joins as a guest with the flag. Agents never get it.
- **Member status**:
  - `invited`: no seat until the invite is accepted.
  - `active`: counts as a seat.
  - `deactivated`: no access and no seat, but history and attribution are kept. Members are deactivated, never deleted.
- **Trial, no free plan** (decided 2026-10-15): a new workspace gets 30 days of the full product, meeting agent
  included, with no card, for any number of people. Then it subscribes for the seats it has at that point.
  Unpaid, it turns read-only (export still works, task 016) and is deleted 30 days later, with emails before each step.
- **Self-hosted**: no seat limit. Entitlement checks resolve to "unlimited" when no billing module is present.
- **Non-profits and education** (decided 2026-10-18): the paid plan free on request, without the meeting agent (an AI
  allowance of 0 hours). Seats cost us next to nothing, but every meeting hour is about €0.69 in vendor fees
  ([ADR-015](ADR-015-meeting-agent-vendors.md)) and a free workspace pays none of it back. Typed transcripts still get
  keyword proposals, which use no LLM.

### Meeting agent usage (decided 2026-10-15)

Speech-to-text and LLM calls cost money per use ([ADR-015](ADR-015-meeting-agent-vendors.md)); seats don't. One
workspace leaving the agent on all day must not cost more than it pays.

- **In the trial and the paid plan.** The meeting agent is a core feature, so the trial includes it.
- **One budget, two meters.** Each workspace has a monthly AI budget, kept in euro-cents at configured rates. Speech
  seconds (per microphone stream) and LLM input and output tokens (from the API's `usage`) both draw from it, so the
  margin holds whatever the mix of speech and typed text, and whichever model is configured.
- **Allowance**, shown to people as meeting hours: 2 hours per seat a month, pooled across the workspace; 2 hours
  in total for a trial, whatever its size, so inviting extra people doesn't buy hours. Both are configuration.
  Owners are warned at 80 %. Cut from 5 hours on 2026-10-17, when the extraction model changed and an hour went from about €0.32
  to €0.69 ([ADR-015](ADR-015-meeting-agent-vendors.md) IMP-006); 2 hours is at most about €1.40 per seat, and a
  6-person team's daily 15-minute standups (≈ 5.5 h) fit in its 12. The trial was cut from 10 hours the same day to
  keep spend low before revenue: at most about €1.40 per trial, enough for 8 standups of 15 minutes.
- **When it runs out**: the microphone stops. Text sent through the API, CLI or MCP still produces proposals with the
  offline keyword extractor, which uses no LLM. The feature gets less clever; it never disappears. Resets each month.
- **Guards on every call and meeting**: a `max_tokens` limit on LLM output, a pause after 10 minutes without speech, a
  3-hour meeting limit, and a server-wide daily spending ceiling that turns the agent off for everyone.
- **Not now**: extra-hour packs (only if customers ask), customer API keys on hosted Kupro (secret storage, and a
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
- **POS-004**: Both plans are Alunta license plans: seat changes, proration and renewals need no billing logic of ours.
- **POS-005**: Yearly brings cash up front, and the next invoice can be read off the member list.

### Negative

- **NEG-001**: Status adds a check to every authorization path (deactivated members must be rejected).
- **NEG-002**: Until OQ-2 is settled, agent-heavy workspaces have no clear price.
- **NEG-003**: Allowances in hours are approximate: an hour of dense speech with a large workspace costs more than a quiet one.
- **NEG-004**: Every invite is a purchase decision, so some organisations will onboard fewer people. Idle-seat
  alerts help owners clean up before renewal.

## Alternatives Considered

### Per-user billing across workspaces

- **ALT-001**: **Description**: One subscription per user, covering every workspace they belong to.
- **ALT-002**: **Rejection Reason**: Teams buy tools, not individuals; it doesn't match how startups pay.

### Flat price per workspace

- **ALT-003**: **Description**: One price regardless of team size.
- **ALT-004**: **Rejection Reason**: Under-charges larger teams and makes seat-based enterprise upsell harder.

### Pay only for people active that month (fair billing)

- **ALT-005**: **Description**: A seat is a member who used Kupro in the calendar month, so idle people cost €0.
  Monthly billed in arrears; yearly either prepaid with idle seat-months returned as credit, or a 12-month agreement
  still billed monthly (tried 2026-10-17 to 18).
- **ALT-006**: **Rejection Reason** (2026-10-18): it doesn't combine with a yearly plan paid up front. Credit needs a
  ledger Alunta doesn't have, and a yearly plan billed monthly isn't a yearly plan. Idle-seat alerts let owners spot
  unused seats themselves, with none of the billing code.

### Founding-team discount

- **ALT-007**: **Description**: 50 % off for the first 25 paying workspaces for as long as they stay, in return for a
  quarterly feedback call; stacked with yearly, €45 a seat a year.
- **ALT-008**: **Rejection Reason** (2026-10-18): the meeting agent's allowance would cost up to 37 % of a founding
  yearly seat ([ADR-015](ADR-015-meeting-agent-vendors.md) IMP-006 cost), more than we can carry before revenue.

## Implementation Notes

- **IMP-001**: Task 003 adds `Member.Status` and deactivate (not delete) semantics.
- **IMP-002**: Deactivating a member revokes their API tokens ([ADR-005](ADR-005-auth-keycloak.md)), and their agent tokens with them.
- **IMP-003**: Member count index: `(workspace_id, kind, status)`.
- **IMP-004**: Billing task is written once OQ-2 and OQ-3 are resolved; it is not part of v1.
- **IMP-005**: The AI budget, meters and guards are [task 017](../../tasks/completed/017-meeting-agent-budget.md). Until billing
  exists, every workspace gets the paid allowance for its active human Owners and Members, and a Scaleway billing alert
  is the outer brake.
- **IMP-006**: `Member.LastActiveAt`, set where a request's member is resolved (an agent token sets it for the human
  it acts for). At most one write per member per day, behind an in-memory guard ([ADR-010](ADR-010-performance-budget.md)).
  Drives the idle-seat flag and the renewal emails.
- **IMP-007**: Alunta mapping ([API reference](https://app.alunta.com/docs/v1)), checked 2026-10-18:
  - Two `license_based` plans, Monthly (€9) and Yearly (€90), with `license_decrease_behavior: next_renewal`: an added
    seat is prorated from the day it is added, a removed one is billed until renewal.
  - Kupro sets the subscription's quantity to the seat count whenever it changes
    (`PUT /subscriptions/{uuid}/quantity`), retried through an outbox if Alunta is down.
  - The trial stays in Kupro; the subscription is created through Alunta's checkout when the customer subscribes.
  - Custom subscriptions are created by hand in Alunta (any interval, invoice payment). Kupro links the workspace
    to the subscription and syncs the quantity the same way, unless the agreement fixes the seat count.
  - Webhooks (`invoice.*`, payment and subscription events) drive the read-only state for unpaid workspaces.
- **IMP-008**: The billing-access flag, a `[BillingAccess]` guard, the guest cap (`Members:GuestsPerSeat`, 5 on
  hosted, unset on self-hosted) and the members page are [task 018](../../tasks/completed/018-members-billing-access.md).
  Task 017's usage view and budget emails use that guard.

## Related Decisions

- [ADR-005](ADR-005-auth-keycloak.md) — identity and tokens; roles live in Kupro
- [ADR-006](ADR-006-eu-vendor-policy.md) — Alunta as billing vendor
- [ADR-007](ADR-007-licensing.md) — open-core boundary (OQ-3)
- [ADR-008](ADR-008-work-item-model.md) — agents as members
- [ADR-009](ADR-009-multi-tenancy.md) — workspace as tenant and billing unit
- [ADR-015](ADR-015-meeting-agent-vendors.md) — the speech and LLM vendors the AI budget pays for
