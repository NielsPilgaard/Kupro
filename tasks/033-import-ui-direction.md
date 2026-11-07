---
title: 'Task 033: Pick the import UI direction'
description: >-
  Owner decision gate for the import wizard's web UI: pick one (or a mix) of the ten prototypes in
  prototypes/import/, record the pick in task 023, and add the design-guide patterns that direction needs.
status: 'Waiting on owner'
phase: 1
depends_on: []
purpose: 'Decision gate before any web/ code for the import UI (task 023).'
---

# Task 033: Pick the import UI direction

## TL;DR

The import wizard's layout is the owner's call (AGENTS.md rule 11). Ten directions are in
[prototypes/import/](../prototypes/import/index.html). **An agent picking this up does not choose.** Ask the owner
which direction (or mix) to build, wait for the answer, then do the steps below. Until it's Done, no `web/` code for
the import UI ([task 023](023-import-framework.md) Surfaces).

## Ask the owner

1. Which direction, or which mix (e.g. "09 for the first run, 08 for re-runs, 10 for the mapping")?
2. Anything from the other prototypes to keep?

## After the answer

- Record the pick in [task 023](023-import-framework.md) Surfaces → Web, replacing the "waits on task 033" line, with
  the screens and steps the direction implies.
- Add to [design-guide.md](../docs/design-guide.md) only the patterns the chosen direction uses that the guide lacks:
  candidates are a stepper, option cards (radio cards), data grid, drawer, progress bar, diff tints
  (add/change/remove), terminal/code block, sparkline, neutral status chips, a placeholder avatar distinct from
  Unassigned's dashed circle, and how "one primary button per view" applies to a side rail with both Continue and
  Import.
- Keep the chosen prototype file(s); delete the rest only if the owner says so.
- Set this task to Done and move it to `tasks/completed/`.

## Acceptance criteria

- [ ] The owner's pick is quoted in task 023.
- [ ] Every pattern the chosen direction needs is in the design guide, with tokens only.
