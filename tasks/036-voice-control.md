---
title: 'Task 036: Voice control'
description: >-
  Talk to Kupro anywhere, not only in meetings: push to talk to change issues (the meeting agent's operations),
  navigate ("open KUP-12", "show next sprint"), and dictate into any text field. For people who find spelling hard,
  have little time, or work in the field. Uses the AI budget, or the workspace's own key for unlimited use (ADR-022).
status: 'In progress'
phase: 2
depends_on: ['035']
purpose: 'Implementation spec for task 036.'
---

# Task 036: Voice control

## TL;DR

- **What**: a push-to-talk button and a hotkey, everywhere in the web app, on desktop and phone. One utterance does
  one of three things:
  - **operations**: the meeting agent's six operations;
  - **navigation**: open, show, filter;
  - **dictation**: text into the focused field.
- **Who for**: anyone, but chiefly people who find spelling hard (dyslexia), people short on time, and people in
  the field with a phone in one hand. Minimal friction is the requirement: no setup, and nothing to learn first.
- **On what**: the `transcription` and `voice-commands` capabilities from
  [task 035](035-ai-connections.md) ([ADR-022](../docs/adr/ADR-022-ai-providers-byok.md)).
  - Without a workspace key: Kupro's key, drawing on the same monthly AI budget as meetings
    ([ADR-013](../docs/adr/ADR-013-seats-billing.md)).
  - With one: unlimited.
- **Order**: product scope change, prototypes and the owner's pick are done (01 and 03, see
  [Prototypes](#prototypes-before-any-web-code)). The API, the preference, the CLI and the web are built (see
  [Progress](#progress)); left are the measurements, a Playwright mobile run and the activity-feed snippet.

## Progress

- [x] API (`Features/Voice`): `POST …/voice/interpret` (text) and `POST …/voice/interpret/audio` (WAV) return one
      interpretation: `operations` in the meeting proposal shape (target issue, candidates, before/after with display
      values, confidence, `held` reasons, and the `update`/`undo`/`create` bodies for the issue endpoints), `navigate`
      (a structured `VoiceTarget` checked against the workspace), or `none` with a `reason`. `POST …/voice/transcribe`
      is dictation with an optional `tidy` suggestion. Interpreting changes nothing.
- [x] Model: `voice-commands` through `AiRouter` with its own JSON schema (the meeting operations plus `navigate`),
      behind the `IVoiceInterpreter` seam. Keyword fallback (`KeywordVoiceInterpreter`: the meeting rules plus EN/DA
      place patterns, "this one" as the issue on screen) when nothing serves it, the own key is refused, Kupro's
      budget is spent, or the vendor fails. Issue references are matched by our own search, shared with the meeting
      agent (`Meetings/ProposalValues.cs`, moved out of `MeetingService`).
- [x] Tidy-up is checked on the server: the same words in order, each spelled at most a third differently; anything
      else is dropped, so a person's words are never rewritten.
- [x] Budget and keys: audio and tokens are metered like meetings and draw from the same monthly budget on Kupro's
      key; `409 ai_budget_spent` for audio over budget; a workspace's own key skips the budget; a refused own key
      gives `409 ai_connection_rejected` for audio and keyword reading for text, and never borrows Kupro's key.
- [x] Preference: `voiceButton` (`topBar` default, `thumb`) and `voiceAutoApply` on the user, in `GET`/`PATCH /me`
      and `kupro me update --voice-button --voice-auto-apply`.
- [x] CLI: `kupro say "<text>"` shows the changes, asks (or `--yes`), and applies through the issue endpoints.
      Parity: MCP opts out of all three operations; the CLI opts out of the two audio ones.
- [x] Web (`web/src/features/voice`): the two button styles by preference, hold Ctrl+Space with either, a short tap
      or the keyboard latches the microphone; the card (01) or bottom sheet (03) with the diff, held reasons,
      candidate picking, Enter to apply, Escape to close, Undo in the toast; "apply without asking" for sure changes;
      navigation through the router; dictation at the cursor of the focused field with "Tidy up?" as a word diff;
      over-budget, rejected-key, no-speech-model and microphone-denied states with typing as the fallback; a
      live region announces listening, thinking and the result; profile settings for both preferences. EN and DA.
      Applied changes are sent with `X-Kupro-Client: voice`, so the feed says "via voice".
- [x] Tests: `VoiceTests` (API, budget, own and refused keys, dictation, nothing stored, preference, `kupro say`),
      `VoiceInterpreterTests` (keyword reader EN/DA, model parsing, the tidy-up guard), web `voice.test.ts` and
      `VoiceCard.test.tsx`.
- [x] The .NET integration tests run and pass (2026-11-27, with the full suite in Release). The first run found two
      budget bugs from the per-feature ledger rows, fixed on merge: the budget check now sums Kupro's rows, and the
      80/100 % warnings claim only the meetings row (regression test in `VoiceTests`).
- [ ] Measure the 3 s p50 from release to card on Scaleway defaults (OQ-1), and a Playwright mobile run at 360 px.
- [ ] The transcript as the snippet on the activity entry (changes already show "via voice").

Different from the spec, on purpose:

- **Audio has its own path**: `voice/interpret/audio` next to `voice/interpret`, as meetings have `/audio` next to
  `/transcript`. One path with two content types can't be one OpenAPI operation, and the CLI's generated client
  needs the JSON one.
- **Ledger**: `meeting_usage` gets a `feature` column (`Meetings`, `Voice`) in its key rather than a `capability`
  column: transcription serves both features, so only the feature tells the meeting agent and voice control apart
  (ADR-022 decision 5). The budget, the 80 %/100 % warnings and the AI settings' usage sum both; the warnings are
  claimed on the meetings row.
- **Apply without asking** is stored on the user (`voiceAutoApply`), like the button style, so it follows the person
  across devices.
- **Navigation targets**: issue, my issues, a project's list or board (optionally one person's issues: the project
  route gained an `assignee` search parameter), sprints (with the sprint asked for), and the delivery plan.

## Scope

### Push to talk

- Two button styles, each person's choice (owner's pick, 2026-11-27):
  - **Top bar mic** (prototype 01, the default): a Talk button next to search on every screen; results drop down as a
    card under the top bar.
  - **Thumb button** (prototype 03): a large round button within thumb reach on every screen; results in a bottom
    sheet with big buttons and large text.
- The style is a per-person preference beside language and time zone (`voiceButton: topBar | thumb`), set over the
  API (`PATCH /me`), the CLI and in profile settings.
- Onboarding asks once: "Do you want to talk to Kupro?" Yes sets the thumb button; no keeps the top bar mic. Kupro
  has no onboarding yet, so the first run itself is [task 037](037-onboarding.md); this task builds the preference
  it sets, and profile settings to change it.
- A hotkey (hold to talk, release to send) on desktop, with either style. No always-on listening.
- Audio is one utterance cut by the browser (`web/src/lib/microphone`), sent as 16 kHz WAV, kept in memory, and dropped
  after transcription, as in meetings ([ADR-015](../docs/adr/ADR-015-meeting-agent-vendors.md) IMP-004).
- Language: the user's UI language by default. The workspace's meeting languages decide which are offered.

### Operations

- Uses the meeting agent's operations: state, sprint, assignee, estimate, comment, create issue. Issue references
  are matched by our own search, never by the model; the issue on screen counts as "this one".
- A confirmation card shows the diff. One tap or Enter applies it, and the result can be undone.
  - The user can turn on "apply without asking" for themselves.
  - Even with that on, the cases that full auto holds back are still held: low confidence, an ambiguous reference,
    a destructive operation, creating an issue.
- Changes record the user as actor, via voice, with the transcript as the snippet in the activity feed.

### Navigation

- "Open KUP-12", "show next sprint", "my issues", "board for project X", "filter by Anna". The API returns a
  structured target (route plus filters), never a URL from the model. The web router carries it out; nothing changes
  on the server.

### Dictation

- With a text field focused (comment, description, title), speech is inserted at the cursor.
- An optional "tidy up" pass fixes punctuation and spelling and nothing else. It is shown as a suggestion the user
  accepts, so their words are never silently rewritten.
- Markdown fields keep Markdown. Dictation never runs an operation; it is chosen by the focused field, not guessed.

### API, CLI, MCP

- `POST api/v1/workspaces/{workspace}/voice/interpret`: audio or text in, one interpretation out:
  - `operations`, with proposals in the meeting shape;
  - `navigate`, with the target;
  - `none`, with the reason.

  Text input is for testing and for the CLI.
- `POST …/voice/transcribe`: audio in, text out (dictation), with an optional `tidy` flag.
- The usage ledger gets `Capability = voice-commands` and `transcription`. Over budget gives
  `409 ai_budget_spent`. A rejected own key gives `409 ai_connection_rejected` (task 035).
- CLI: `kupro say "move KUP-12 to next sprint"` runs `interpret` with text and applies after confirmation, the same
  flow as the web.
- MCP and the rest of the CLI ([ADR-001](../docs/adr/ADR-001-agent-parity.md)):
  - MCP: an explicit opt-out for `interpret` and `transcribe`. Agents already call the operations directly, and
    natural language to an agent's own tools adds nothing.
  - Navigation and audio are web-only: an explicit opt-out for the CLI and MCP.

### Accessibility and friction

- Reachable by keyboard and screen reader. The button's state (idle, listening, thinking) is announced, not only
  coloured.
- Works one-handed on a 360 px wide phone. A mistaken utterance costs one tap to dismiss.
- Errors say what to do next in the brand voice ([branding](../docs/branding.md)), in EN and DA.
- Opening the app on a phone and talking needs no setup beyond microphone permission.

### Prototypes (before any `web/` code)

- [prototypes/voice-control/](../prototypes/voice-control/index.html) has three directions (01 top bar mic, 02 voice in
  the command palette, 03 thumb button). **Pick (2026-11-27): 01 and 03, as a per-person setting** (see
  [Push to talk](#push-to-talk)): both are valuable, so neither replaces the other. 02 is not built. Each covers:
  - the button on desktop and on a phone;
  - an operation's card;
  - navigation;
  - dictation in a comment, with "tidy up";
  - over budget, and a rejected key.

## Acceptance criteria

- [ ] Saying "move KUP-12 to next sprint" shows a card with the diff within 3 s of release (p50, Scaleway defaults),
      and one tap applies it with undo. (Card, apply and undo built and tested; the 3 s is not measured yet.)
- [x] Saying "open KUP-12" opens the issue. A navigation target the user can't see (another workspace, no access) is
      never produced.
- [x] Dictating into a comment inserts the text at the cursor. "Tidy up" changes only spelling and punctuation, as a
      suggestion.
- [x] Danish and English both work for all three kinds.
- [x] Without a workspace key, usage draws on the AI budget and stops at it. With one, there is no limit (shared
      tests with task 035).
- [ ] Works with keyboard only and with a screen reader, and on a 360 px phone (Playwright mobile project). (Built
      for it; no Playwright run yet.)
- [x] The parity test passes, with the opt-outs above.
- [x] Audio is never stored. A test asserts nothing about an utterance outlives the request.

## Open questions

- **OQ-1**: A model for voice commands. They are short, single utterances, so a faster model than the meeting
  extractor may hold the 3 s target. Run the ADR-015 IMP-006 style spike on command samples; a different default
  model is a change to the provider registry entry, not code.
- **OQ-2**: The PWA or home-screen install for field use (offline is out of scope; the vendors are online).

## Out of scope

- Always-on wake words ("Hey Kupro").
- Chat or questions answered from data ("what's blocking the sprint?"); still out of v1
  ([product.md](../docs/product.md)).
- Voice in the CLI beyond text (`kupro say` takes text).
