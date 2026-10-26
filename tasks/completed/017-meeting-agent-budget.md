---
title: 'Task 017: Meeting agent AI budget'
description: >-
  Meter speech seconds and LLM tokens per workspace, draw both from one monthly AI budget, warn at 80 %, fall back to
  the keyword extractor when it is spent, and add per-call, per-meeting and server-wide guards. Implements the usage
  decision in ADR-013.
status: 'Done'
phase: 2
depends_on: [015, 018]
purpose: 'Implementation spec for task 017.'
---

# Task 017: Meeting agent AI budget

## TL;DR

The rules are in [ADR-013 "Meeting agent usage"](../../docs/adr/ADR-013-seats-billing.md#meeting-agent-usage-decided-2026-10-15).
This task builds them:
- a usage ledger, and a monthly budget per workspace that speech and tokens both draw from;
- what happens when the budget runs out;
- the guards that stop one meeting, or the whole server, from running up a bill.

## Scope

- **Meters**:
  - `OpenAiSpeechToText` reports the audio length of each utterance.
  - `OpenAiProposalExtractor` reads `usage.prompt_tokens` and `usage.completion_tokens` from each response. If a
    response has no `usage`, it estimates from the text length.
  - `KeywordProposalExtractor` costs nothing.
- **Ledger**:
  - A `MeetingUsage` row per workspace per month (tenant-owned, [ADR-009](../../docs/adr/ADR-009-multi-tenancy.md)) holds
    audio seconds, input tokens, output tokens and cost in euro-cents.
  - Updated with an atomic increment, so concurrent microphones don't lose counts.
- **Rates and allowances** in configuration (`MeetingAgent:Budget`):
  - euro per audio minute, and per million input and output tokens, for each route;
  - hours per seat a month (2) and hours for a whole trial (2);
  - the euro value of one "hour", used to turn hours into cents.
  - The budget period is the calendar month for paid workspaces and the whole 30 days for a trial.
  - Until billing exists, every workspace counts as paid with its current seat count.
  - Self-hosted with no `Budget` section means no limit.
- **Before each call**: check the budget is not spent.
  - Audio over budget: `409 meeting_budget_spent`. The web panel turns the microphone off and explains why.
  - Text over budget: routed to `KeywordProposalExtractor`, and the response says so (`extractor: "keyword"`) so the
    CLI and MCP can show it.
- **Visibility**:
  - `GET …/meeting-settings` gains `usage`: hours used, hours included, and the reset date. The CLI `meeting settings` and
    the MCP settings tool show it.
  - The meeting panel shows "≈ 3.5 of 12 hours used this month" to everyone.
  - Everyone with billing access (Owners, plus members with the flag from task 018) gets an email (EN/DA) once at 80 % and once at 100 %.
- **Guards**:
  - `max_tokens` on every extraction call (already 4,000, [ADR-015](../../docs/adr/ADR-015-meeting-agent-vendors.md) IMP-007).
  - The web pauses the meeting after 10 minutes without speech. The API ends meetings live for more than 3 hours.
  - A server-wide daily ceiling in euro (configuration): when reached, every workspace falls back as if over budget,
    and an alert fires ([ADR-014](../../docs/adr/ADR-014-observability.md)).
- **Metrics**: counters for audio seconds, tokens and cost by route. A Grafana panel shows spend per day and the top
  workspaces.

## Acceptance criteria

- [x] Audio and LLM usage are recorded per workspace per month; tests use fake seams that report known seconds and tokens.
- [x] Concurrent utterances in one workspace are counted exactly (parallel test).
- [x] Over budget: audio gets `409 meeting_budget_spent`, and text produces keyword proposals with `extractor: "keyword"`.
      Both work the same through REST, CLI and MCP; parity test passes.
- [x] Usage shows in settings (web, CLI, MCP) and in the meeting panel; members with billing access get the 80 % and 100 % emails once each month.
- [x] Budget resets on the 1st; last month's row is kept for reporting.
- [x] `max_tokens`, the 10-minute silence pause, the 3-hour limit and the server-wide ceiling each have a test.
- [x] No `Budget` configuration means no limits (self-hosted).
- [x] Spend dashboard and the daily-ceiling alert are in `infrastructure/observability/`.

## Outcome

Built as specified, with three differences:

- **Rates live on each model route** (`MeetingAgent:Transcription:EuroPerAudioMinute`,
  `MeetingAgent:Extraction:EuroPerMillion{Input,CachedInput,Output}Tokens`), next to the model they price, and a
  language route inherits them. `MeetingAgent:Budget` holds hours per seat, euro per hour and the daily ceiling.
  Hosted values are constants in `src/Kupro.AppHost/MeetingAgent.cs` (ceiling €10 a day).
- **Trial allowance not built**: there is no trial yet, so every workspace is paid with its seats, as this spec says.
  The 30-day period and the trial's 2 hours come with billing.
- **MCP**: the settings operation stays UI/CLI-only (owner configuration); agents read usage with
  `meeting action=usage`, and `say` returns `extractor` and `budgetSpent`.

Code: `MeetingBudget.cs` (ledger, check, emails, metrics), the guards in `MeetingService`, tests in
`MeetingBudgetTests`.

## Out of scope

- Extra-hour packs, customer API keys and a higher seat tier ([ADR-013](../../docs/adr/ADR-013-seats-billing.md) "Not now").
- Choosing the extraction model: the spike in [ADR-015](../../docs/adr/ADR-015-meeting-agent-vendors.md) IMP-006. Rates
  are configuration, so a model change is a config change.
- Charging money: billing is a later task.
