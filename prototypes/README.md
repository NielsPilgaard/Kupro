---
title: 'Kupro — UI prototypes'
description: >-
  Clickable HTML mockups made before any React code, so the owner can pick a direction for a feature. Not shipped,
  not linted, not translated. One folder per feature; the chosen direction is recorded in the feature's task file.
status: 'Living'
purpose: 'Explains what the prototypes folder is, how it is organised, and where decisions about it are recorded.'
---

# Kupro — UI prototypes

## TL;DR

- Throwaway HTML mockups that compare **design directions** for a feature before it is built. Open the files in a browser.
- **Not product code**: not shipped, not linted, not translated, no tests. The rules for real UI are in the
  [design guide](../docs/design-guide.md); prototypes borrow its tokens but may cut corners.
- One folder per feature: `prototypes/<feature>/`, with an `index.html` gallery and numbered files
  (`01-<idea>.html` …). Each file is self-contained: inline CSS and JS, no CDN, fonts or images.
- The direction the owner picks is recorded in the feature's task file, not here.

## Folders

| Folder | Feature | Task |
|---|---|---|
| [import/](import/index.html) | Import from other trackers: ten directions | [Task 023](../tasks/023-import-framework.md) |
| [switch/](switch/index.html) | Public-site `/switch/<source>` pages: three directions | [Task 024](../tasks/024-import-jira.md) |
| [time/](time/index.html) | Time registration: three directions | [Task 034](../tasks/034-time-registration.md) |
| [monday-columns/](monday-columns/index.html) | Import wizard: which Monday column holds what, per board: three directions | [Task 027](../tasks/027-import-github-monday-youtrack.md) |
| [ai-connections/](ai-connections/index.html) | Workspace settings → AI: own Scaleway key, own EU endpoint, what runs where: three directions | [Task 035](../tasks/035-ai-connections.md) |
| [voice-control/](voice-control/index.html) | Push to talk for changes, navigation and dictation: three directions | [Task 036](../tasks/036-voice-control.md) |

## Conventions

- Sample data is realistic and the same across a feature's files, so directions compare on equal terms.
- Each page says it is a prototype. Light theme by default, with a dark toggle.
- Colours and fonts come from the token block copied at the top of each file, mirroring `web/src/styles/tokens.css`.
- When a direction is chosen, record it (and the runner-up) in the task's spec. The folder can stay as a reference or be
  deleted once the feature ships.
