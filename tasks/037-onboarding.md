---
title: 'Task 037: First-run onboarding'
description: >-
  Kupro has no onboarding. Add a short, skippable first run for each person, whose first question sets their voice
  preference (task 036): "Do you want to talk to Kupro?" Yes picks the thumb button, no keeps the top bar mic. Every
  answer can be changed later in profile settings.
status: 'Todo'
phase: 2
depends_on: ['036']
purpose: 'Implementation spec for task 037.'
---

# Task 037: First-run onboarding

## TL;DR

- **Why**: the owner's pick for voice control (2026-11-27, [task 036](036-voice-control.md)) makes the voice button a
  per-person setting. The default (top bar mic) suits most people, but the people voice helps most (dyslexia, field
  work, little time) should get the thumb button without hunting for a setting. So Kupro asks once.
- **What**: a first run shown once to each person after their first sign-in in a workspace. It starts with one
  question and grows only when another setting earns a place.
- **Rule**: every step is skippable, and every answer is a normal setting that profile settings can change. Nothing
  in onboarding is only reachable from onboarding.

## Scope

- **Step 1, voice**: "Do you want to talk to Kupro?" with a short line on what that means (push to talk to change
  issues, go somewhere or dictate). Yes sets `voiceButton: thumb`; no keeps `topBar`. Skipping changes nothing.
- **Shown once**: a per-person flag (for example `User.OnboardedAt`), set on finish or skip. People who joined before
  this ships see it once too.
- **Not for agents**: agent tokens and the CLI never see onboarding; the setting itself has parity through task 036's
  preference (`PATCH /me`, the CLI and profile settings).
- **Prototype first** (AGENTS.md rule 11): a new flow, so `prototypes/onboarding/` with a few directions (a card on
  the first screen, a full-screen step, a banner) before `web/` code. The owner picks; this file records it.
- i18n EN/DA ([ADR-011](../docs/adr/ADR-011-i18n.md)), design tokens only ([design guide](../docs/design-guide.md)),
  keyboard and screen reader friendly, works at 360 px.

## Acceptance criteria

- [ ] A person signing in for the first time sees the voice question once; answering yes gives them the thumb
      button at once.
- [ ] Skipping, or answering no, leaves the top bar mic; the question doesn't come back.
- [ ] The same choice is in profile settings, and changing it there takes effect at once.
- [ ] Existing members see it once after release.
- [ ] Agents and the CLI never get onboarding.

## Out of scope

- Workspace setup (projects, invites, import): that is the workspace's own first run, a separate task if needed.
- Product tours or tooltips.
