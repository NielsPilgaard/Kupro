---
title: 'Task 026: Import from Linear and Trello'
description: >-
  Wave 2 connectors for the import framework: Linear (GraphQL; teams, projects, cycles, sub-issues, history) and
  Trello (boards, lists, cards, checklists, actions), with their default mappings.
status: 'Todo'
phase: 1
depends_on: [023]
purpose: 'Implementation spec for task 026.'
---

# Task 026: Import from Linear and Trello

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md); the pipeline is [task 023](023-import-framework.md). Two
connectors for the startup audience. Linear's model is closest to Kupro's; Trello's is the furthest, so its
wizard leans on hand mapping of lists.

## Scope

### Linear

- Access: personal API key (`Authorization: <key>`), GraphQL `https://api.linear.app/graphql`. Complexity-based rate
  limits: read the rate-limit headers, back off.
- Mapping: team → project (team key kept: `ENG-123`); Linear project → Epic (target date → due, lead → assignee);
  project milestones → milestones; issue → Task, promoted to Feature when it has sub-issues; deeper sub-issues fitted
  (ADR-019 Hierarchy fit). Initiatives → label on their projects' epics.
- States by type: triage and backlog → Backlog, unstarted → Todo, started → In Progress (name contains "review" →
  In Review), completed → Done, canceled → Canceled.
- Priority: Urgent → Urgent, High → High, Medium → Medium, Low → Low, No priority → None.
- Estimates: points × factor (Linear has no time estimates). Cycles → sprints.
- Relations: blocks, related, duplicate map directly.
- Description and comments are Markdown already; `uploads.linear.app` files need the key to download; mentions and
  issue URLs rewritten.
- Changelog: `issue.history` (state, assignee, priority, estimate, cycle, project, parent, labels, title).
- Delta: `updatedAt` filter.

### Trello

- Access: Kupro's Trello API key (manual step) and the user's token from Trello's authorize link, read scope.
- Board → project (owner picks the key); card `idShort` kept as the number. Lists → states, mapped by hand in the
  wizard (defaults by name: "to do", "doing", "review", "done"). Archived cards → Canceled, or skipped (owner picks).
- Labels → labels (Trello colour → nearest Kupro label colour), members → people, due/start → dates.
- Checklists → Markdown task list in the description; a checklist item with its own member or due date → Task under
  the card (card promoted to Feature) when the owner ticks it.
- Optional epic source: a label or a list the owner picks becomes an Epic, with its cards as children.
- Comments and changelog from actions (`commentCard`, `updateCard` with `listAfter`, member and due changes).
  Trello returns at most 1,000 actions per request, so actions are paged by date.
- Attachments: uploaded files downloaded with the OAuth header; link attachments kept as links.
- Custom Fields Power-Up values → labels or the details block.

### Site

- `kupro.app/switch/linear` and `kupro.app/switch/trello` (EN/DA).

## Acceptance criteria

- [ ] Recorded fixtures for each source import with no errors; plan and commit agree.
- [ ] Linear `ENG-123` keys kept; projects become epics; sub-issue trees fit ADR-008.
- [ ] Trello list mapping drives states; a board with 2,000+ actions keeps every comment.
- [ ] Re-run picks up changes in both sources without duplicates.
- [ ] One live run per source before marking Done, noted in the PR.

## Out of scope

- Linear's Asks, customer requests and SLAs. Trello Power-Ups other than Custom Fields.
- Linear OAuth "Connect" button.
