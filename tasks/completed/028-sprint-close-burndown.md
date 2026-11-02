---
title: 'Task 028: Sprint close review, split carry-over and burndown'
description: >-
  A purpose-made view for closing the active sprint: per-issue outcome (carry, split, backlog, cancel, done), split
  into a Done original and a continuation issue, team and per-assignee burndown, and the same page as a read-only
  sprint report after close. Backed by the sprint ledger, on REST, CLI and MCP.
status: 'Todo'
phase: 1
depends_on: [010, 012]
purpose: 'Implementation spec for task 028.'
---

# Task 028: Sprint close review, split carry-over and burndown

## TL;DR

Decisions are in [ADR-020](../docs/adr/ADR-020-sprint-close-burndown.md). This task replaces the inline close prompt
from [task 010](completed/010-sprints.md) with a **Close sprint** page: summary, burndown, per-assignee burndown and an
"Unfinished work" table where each issue gets an outcome. After close the same page is the read-only **sprint report**.
Agents get the same review, close plan and burndown through CLI and MCP.

## Scope

### Data (`Features/Sprints`)

- `SprintLedgerEntry` and `sprint_ledger` table per ADR-020 IMP-001; enum `LedgerKind`.
- `SprintLedger` helper (ADR-020 IMP-002), called by `IssueService` wherever an issue's sprint, effort, state or
  assignee changes (single update, bulk update, create into a sprint, `MoveUnfinishedAsync`, and the meeting agent's
  apply path, which goes through `IssueService` already). Rows only for issues whose sprint (old or new) is active.
- `SprintService.StartAsync` writes `baseline` rows for every issue in the sprint.
- Migration: `sprint_ledger` + baseline rows for currently active sprints (IMP-003).
- Export: add `sprint_ledger` to `ExportWriter` ([task 016](completed/016-workspace-export.md)).

### Close

- `CloseSprintRequest` gains `target` (sprint id; default next planned), `items` (`issue` key, `outcome`:
  `carry|split|backlog|cancel|done`, `hoursLeft` for split, optional per-item `target`) and `startNext` (start the
  target sprint right after close). `moveUnfinishedTo` stays as the default outcome for unlisted issues.
- Validation: split rules per [ADR-020](../docs/adr/ADR-020-sprint-close-burndown.md) (`invalid_split`); target must be
  an open sprint of the same project other than the closing one (`invalid_sprint`); `no_next_sprint` as today when
  carry or split has no target.
- Split per [ADR-020](../docs/adr/ADR-020-sprint-close-burndown.md); the continuation is created through
  `IssueService.CreateAsync`'s path, in the target sprint.
- One transaction: outcomes applied through `IssueService` (activity entry `sprint_closed` per issue with outcome and
  from/to sprint), ledger close rows, sprint closed, optional start of the target.
- Issues that are no longer unfinished in the sprint are skipped, not failed; the response lists them.
- `CloseSprintResponse` gains per-outcome counts and hours, `splits` (original key → continuation key), `skipped`,
  and `started`.
- Inline "Create next sprint" (same length, starting the day after this one ends) uses the existing create endpoint.

### Report and burndown (reads)

- `GET /sprints/{id}/review`: sprint, summary (at start, added, removed, completed, carried, split, to backlog,
  canceled, completion %), unfinished issues (key, title, state, assignee, effort, has children), completed issues
  (splits marked with their continuation key), open target sprints. Works for active and closed sprints.
- `GET /sprints/{id}/burndown?by=assignee`: per day (Europe/Copenhagen) `scope` and `remaining`, the ideal line, and,
  with `by=assignee`, the same series per assignee (member id, name; null = unassigned).
- Both are single grouped SQL queries over `sprint_ledger` + one issue projection; no per-issue queries.
- Issue detail: "Split from KUP-12" / "Continued in KUP-31" line from the `split_from` / `split_into` activity.

### Surfaces (ADR-001)

| REST | CLI | MCP |
|---|---|---|
| `GET /sprints/{id}/review` | `kupro sprint review <sprint>` | `get_sprint_review` |
| `GET /sprints/{id}/burndown` | `kupro sprint burndown <sprint> [--by assignee]` (table + block sparkline; `--json`) | `get_sprint_burndown` |
| `POST /sprints/{id}/close` (extended) | `kupro sprint close <sprint> [--to next\|backlog\|<sprint>] [--split KUP-12=4] [--backlog KEY…] [--cancel KEY…] [--done KEY…] [--start-next]` | `manage_sprint` close with `items`, `startNext` |

`<sprint>` accepts `active` with `--project`. MCP tool descriptions say how to build a close plan, so an agent can
answer "close sprint 14, split what's in review at half its effort".

### Web

- Route `/sprints/$sprintId` (report; close controls only while active). "Close sprint" on the active sprint board
  navigates here and the inline prompt goes. Banner on the sprint board once the end date has passed: "Sprint 14
  ended 2 days ago. Review and close." Closed sprints in the Sprints tabs link here.
- Layout, top to bottom: header (name, dates, goal) · stat row (At start, Added, Completed, Carried, Completion %) ·
  burndown (remaining step line, dashed scope line, ideal line, weekends shaded, end-date marker) · per-assignee small
  multiples with a shared y scale and a table (at start, added, completed, remaining, carried) · "Unfinished work"
  table · collapsed "Completed (12)" list.
- Unfinished table: row checkbox, key, title, state, assignee, effort, outcome segmented control (Carry · Split ·
  Backlog · Cancel · Done). Split shows an empty, focused hours-left input; once filled the row reads "KUP-12 done
  here (4 h) · new issue with 4 h to Sprint 15". Split is disabled, with a tooltip, for issues without effort or with
  children. Bulk outcome for selected rows (bulk split is not offered). Target sprint picker (default next planned) with "Create next
  sprint" when there is none.
- Sticky footer: "6 carry (31 h) · 2 split (8 h done here, 2 new issues) · 1 to backlog" + "Start Sprint 15 after
  closing" checkbox + Close sprint; disabled while a split row has no valid hours left. Toast after close as today,
  listing new continuation keys and skipped issues.
- Keyboard: `j`/`k` rows, `x` select, `c` carry, `s` split (focuses hours), `b` backlog, `Del` cancel, `d` done.
- Live: the page follows realtime issue and sprint changes ([task 012](completed/012-realtime.md)); a row whose issue
  became Done moves to Completed and drops its chosen outcome.
- Chart: hand-rolled SVG like the delivery plan, no chart dependency. Colours from tokens; the rules go in a new
  "Burndown" section of [design-guide.md](../docs/design-guide.md). Screen-reader table behind each chart.
- Strings EN + DA ([ADR-011](../docs/adr/ADR-011-i18n.md)); terms from the [glossary](../docs/branding.md#glossary).

## Out of scope

- Velocity across sprints, forecasting, per-person capacity or per-person history across sprints.
- Retro notes, goal met yes/no, story points.
- Working-day calendars and holidays (ideal line uses weekdays).
- Meeting agent proposing close outcomes in a review meeting (follow-up to [task 015](015-meeting-agent.md)).
- Burndown for sprints closed before this ships.

## Acceptance criteria

- [ ] Full close with mixed outcomes (carry, split, backlog, cancel, done) via UI, CLI and MCP, one transaction.
- [ ] Split: original is Done in the closing sprint with effort = hours done; continuation has hours left, copied
      fields, the original's state, a `relates` link and the open `blocks` relations, in the target sprint; the
      parent's roll-up total is unchanged; both issues show the split link.
- [ ] Old `close` call with only `moveUnfinishedTo` behaves as in task 010.
- [ ] Burndown matches a hand-computed fixture: mid-sprint add, effort change, reassignment, reopen, cancel, split.
- [ ] Per-assignee series sum to the team series; unassigned shown.
- [ ] Test fails if an `IssueService` write path changes sprint, effort, state or assignee of an issue in an active
      sprint without ledger rows (ADR-020 IMP-002).
- [ ] Review and burndown on a 300-issue sprint with 5 000 ledger rows inside the ADR-010 budget (perf test, task 014).
- [ ] Parity test passes; `sprint_ledger` in the export.
- [ ] EN + DA strings; i18n check passes.

Resolved questions (split input starts empty; per-assignee burndown visible to all) are in
[ADR-020](../docs/adr/ADR-020-sprint-close-burndown.md).
