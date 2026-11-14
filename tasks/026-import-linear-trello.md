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

**Next** for Trello, each waits on a slice of [task 023](023-import-framework.md):

- Default list mapping by name, archived cards to Canceled or skipped, the epic source and checklist items to Tasks
  need 023's mapping model. Promoted items are also still in the description's task list; the pipeline drops those
  lines or keeps them.
- Label colours need a place in the snapshot (an optional `labels` list in the header); today a colour-only label
  comes as its colour name and a named label's colour is not read across.
- `@username` mentions and card links (`trello.com/c/…`) need its `MarkupConverter`.
- Keys (`idShort` under the owner's key) and card URL aliases need its pipeline.
- The authorize link and token storage need the wizard; Kupro's API key is a manual step.
- The site page needs the wizard. (`kupro import trello --local --key <api-key>` is in, built with 024's.)

A comment edited after the cursor keeps its action date, so only a full run picks up the edit. Attachments not on
Trello are fetched without credentials: in the API that needs the same private-address guard (SSRF) as task 025's base
URL. Recorded fixtures need a Trello sandbox board (a manual step); until then the fake stands in for them.

**Next** for Linear, each also waits on a slice of [task 023](023-import-framework.md):

- Default mappings need 023's mapping model: state types to states (with the "review" hint), priority names, Linear
  projects to Epics (and a home team for one that spans teams; today it goes under the first team in scope), issues
  with sub-issues to Features through Hierarchy fit, initiatives to labels, and the points factor.
- Reply threads need a place in the snapshot (an optional `parent` on comments). Kupro's comments are flat, so today a
  reply is a comment in time order and loses what it answers.
- Mentions and issue URLs (`linear.app/<org>/issue/ENG-123`) need its `MarkupConverter`. Pasted files are attachments
  already, so inline images can point at the copies.
- Keys (`ENG-123`, and the new key after a move to another team) and aliases need its pipeline.
- Project history, milestone changes in issue history and project updates are not read yet.
- Key storage and the "where to get it" step need the wizard, and so does the site page. (`kupro import linear
  --local` is in, built with 024's.)

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
