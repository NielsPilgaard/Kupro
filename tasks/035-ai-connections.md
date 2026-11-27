---
title: 'Task 035: Workspace AI connections (bring your own key)'
description: >-
  Let a workspace connect its own AI provider (a Scaleway key now, or a custom OpenAI-compatible EU endpoint), route
  each AI capability to a connection, and skip the AI budget for calls on it. Build the provider registry and the
  capability router that make Scaleway replaceable. Implements ADR-022.
status: 'In progress'
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

**Before code**: ADR-022 accepted (2026-11-27), and prototype 03 picked for the settings screen (see [UI](#ui)).

## Progress

The backend and the web settings screen (prototype 03) are built against ADR-022 (accepted 2026-11-27). Per-model
approval stats are next.

- [x] Provider registry and capabilities (`Features/Ai/AiProviders.cs`), connections and routes with a migration,
      keys encrypted with Data Protection (`AiKeys`), the router (`AiRouter`) with Kupro's server route as the default
      layer.
- [x] The meeting agent asks the router: speech and extraction take the resolved route, the keyword extractor is the
      fallback when nothing serves proposals, and the microphone shows per workspace.
- [x] Budget: `meeting_usage` has a `paid_by` column. Only Kupro's row counts against the budget, the daily ceiling,
      the warnings and the spend metric. The 3-hour limit is off on a workspace's own transcription key.
- [x] Failure: 401/403 from a workspace's own key rejects the connection, returns `409 ai_connection_rejected` for
      audio, gives keyword proposals for text, and emails billing once. Kupro's key is never used instead.
- [x] REST (`api/v1/workspaces/{workspace}/ai`), CLI (`kupro ai status|providers|connections …|routes …`, key from
      stdin, `KUPRO_AI_KEY` or a hidden prompt), MCP `meeting action=ai` (status and providers, read-only; a separate tool would pass the 20-tool cap), parity test.
- [x] Custom endpoints: https, public addresses only (checked on save and on every call), no redirects, EU
      confirmation required. `Ai:AllowPrivateAddresses` lets a self-hosted Kupro reach its own network.
- [x] Web settings screen, on prototype 03 (see [UI](#ui)): `/w/:workspace/settings/ai`, linked from the sidebar, the
      command palette and the account settings page. Usage first, "Go unlimited" wizard, connection cards, the
      rejected-key banner, read-only for members without billing access. Tests in `AiSettingsPage.test.tsx`.
- [ ] Each proposal records the model that made it, and suggest-mode stats split by model.

Different from the spec, on purpose:

- **Ledger**: `MeetingUsage` keeps its name and gains `PaidBy`; the `Capability` column comes with task 036, when
  voice is the first usage that isn't a meeting.
- **EU confirmation**: kept on the connection (who and when) and in a structured log line. Kupro has no
  workspace-level activity log yet; the issue activity feed is per issue.

Web screen, different from prototype 03:

- **Where**: Kupro has no workspace settings page with tabs yet, so the page stands alone with its own route and
  sidebar entry ("AI"), instead of a tab row.
- **Usage**: two figures, Kupro's included hours (the meeting budget) and the audio and tokens on the workspace's own
  key. The prototype's bars per feature (meetings, voice, dictation) wait for the ledger's capability column (task
  036). No amount in euro anywhere (the owner's pick note), though the API still returns an estimate.
- **Step 2 adds the connection** without routes, so the vendor really checks the key; step 3 routes what was ticked.
  Cancel removes the connection the dialog added, and a retry with another key replaces it. No auto-advance after a
  passing check: a "Continue" button instead.
- **Step 1** names Scaleway's menus but not the permission set; its exact name is a code comment to confirm against
  Scaleway's console (ADR-022 IMP-002).
- **Custom endpoint**: one form opened from step 1. "Check and save" adds it with what was ticked, so the API routes
  only when the check passes. A key is required, as the API requires one.
- **No "Advanced" section**: a model other than Scaleway's tested one, and per-language routes, are set with the CLI
  (`kupro ai routes set --model`); the page shows them, with "Not tested by Kupro". The mark next to the full-auto
  toggle comes with the per-model stats.
- **Rejected state**: "Go back to included hours" is "Disconnect and use included hours", behind the same in-page
  confirmation as Disconnect.

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
- MCP: `meeting` with `action=ai`, read-only (connections without keys, routes, usage by who paid). Writes are an
  explicit opt-out in the parity test, with the reason "secrets must not pass through model context"
  ([ADR-001](../docs/adr/ADR-001-agent-parity.md)).

### UI

- Prototypes first (AGENTS.md rule 11): [prototypes/ai-connections/](../prototypes/ai-connections/index.html) has three
  directions for the workspace settings "AI" section (01 one card, 02 connections and routes, 03 usage first).
  **Pick (2026-11-27): 03 usage first.** The page leads with this month's hours and who paid; "Go unlimited" opens
  the three-step wizard (make a key, paste and check, choose what runs on it). Owner's change: don't mention
  Scaleway's price on the page. They cover:
  - the Scaleway connect flow: guide, paste key, check, done;
  - a custom endpoint with the EU confirmation;
  - capability routes;
  - the rejected-key state.

- The fast path is the point: "Connect Scaleway", paste the key, check, done. Every capability routes to the new
  connection with Kupro's default models. Routes and model overrides sit under "Advanced".
- A short guide to making a Scaleway key (EN/DA), linked from the flow. Every string through i18n
  ([ADR-011](../docs/adr/ADR-011-i18n.md)), tokens only ([design guide](../docs/design-guide.md)).

## Acceptance criteria

- [x] An Owner connects a Scaleway key. The check calls `/models`, and from then on meeting audio and proposals for
      that workspace go to that key. A test with a fake vendor proves this.
- [x] Calls on a workspace's own key are recorded with `PaidBy = workspace` and never return `meeting_budget_spent`,
      even when Kupro's budget for the workspace is spent and the daily ceiling is reached.
- [x] A 401 from a workspace's own key rejects the connection, stops audio with `409 ai_connection_rejected`, gives
      keyword proposals for text, sends one email, and never calls Kupro's key (asserted).
- [x] Two workspaces with different connections are never mixed up. A test runs them concurrently, and the cache
      drops an entry when a key is replaced.
- [x] A self-hosted Whisper as a custom endpoint serves `transcription` while Scaleway serves `meeting-proposals`.
- [x] Hosted: a custom endpoint on HTTP, or one resolving to a private or metadata address, is refused. The EU
      confirmation is required and appears in the activity log.
- [ ] No API response, log line, trace or export contains a key. A test searches each of them for a known key.
      (API responses and CLI output are tested; logs, traces and the export are not yet.)
- [x] A Member without billing access, and an agent token, get `403` on every write.
- [x] The parity test passes, with MCP writes listed as an explicit opt-out.
- [x] Self-hosted with only `MeetingAgent:*` configuration behaves exactly as before.
- [ ] Trial and non-profit workspaces can connect a key and use the meeting agent without limits. (Nothing blocks
      it; there are no trial or non-profit plans to test against until billing exists.)

## Manual steps

- Privacy policy and DPA wording for workspace-chosen AI providers (ADR-022 decision 6): legal review.
- A real Scaleway account other than Kupro's own, to try the connect flow end to end.

## Out of scope

- Voice control itself ([task 036](036-voice-control.md)).
- An Owner's spend cap on their own key (ADR-022 OQ-1).
- Named provider types beyond Scaleway (Mistral, syv.ai): each is a registry entry and an ADR-006 row when wanted.
