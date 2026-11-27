---
title: 'Task 035: Workspace AI connections (bring your own key)'
description: >-
  Let a workspace connect its own AI provider (a Scaleway key now, or a custom OpenAI-compatible EU endpoint), route
  each AI capability to a connection, and skip the AI budget for calls on it. Build the provider registry and the
  capability router that make Scaleway replaceable. Implements ADR-022.
status: 'Todo'
phase: 2
depends_on: ['015', '017', '018']
purpose: 'Implementation spec for task 035.'
---

# Task 035: Workspace AI connections (bring your own key)

## TL;DR

The rules are in [ADR-022](../docs/adr/ADR-022-ai-providers-byok.md). This task builds them:

- a provider registry (`scaleway`, `openai-compatible`);
- tenant-owned connections and routes, with encrypted keys;
- a router that every AI call goes through;
- metering by who pays;
- the connect flow in workspace settings;
- REST and CLI, with MCP able to read status only.

The meeting agent is the first feature on it. Voice control ([task 036](036-voice-control.md)) is the second.

**Before code**: ADR-022 accepted, and prototypes for the settings screen (see [UI](#ui)).

## Scope

### Provider registry and router

- `AiProviderType` registry in code: id, authentication, endpoint (fixed or custom), capabilities, default models,
  list prices. Scaleway's rates move out of `src/Kupro.AppHost/MeetingAgent.cs` into its entry, and the pricing skill's
  `pricing.json` stays the source for them.
- `AiCapability`: `transcription`, `meeting-proposals`, and `voice-commands` (declared here, used in task 036).
- `IAiRouter.Resolve(workspace, capability, language)` returns, in this order: the workspace route for the
  language, the workspace route for every language, the server route (`MeetingAgent:*`), or nothing. The result
  carries the endpoint, model, key, reasoning effort, rates and `PaidBy`. It is cached in the workspace cache and
  dropped when a connection or route changes.
- `OpenAiSpeechToText` and `OpenAiProposalExtractor` take the resolved route as an argument; they no longer read
  `IOptions`. `ISpeechToText.Supports` becomes a router question per workspace, so the microphone shows when the
  workspace's own route serves the language.
- `KeywordProposalExtractor` becomes what the router gives when nothing serves `meeting-proposals`. It is no longer
  chosen once at startup in `Program.cs`.

### Connections, routes, keys

- Entities `AiConnection` and `AiRoute`, with `WorkspaceId`, under the global query filter, and a migration. Fields as
  in ADR-022 decision 3.
- `AiKeys` (alongside `ImportTokens`): Data Protection purpose `Kupro.Ai.ConnectionKey` plus the connection's id; the
  key ring in the `KeyStorage` bucket. Saving fails with `409 key_storage_not_configured` when there is no bucket.
- Check on save and on demand: `GET {endpoint}/models` with the key; every routed model must be listed. The result
  sets the connection's status and keeps the vendor's error text.
- A custom endpoint on hosted Kupro:
  - HTTPS only, no redirects;
  - private, loopback, link-local and metadata addresses refused on every call (the import guard and its
    `AllowPrivateAddresses` switch);
  - the EU confirmation required, and written to the activity log.
- Removing a connection also removes its routes. The capabilities then go back to Kupro's server route, inside the
  budget.

### Budget, failure, guards

- `MeetingUsage` → `AiUsage` with `Capability` and `PaidBy`, keeping last month's rows. `MeetingBudget` counts only
  rows where Kupro paid, against the budget and the daily ceiling.
- `PaidBy = workspace`:
  - no budget check;
  - no daily ceiling;
  - the 3-hour meeting limit is off;
  - `max_tokens` and the 10-minute silence pause stay.
- 401 or 403 from a workspace's own connection:
  - the connection becomes `rejected`;
  - audio gets `409 ai_connection_rejected`;
  - text gets keyword proposals;
  - members with billing access get one email (EN/DA).

  429, 5xx and timeouts fall back for that call only. Kupro's key is never used in place of the workspace's own
  key.
- Usage (meeting settings, the meeting panel, the CLI, the MCP `meeting action=usage`) shows the workspace's own
  usage apart from Kupro's: minutes and tokens, plus an estimate in euro where the provider type has prices.
- Each proposal records the model that made it. Suggest-mode stats can be filtered by model, and a model that isn't
  the provider's default is marked "not tested by Kupro" next to the full-auto toggle.

### API, CLI, MCP

- REST under `api/v1/workspaces/{workspace}/ai/…`:
  - list provider types (with capabilities and default models);
  - connections: list, add, get, replace key, check, remove;
  - routes: list, set, remove.

  Guard: `[BillingAccess]` (Owners and members with the flag). Agents get `403`.
- CLI:
  - `kupro ai providers`;
  - `kupro ai connections list|add|check|set-key|remove` (the key from stdin or a hidden prompt; refused as an
    argument);
  - `kupro ai routes list|set|remove`.
- MCP: `ai` with `action=status`, read-only (connections without keys, routes, usage by who paid). Writes are an
  explicit opt-out in the parity test, with the reason "secrets must not pass through model context"
  ([ADR-001](../docs/adr/ADR-001-agent-parity.md)).

### UI

- Prototypes first (AGENTS.md rule 11): [prototypes/ai-connections/](../prototypes/ai-connections/index.html) has three
  directions for the workspace settings "AI" section (01 one card, 02 connections and routes, 03 usage first).
  **Pick: not made yet.** They cover:
  - the Scaleway connect flow: guide, paste key, check, done;
  - a custom endpoint with the EU confirmation;
  - capability routes;
  - the rejected-key state.

  The owner picks one, and this file records the pick.
- The fast path is the point: "Connect Scaleway", paste the key, check, done. Every capability routes to the new
  connection with Kupro's default models. Routes and model overrides sit under "Advanced".
- A short guide to making a Scaleway key (EN/DA), linked from the flow. Every string through i18n
  ([ADR-011](../docs/adr/ADR-011-i18n.md)), tokens only ([design guide](../docs/design-guide.md)).

## Acceptance criteria

- [ ] An Owner connects a Scaleway key. The check calls `/models`, and from then on meeting audio and proposals for
      that workspace go to that key. A test with a fake vendor proves this.
- [ ] Calls on a workspace's own key are recorded with `PaidBy = workspace` and never return `meeting_budget_spent`,
      even when Kupro's budget for the workspace is spent and the daily ceiling is reached.
- [ ] A 401 from a workspace's own key rejects the connection, stops audio with `409 ai_connection_rejected`, gives
      keyword proposals for text, sends one email, and never calls Kupro's key (asserted).
- [ ] Two workspaces with different connections are never mixed up. A test runs them concurrently, and the cache
      drops an entry when a key is replaced.
- [ ] A self-hosted Whisper as a custom endpoint serves `transcription` while Scaleway serves `meeting-proposals`.
- [ ] Hosted: a custom endpoint on HTTP, or one resolving to a private or metadata address, is refused. The EU
      confirmation is required and appears in the activity log.
- [ ] No API response, log line, trace or export contains a key. A test searches each of them for a known key.
- [ ] A Member without billing access, and an agent token, get `403` on every write.
- [ ] The parity test passes, with MCP writes listed as an explicit opt-out.
- [ ] Self-hosted with only `MeetingAgent:*` configuration behaves exactly as before.
- [ ] Trial and non-profit workspaces can connect a key and use the meeting agent without limits.

## Manual steps

- Privacy policy and DPA wording for workspace-chosen AI providers (ADR-022 decision 6): legal review.
- A real Scaleway account other than Kupro's own, to try the connect flow end to end.

## Out of scope

- Voice control itself ([task 036](036-voice-control.md)).
- An Owner's spend cap on their own key (ADR-022 OQ-1).
- Named provider types beyond Scaleway (Mistral, syv.ai): each is a registry entry and an ADR-006 row when wanted.
