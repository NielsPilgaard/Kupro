---
title: 'Task 007: Web UI — list, board, issue detail, command palette'
description: >-
  App shell, issue list and board views, issue detail with markdown editor, command palette and keyboard shortcuts,
  optimistic mutations.
status: 'Done'
phase: 1
depends_on: [000, 004]
purpose: 'Implementation spec for task 007.'
---

# Task 007: Web UI — list, board, issue detail, command palette

Follow [design-guide.md](../../docs/design-guide.md) (tokens, shell, components) and [branding.md](../../docs/branding.md) (voice, glossary).

## Scope

- App shell: workspace/project switcher, sidebar, i18n ([ADR-011](../../docs/adr/ADR-011-i18n.md)).
- List view (group/filter/sort), board view (drag between states), issue detail (markdown editor, fields, relations, activity).
- Command palette (`Ctrl/Cmd+K`); shortcuts (`C` create, `E` edit, `S` state, `A` assign, ...).
- Optimistic updates with rollback.

## Acceptance criteria

- [x] Every action reflected in UI < 50 ms (optimistic cache patches for update and move; rollback + toast on error).
- [x] Full issue lifecycle possible keyboard-only (C create, J/K/Enter, S/A/P/L pickers, E edit, Esc close, Ctrl+K).
- [x] Playwright E2E: create -> move -> close (`tests/e2e`, runs against `aspire run`).

## Implementation notes

- Routes: `/` (onboarding or first workspace), `/w/:workspace/p/:project?view=list|board`, `/w/:workspace/my-issues`,
  `/w/:workspace/new-project`, `/settings`. `?issue=KEY` opens the detail panel over any view, so it is linkable.
- The command palette (cmdk) is also the picker for state, assignee, priority and labels, and its footer shows the
  equivalent `flowstate` command. Board drag uses dnd-kit; `S` changes state without a mouse.
- Optimistic updates patch every cached issue list and the detail in the same frame (`web/src/api/issues.ts`).
- Settings has language and API tokens, so people can create tokens for agents without the CLI.
- Project colours and the backdrop scrim are tokens; checked in light and dark.
- Activity in the detail panel comes with task 008 (the feed API is built there).
- The E2E suite is not in CI yet: it needs the whole Aspire stack (Keycloak included) on the runner.
