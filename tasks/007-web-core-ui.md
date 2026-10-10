---
title: 'Task 007: Web UI — list, board, issue detail, command palette'
description: >-
  App shell, issue list and board views, issue detail with markdown editor, command palette and keyboard shortcuts,
  optimistic mutations.
status: 'Todo'
phase: 1
depends_on: [000, 004]
purpose: 'Implementation spec for task 007.'
---

# Task 007: Web UI — list, board, issue detail, command palette

Follow [design-guide.md](../docs/design-guide.md) (tokens, shell, components) and [branding.md](../docs/branding.md) (voice, glossary).

## Scope

- App shell: workspace/project switcher, sidebar, i18n ([ADR-011](../docs/adr/ADR-011-i18n.md)).
- List view (group/filter/sort), board view (drag between states), issue detail (markdown editor, fields, relations, activity).
- Command palette (`Ctrl/Cmd+K`); shortcuts (`C` create, `E` edit, `S` state, `A` assign, ...).
- Optimistic updates with rollback.

## Acceptance criteria

- [ ] Every action reflected in UI < 50 ms.
- [ ] Full issue lifecycle possible keyboard-only.
- [ ] Playwright E2E: create -> move -> close.
