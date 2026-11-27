---
title: 'ADR-022: AI providers and workspace AI connections (bring your own key)'
status: 'Proposed'
date: '2026-11-26'
authors: 'Niels'
tags: ['ai', 'vendors', 'tenancy', 'billing', 'security']
supersedes: ''
superseded_by: ''
description: >-
  Workspaces can connect their own AI provider: a Scaleway Generative APIs key, or on hosted Kupro any public HTTPS,
  OpenAI-compatible endpoint the Owner confirms is in the EU and under their control. Providers are entries in a
  registry and capabilities (transcription, meeting proposals, voice commands) are routed per workspace, so Scaleway
  can be swapped for a self-hosted Whisper or another LLM vendor without code changes in the features. Calls on a
  workspace's own connection skip the AI budget (unlimited), and when that connection fails the feature degrades;
  it never falls back to Kupro's key. Changes the "no customer API keys on hosted" line in ADR-013.
---

# ADR-022: AI providers and workspace AI connections (bring your own key)

## TL;DR

- **Why**: unlimited voice in Kupro with minimal friction. A workspace pastes its own Scaleway key and the meeting
  agent and voice control ([task 036](../../tasks/036-voice-control.md)) stop counting hours.
- **No lock-in**: the AI work in Kupro is split into **capabilities** (transcription, meeting proposals, voice
  commands), served by **connections** that are instances of a **provider type** from a registry. Scaleway is the only
  named provider type now. An OpenAI-compatible custom endpoint is the second, so a self-hosted Whisper or vLLM works
  today. A new vendor is one registry entry, plus an adapter only if its API is not OpenAI-compatible.
- **Routing**: a workspace route (capability → connection + model) wins over Kupro's server route
  ([ADR-015](ADR-015-meeting-agent-vendors.md) decision 4, which stays as the default layer).
- **Budget**: a call on a workspace connection is metered for display but takes nothing from the AI budget
  ([ADR-013](ADR-013-seats-billing.md)) and nothing from the server's daily ceiling. Allowed in every plan, the
  trial and the non-profit plan included.
- **Failure**: degrade, never borrow. The microphone stops, typed text uses keyword matching, and the people with
  billing access get an email. Kupro's key is never used in place of a workspace's own key.
- **Hosted custom endpoints**: public HTTPS only, private addresses blocked, and an Owner confirms the endpoint is
  in the EU and controlled by them; the confirmation is logged.
- **Who**: Owners and members with billing access manage connections over REST, web and CLI. MCP can read their
  status and opts out of writing them, so keys never pass through a model's context.
- **Where**: core (AGPL, [ADR-007](ADR-007-licensing.md)), so self-hosters get the same setup.

## Status

**Proposed** 2026-11-26. The owner made each decision below in a Q&A on 2026-11-26. This becomes Accepted once the
owner has read this text.

## Context

- The meeting agent runs on Kupro's Scaleway key ([ADR-015](ADR-015-meeting-agent-vendors.md)) inside a monthly AI
  budget of 2 hours per seat ([ADR-013](ADR-013-seats-billing.md), [task 017](../../tasks/completed/017-meeting-agent-budget.md)).
  That cap is right for Kupro's margin, but wrong for a team that wants to talk to Kupro all day, and it stops
  non-profits (0 hours) entirely.
- Voice control outside meetings ([task 036](../../tasks/036-voice-control.md)) helps people who find spelling hard,
  have little time, or work in the field. It only lowers friction if people can use it without counting hours.
- ADR-013 ruled out customer keys on hosted Kupro for two reasons:
  - **Secret storage**: since solved. Import source tokens are encrypted at rest with Data Protection, with the key
    ring in its own bucket ([ADR-019](ADR-019-import.md), `ImportTokens`).
  - **A key could point at a non-EU vendor**: handled below by a provider registry and an Owner's confirmation.
- The code is already partly vendor-neutral: `ISpeechToText` and `IProposalExtractor` work with any OpenAI-compatible
  endpoint. Two things tie it to one vendor:
  - routes are read from server-wide `IOptions<MeetingAgentOptions>`, so they cannot vary by workspace;
  - the extractor implementation is picked once, at startup.

## Decision

### 1. Provider types (a registry, in code)

A provider type describes a vendor. It sets:

- how to authenticate;
- which endpoint to use: fixed, or entered by the workspace;
- which capabilities it can serve: audio transcription, and chat with a JSON schema;
- which models Kupro tested and uses by default;
- the list prices used to estimate cost.

| Type | Endpoint | Capabilities | Defaults | On hosted |
|---|---|---|---|---|
| `scaleway` | Fixed: `https://api.scaleway.ai/v1`, or `…/{projectId}/v1` when a project is given | transcription, chat | the ADR-015 models | Yes |
| `openai-compatible` | Entered by the workspace (custom) | whichever the Owner ticks | none: a model is required | Yes, with the guard in decision 6 |

- To add a named vendor (Mistral, syv.ai Hviske, a future Scaleway product), add a registry entry and a Chosen row in
  the [ADR-006](ADR-006-eu-vendor-policy.md) register. Write an adapter only when the vendor's API is not
  OpenAI-compatible.
- Server-wide routes (ADR-015 decision 4, `MeetingAgent:*` configuration) are Kupro's own connection. They serve
  every workspace that has no route of its own.

### 2. Capabilities

A capability is a feature's need, named once, with the provider feature it requires:

| Capability | Needs | Used by |
|---|---|---|
| `transcription` | audio transcription | meeting audio ([task 015](../../tasks/015-meeting-agent.md)); voice commands and dictation ([task 036](../../tasks/036-voice-control.md)) |
| `meeting-proposals` | chat with JSON schema | the meeting extractor |
| `voice-commands` | chat with JSON schema | voice control (task 036) |

A new AI feature declares a capability and asks the router for it. It never reads configuration or keys itself.

### 3. Connections and routes (tenant-owned, [ADR-009](ADR-009-multi-tenancy.md))

- **AI connection** fields:
  - `WorkspaceId`, `ProviderType`, `Name`;
  - `Endpoint` (custom only) and `ProjectId` (Scaleway, optional);
  - `EncryptedKey` and the key's last 4 characters;
  - `Status`: `unverified`, `healthy` or `rejected`, plus `LastError` and `LastCheckedAt`;
  - the EU confirmation (custom only): who gave it and when;
  - `CreatedBy`.

  A workspace may have several connections.
- **AI route**: `WorkspaceId`, `Capability`, `ConnectionId`, `Model` (null means the provider's default) and
  `Language` (null means every language). Per-language routes exist in the API and CLI. The web shows one route per
  capability.
- **Resolution**: `IAiRouter.Resolve(workspace, capability, language)` returns, in this order:
  1. the workspace route for that language;
  2. the workspace route for every language;
  3. Kupro's server route.

  The result says who pays: `Workspace` or `Kupro`. It is cached in the workspace cache
  ([ADR-010](ADR-010-performance-budget.md)) and dropped when a connection or route changes. The key is decrypted
  only when a call is made.
- The seams receive the resolved route; they no longer read `IOptions` themselves. `KeywordProposalExtractor` becomes
  what the router returns when nothing serves `meeting-proposals`, not a choice made at startup.

### 4. Keys

- Keys are encrypted with Data Protection, purpose `Kupro.Ai.ConnectionKey`, with the connection's id as a
  sub-purpose. The key ring is the same bucket as the import tokens, so a database dump alone can't decrypt them.
  Without key storage, connections can't be saved; self-hosters set `KeyStorage` or use server routes.
- Keys are write-only. The API returns only `hasKey` and the last 4 characters. Replacing a key is a new write.
- The CLI reads a key from stdin or a hidden prompt, never from an argument, so it stays out of shell history.
- HTTP logging and traces redact the `Authorization` header to the AI clients
  ([ADR-014](ADR-014-observability.md)).
- **Check on save**: Kupro calls `GET /models` with the key and checks that each routed model is in the list. A key
  that fails the check is saved as `rejected`, with the vendor's error, so the Owner sees why.

### 5. Budget and guards

- A call on a workspace connection is written to the usage ledger with `paidBy = workspace`. It is shown in usage
  (minutes, tokens, and an estimate in euro at the provider type's list price; for a custom endpoint, minutes and
  tokens only). It takes nothing from the AI budget and nothing from the server's daily ceiling.
- The ledger records who paid (`MeetingUsage.PaidBy`), and gains a capability column with voice control
  ([task 036](../../tasks/036-voice-control.md)), so the usage view can show the meeting agent and voice control apart.
- **Allowed in every plan**: trial, paid and non-profit. A non-profit with its own key gets the meeting agent and
  voice control, at no cost to Kupro.
- **Guards that stay on a workspace's own key**: `max_tokens` (stops a runaway call) and the 10-minute silence
  pause (privacy: nobody leaves a microphone on by mistake).
- **Guard that goes**: the 3-hour meeting limit. It exists only to cap Kupro's cost.

### 6. Custom endpoints on hosted Kupro

- HTTPS only. No redirects. Short timeouts.
- The address is resolved on every call, and loopback, private, link-local and cloud-metadata addresses are
  refused. This is the import guard (`ImportOptions.AllowPrivateAddresses`); self-hosters can turn it off to reach a
  Whisper server on their own network.
- An Owner ticks "This endpoint is in the EU/EEA and run by us or our processor". The connection keeps who ticked it
  and when, and the API logs it. A workspace-level activity log, when Kupro gets one, should show it too.
- A custom endpoint is the workspace's own processor, under its own agreement, so it doesn't go in Kupro's ADR-006
  register. The privacy policy and DPA must say that audio and text go to the provider the workspace chose, while it
  has one (a manual legal step).

### 7. Failure: degrade, never borrow

Once a workspace has a route for a capability, that capability never uses Kupro's key. The only way back to Kupro's
key is to remove the route.

| Error | What happens |
|---|---|
| 401 or 403 | The connection becomes `rejected`. The microphone stops (`409 ai_connection_rejected`, with the web explaining). Typed text gets keyword proposals (`extractor: "keyword"`). Everyone with billing access gets an email (EN/DA), once per rejection. |
| 429, 5xx, timeout | Only that call falls back, as ADR-015 IMP-007 already does. The status doesn't change. |

Saving a new key, or a check that passes, sets the connection back to `healthy`.

### 8. Models

- The Scaleway provider type uses the models Kupro tested (ADR-015 IMP-006) unless the route names another.
- A different model is allowed. The web marks it "not tested by Kupro" next to the full-auto toggle.
- Each proposal records the model that made it, so suggest-mode approval stats can be split by model
  ([task 015](../../tasks/015-meeting-agent.md)).

### 9. Access and parity ([ADR-001](ADR-001-agent-parity.md))

- **Who**: Owners, and members with billing access ([task 018](../../tasks/completed/018-members-billing-access.md)).
  A key moves the cost to the workspace's own vendor account, so it is a billing decision.
  Agents never manage connections.
- **REST and CLI**: list, add, check, replace key, remove; set and remove routes (`kupro ai connections …`,
  `kupro ai routes …`).
- **MCP**: an explicit opt-out for writes. MCP can read status: configured, healthy, rejected, the last error and
  usage by who paid. It never sees a key.

## Consequences

### Positive

- **POS-001**: Unlimited voice for any workspace that wants it, at no AI cost to Kupro, the trial and non-profits
  included.
- **POS-002**: Swapping vendors is configuration: a self-hosted Whisper for Danish, another LLM for proposals, or a new
  provider type for everyone. Features only name capabilities.
- **POS-003**: Self-hosters get the same multi-provider setup as hosted (core).
- **POS-004**: Removes ADR-013's two objections without new vendors in Kupro's own register.

### Negative

- **NEG-001**: Hosted Kupro now sends customer data to endpoints Kupro doesn't vet. The EU confirmation is a
  statement by the customer, not a check. Mitigations: the private-address guard, the logged confirmation, and the
  DPA wording.
- **NEG-002**: A route per workspace adds a lookup (cached) and a decryption to every AI call.
- **NEG-003**: Untested models can make full auto worse. Mitigations: the "not tested" mark and approval stats per
  model.
- **NEG-004**: Support load: "my key doesn't work" questions. Mitigations: the check on save, the vendor's error
  shown verbatim, and a step-by-step Scaleway guide in the connect flow.
- **NEG-005**: When a workspace's key fails, its meeting stops listening even if it has budget left on Kupro's key.
  That is deliberate (decision 7), but owners may be surprised.

## Alternatives considered

### Allowlisted providers only on hosted

- **ALT-001**: **Description**: hosted workspaces pick only named provider types; custom URLs only for self-hosters.
- **ALT-002**: **Rejection reason** (2026-11-26): it keeps a team that runs its own Whisper or LLM in the EU locked
  out of hosted Kupro. The guard in decision 6 plus the Owner's confirmation is enough.

### Use the included hours first, then the workspace's key

- **ALT-003**: **Description**: spend the 2 hours per seat on Kupro's key, then switch to the workspace's key.
- **ALT-004**: **Rejection reason**: two payers in one month, and the audio's processor changes mid-month. Hard to
  explain and to put in a DPA.

### Fall back to Kupro's key when the workspace's key fails

- **ALT-005**: **Description**: keep the meeting running on Kupro's key, within the normal budget.
- **ALT-006**: **Rejection reason**: the workspace's audio would move to a processor account it didn't choose, without
  anyone noticing.

### One connection for everything

- **ALT-007**: **Description**: one provider and key per workspace serves every capability.
- **ALT-008**: **Rejection reason**: it rules out the setup this ADR exists for, such as a self-hosted Whisper with
  Scaleway for proposals.

### In `ee/`

- **ALT-009**: **Rejection reason**: "no lock-in" should hold for self-hosters too, and nothing here is billing code.

## Open questions

- **OQ-1**: An optional monthly spend cap on a workspace's own key, entered by the Owner, so a team can't run up its
  own Scaleway bill by accident. Leaning: later, if anyone asks. Scaleway has billing alerts of its own.
- **OQ-2**: Should users see when a meeting runs on the workspace's own key (for example "Own Scaleway key" in the
  meeting panel)? Leaning: yes, in the panel's usage line.
- **OQ-3**: Personal connections (a member's own key) for voice control. Leaning: no. Connections belong to the
  workspace, the billing unit.

## Implementation notes

- **IMP-001**: [Task 035](../../tasks/035-ai-connections.md) builds this. [Task 036](../../tasks/036-voice-control.md)
  is the first feature to use it for a new capability.
- **IMP-002**: Scaleway key: an IAM API key with Generative APIs model access, from the customer's own Scaleway
  project. Check the permission set's exact name and the project-scoped URL against Scaleway's documentation when
  building. The connect flow links to a short guide (EN/DA, [ADR-011](ADR-011-i18n.md)).
- **IMP-003**: No new production parameter. Keys live in the database, and the key ring in the existing `KeyStorage`
  bucket ([deploy runbook](../runbooks/deploy.md)).
- **IMP-004**: Self-hosted: server routes (`MeetingAgent:*`) keep working unchanged as Kupro's own connection; with no
  `Budget` section there is still no limit.

## Related decisions

- [ADR-001](ADR-001-agent-parity.md): parity, and the MCP opt-out for writes.
- [ADR-006](ADR-006-eu-vendor-policy.md): named provider types must be in the register; custom endpoints are the
  customer's processors.
- [ADR-007](ADR-007-licensing.md): core, not `ee/`.
- [ADR-009](ADR-009-multi-tenancy.md): connections and routes are tenant-owned.
- [ADR-013](ADR-013-seats-billing.md): the AI budget a workspace's own key skips; changes its "Not now" line.
- [ADR-015](ADR-015-meeting-agent-vendors.md): Kupro's default models and server routes.
- [ADR-019](ADR-019-import.md): the encryption and private-address guard reused here.
