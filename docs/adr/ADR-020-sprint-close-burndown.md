---
title: 'ADR-020: Sprint Close, Split Carry-Over and Burndown'
status: 'Accepted'
date: '2026-10-31'
authors: 'Niels'
tags: ['domain', 'sprints', 'reports']
supersedes: ''
superseded_by: ''
description: >-
  Closing a sprint becomes a review with a per-issue outcome (carry, split, backlog, cancel, done). A split finishes
  the original issue in the closing sprint with the hours done and creates a continuation issue with the hours left.
  An append-only sprint ledger, written with every change, drives the team and per-assignee burndown and the report.
---

# ADR-020: Sprint Close, Split Carry-Over and Burndown

## TL;DR

- Close = a review: each unfinished issue gets an outcome — **carry**, **split**, **backlog**, **cancel** or **done**.
- **Split** = two issues: the original becomes Done in the closing sprint with effort = hours done; a new
  continuation issue gets the hours left and moves on. Delivered work counts where it happened; the total is kept.
- An append-only **sprint ledger** (scope and remaining deltas per issue and assignee) is written in the same
  transaction as every change to an issue in an active sprint. Burndown, per-assignee burndown and the report read it.
- Days are Europe/Copenhagen days. Per-assignee views are visible to every member and show no ranking.

## Status

**Accepted** — 2026-10-31. OQ-1 and OQ-2 resolved.

## Context

Task 010 closes a sprint with one choice for all unfinished issues: next sprint or backlog. Teams need to decide per
issue, and an 8 h issue that is half done should count 4 h delivered in this sprint, visibly, as a done issue on this
sprint's board. [ADR-008](ADR-008-work-item-model.md) promised a simple burndown later. Burndown needs history: on
close, issues leave the sprint, so live issue rows can't rebuild a closed sprint, and the activity log stores
display-oriented JSON per issue that would need a scan of every issue that ever touched the sprint.

## Decision

### Outcomes on close

| Outcome | Effect on issues | Effect on the closing sprint |
|---|---|---|
| Carry (default) | Moves to the target sprint (next planned by default) | Remaining hours leave as carried |
| Split | Original: Done, effort := hours done, stays. Continuation: new issue, effort := hours left, in the target sprint | Hours done completed; hours left leave as carried |
| Backlog | Leaves the sprint; state kept | Remaining hours leave as moved to backlog |
| Cancel | State := Canceled; stays in the closing sprint | Leaves scope |
| Done | State := Done; stays in the closing sprint | Completed |

The close is one transaction. Issues that stopped being unfinished while the review was open are skipped and reported.
Unlisted issues get the request's default outcome, so today's `moveUnfinishedTo` call keeps working.

### Split

- Allowed on issues with an effort and no child issues; the user types the **hours left** (no default),
  `0 < hours left < effort`.
- The continuation copies type, project, parent, title, description, assignee, priority, labels, start and due date,
  and the original's state (work in progress stays in progress). Not copied: comments, attachments, activity.
- The two issues are linked with a `relates` relation; activity records `split_into` on the original and `split_from`
  on the continuation, so both show "Split from KUP-12" / "Continued in KUP-31".
- Open `blocks` relations (both directions) move to the continuation: the Done original no longer blocks anything.
- Hours done + hours left = the original effort, so epic and feature roll-ups and the delivery plan keep the total.

### Sprint ledger

One row per change to an issue while its sprint is active: sprint, issue, assignee (nullable), time, kind, scope
delta and remaining delta (hours; unestimated issues count 0). Kinds: `baseline` (on start), `added`, `removed`,
`estimated`, `completed`, `reopened`, `reassigned` (a minus row for the old assignee, a plus row for the new),
`canceled`, and the close kinds `carried`, `split`, `backlogged`. Written by the feature services next to the activity
entry, never updated or deleted. Canceled issues leave scope; In Review is still remaining.

- Burndown: running sums bucketed by Europe/Copenhagen day, from sprint start to the later of end date and close.
  Close kinds are left out of the lines (they would drop remaining to zero) and feed the outcome summary instead;
  a split's done hours are a normal `completed` row on close day.
- Ideal line: baseline scope to 0 over the sprint's weekdays. No holidays.
- Per assignee: the same sums grouped by assignee, plus "Unassigned". Assignees are always people ([ADR-008](ADR-008-work-item-model.md)).

### Who sees what

Per-assignee burndown is visible to every member: it aggregates data they already see on the board. No cross-sprint
per-person velocity, no ranking, no per-person capacity ([ADR-008](ADR-008-work-item-model.md)).

## Resolved questions

- **OQ-1**: Split hours-left input → **empty**; the user types it.
- **OQ-2**: Per-assignee burndown → **visible to every member**, no setting.

## Consequences

### Positive

- **POS-001**: Delivered work is a Done issue on the sprint where it happened; velocity needs no special case.
- **POS-002**: Estimates add up: hours done + hours left = original effort, so roll-ups and the plan keep the total.
- **POS-003**: Exact, intraday burndown with no scheduler, no gaps when a job is down, and a cheap indexed read.

### Negative

- **NEG-001**: The continuation has a new key. Branches, PRs and notes naming the old key point at the Done part;
  the "Continued in" link bridges it.
- **NEG-002**: Issue counts grow by one per split.
- **NEG-003**: Every issue write path must call the ledger; a missed path silently skews burndown. Guarded by a test.
- **NEG-004**: Sprints closed before the ledger shipped have no burndown; active sprints get a baseline at migration
  time, so their chart starts then.

## Alternatives Considered

### Keep one issue and credit the done hours on the ledger

- **ALT-001**: **Description**: Effort drops to the hours left, the issue moves on, the closing sprint is credited.
- **ALT-002**: **Rejection Reason**: Delivered work isn't a visible Done issue in the sprint, and the original
  estimate disappears from the issue and from roll-ups.

### Original, remaining and completed effort fields

- **ALT-003**: **Rejection Reason**: That is time tracking, out of v1 scope ([product.md](../product.md)).

### Rebuild burndown from the activity log

- **ALT-004**: **Rejection Reason**: Activity data is display-oriented JSON per issue; finding every issue that ever
  touched a sprint needs a JSON scan over the whole log, outside the budget in [ADR-010](ADR-010-performance-budget.md).

### Nightly snapshot job

- **ALT-005**: **Rejection Reason**: Day granularity only, gaps when the job fails, and nothing for today until night.

## Implementation Notes

- **IMP-001**: Table `sprint_ledger`, tenant-owned, index `(workspace_id, sprint_id, at)`. Included in the workspace
  export ([task 016](../../tasks/completed/016-workspace-export.md)).
- **IMP-002**: A `SprintLedger` helper like `ActivityLog`: called with the issue before and after, it adds rows to the
  caller's DbContext. A test fails if an `IssueService` write path changes sprint, effort, state or assignee without it.
- **IMP-003**: Migration writes `baseline` rows for issues in currently active sprints.
- **IMP-004**: The continuation is created through `IssueService`'s create path (key, rank, realtime, search vector).
- **IMP-005**: Import ([ADR-019](ADR-019-import.md)) writes a baseline for an imported active sprint and no ledger
  for closed ones.

## Related Decisions

- [ADR-008](ADR-008-work-item-model.md) — sprints, effort in hours, relations, no per-person capacity
- [ADR-001](ADR-001-agent-parity.md) — review, close and burndown on REST, CLI and MCP
- [ADR-010](ADR-010-performance-budget.md) — burndown and review reads stay inside the budget
- [ADR-019](ADR-019-import.md) — imported sprints and the ledger

## References

- **REF-001**: [Task 028](../../tasks/028-sprint-close-burndown.md) — implementation spec
- **REF-002**: [Task 010](../../tasks/completed/010-sprints.md) — the close it replaces
