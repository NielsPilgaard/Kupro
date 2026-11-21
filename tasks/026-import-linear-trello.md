---
title: 'Task 026: Import from Linear and Trello'
description: >-
  Wave 2 connectors for the import framework: Linear (GraphQL; teams, projects, cycles, sub-issues, history) and
  Trello (boards, lists, cards, checklists, actions), with their default mappings.
status: 'In progress'
phase: 1
depends_on: [023]
purpose: 'Implementation spec for task 026.'
---

# Task 026: Import from Linear and Trello

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md); the pipeline is [task 023](023-import-framework.md). Two
connectors for the startup audience. Linear's model is closest to Kupro's; Trello's is the furthest, so its
wizard leans on hand mapping of lists.

**Progress**: Trello's reading side is done: `Kupro.Import/Trello` (`TrelloConnector`, client, reading) and
`TrelloConnectorTests` against `FakeTrello`, a fake that serves the API 1 response shapes and enforces its page
limits. Key and token go in the OAuth header, to `api.trello.com` only; uploads on `trello.com` are fetched from there.
The connector reports Trello's own terms: lists as per-board statuses (archived lists have category `archived`), cards
as `card` items keyed by `idShort`, checklists as a Markdown task list in the description, checklist items with a
member or due date also as `checkItem` items under the card, the `archived`, `dueComplete` and `members` fields, and
custom fields. Cards and actions are paged by ID with `before`; the delta reads actions `since` the cursor, while
creators and archive times come from a full read of the creation and archive actions.

Linear's reading side is done too: `Kupro.Import/Linear` (`LinearConnector`, GraphQL client, queries, reading) and
`LinearConnectorTests` against `FakeLinear`, a small GraphQL executor that pages connections (`first` ≤ 250), prices
each query the way Linear documents and rejects one over 10,000 points. A personal API key goes bare in
`Authorization`, an OAuth token as `Bearer`, to `api.linear.app` only, and to `uploads.linear.app` for files. The
connector reports Linear's own terms: teams as projects with their key, workflow states with their type as category,
issues as `issue` items keyed `ENG-123` with sub-issues under their parent issue, Linear projects as `project` items
(lead, target date, labels, initiatives in a field) that parent top-level issues, milestones as versions, cycles as
sprints, grouped labels as `Group/Label`, priorities by Linear's names, estimates as points, `blocks`, `related` and
`duplicate` relations (`similar` suggestions are skipped), history as changes, comments with their replies, and files
pasted into Markdown as attachments. Issues come 50 a page with their history, relations and attachments nested (about
7,000 points), and a nested list that overflows is paged on its own; comments are paged per team. The delta filters
`updatedAt` on projects, issues and comments. The client waits out `RATELIMITED` (`Retry-After`, else the
`X-RateLimit-*` resets) and stops before the hourly requests run out.

**Trello** with 023's pipeline in place:

- Done: lists map by name through the shared defaults (`ImportMappingDefaults.ProposeState`: to do, doing, review,
  done); other lists are left to review. Cards keep `idShort` under the owner's key.
- Done: checklist items with their own member or due date are skipped by default (`ImportMappingDefaults.Trello`),
  since they are already lines in the card's task list. Ticked back in, they become Tasks under the card, and their
  lines stay in the task list too.
- Done: a card's other members (`members`, a user field) show by name in the details block.
- Open: archived cards to Canceled or skipped (today the `archived` field only), and the epic source (a label or list).
- Label colours need a place in the snapshot (an optional `labels` list in the header); today a colour-only label
  comes as its colour name and a named label's colour is not read across.
- `@username` mentions and card links (`trello.com/c/…`) need its `MarkupConverter`.
- Check: card URL aliases (`trello.com/c/…`).
- Done: the authorize link. `GET imports/trello-authorize` (`getTrelloAuthorizeLink`, `kupro import trello-authorize`,
  off MCP like the other token steps) returns Trello's read-only, non-expiring authorize page for Kupro's key, and the
  wizard links to it under the token field. It also returns the key, so `kupro import trello --local` uses Kupro's
  key when `--key` is left out and the same token works. Kupro's API key is a manual step; without it the endpoint
  answers 503. Finish deletes Kupro's copy of the token; the wizard tells the person to revoke it at Trello
  ([ADR-019](../docs/adr/ADR-019-import.md#source-access) says why Kupro doesn't).
- Done: the site page, `/switch/trello` and `/da/skift/trello`.

A comment edited after the cursor keeps its action date, so only a full run picks up the edit. Attachments not on
Trello are fetched without credentials: in the API that needs the same private-address guard (SSRF) as task 025's base
URL. Recorded fixtures need a Trello sandbox board (a manual step); until then the fake stands in for them.

**Linear** with 023's pipeline in place:

- Done through the shared defaults: state types to states (with the "review" hint), priority names, Linear projects
  to Epics, issues with sub-issues promoted to Features by Hierarchy fit, and the points factor in the Effort step.
  Team keys are kept (`ENG-123`).
- Open: a home team for a project that spans teams (today it goes under the first team in scope), and initiatives to
  labels (today a field).
- Reply threads need a place in the snapshot (an optional `parent` on comments). Kupro's comments are flat, so today a
  reply is a comment in time order and loses what it answers.
- Mentions and issue URLs (`linear.app/<org>/issue/ENG-123`) need its `MarkupConverter`. Pasted files are attachments
  already, so inline images can point at the copies.
- Check: the new key after a move to another team as an alias.
- Project history, milestone changes in issue history and project updates are not read yet.
- Done: the site page, `/switch/linear` and `/da/skift/linear`, with the "where to get it" step. (`kupro import
  linear --local` is in, built with 024's.)

Recorded fixtures need a Linear sandbox workspace (a manual step); until then the fake stands in for them. Files
outside Linear are fetched without credentials, so the API needs the same private-address guard as Trello's.

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
