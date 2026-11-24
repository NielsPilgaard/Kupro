---
title: 'Task 034: Time registration'
description: >-
  Let members log the time they spend on issues, so a Danish employer can meet the working-time registration
  duty (in force since 1 July 2024) inside Kupro, and teams leaving Jira or Azure DevOps keep their worklogs. One time
  entry model, a week timesheet, an owner's export, and full parity. It starts with an ADR, because it moves time
  tracking into v1 scope.
status: 'Todo'
phase: 2
depends_on: ['003', '004']
purpose: 'Implementation spec for task 034.'
---

# Task 034: Time registration

## TL;DR

- **Why**: Danish employers must record each employee's daily working time (see [Legal background](#legal-background)).
  Teams that do it in Jira today will not move without it, and it is a good feature in its own right.
- **One model**: a time entry is a member, a date, a duration, an issue and an optional note. A day's working time is
  the sum of that day's entries, so the records show what the time went on. No second register.
- **Surfaces**: log time on an issue, a week timesheet for each person, and an owner's export per person and period.
  REST, CLI and MCP in parity ([ADR-001](../docs/adr/ADR-001-agent-parity.md)).
- **Order**: ADR-021 (accepted), then prototypes, then the API, then the UI.
- **Legal check**: the rules below are our reading of them. A lawyer or the employers' guidance confirms them before the
  site or the app claims compliance (a manual step).

## Legal background

As we read it, to be checked:

- Since 1 July 2024 the Working Time Act (lov om tilrettelæggelse af arbejdstiden) requires employers to have a
  system that records the length of each employee's daily working time. It follows the EU Court of Justice's
  judgment C-55/18 (CCOO).
- It covers all employees, not only funktionærer. Employees who largely decide their own working hours
  ("selvtilrettelæggende") can be exempt, and collective agreements can set their own rules.
- The system must be objective, reliable and accessible, the employee must be able to see their own records, and the
  records are kept for 5 years.
- Kupro records the time per work item, so the records show what the working day went on as well as its length.

## Decisions

Decided in [ADR-021](../docs/adr/ADR-021-time-registration.md) (Accepted 2026-11-23): every entry is on a work item,
no schedules, a member sees only their own time, entries lock while the issue is Done or Canceled, included in the
seat, and an MCP `time` tool in place of a merged `relations` tool.

## Scope

### API

- Create, update and delete own entries; list own entries for a date range; an issue's time (own, or the total and
  each person's share for Owners).
- Owners: list and export (CSV and JSON) for one or all members and a date range: date, member, minutes, issue key,
  note.
- Validation: an issue is required, minutes 1–1440 per entry (imports exempt, ADR-021), a day's total at most 24 h, date not later than today,
  and no add, edit or delete while the issue is Done or Canceled.
- Close and log in one save: logging time takes an optional target state (Done or Canceled). The entry and the state
  change commit in one transaction, and the lock applies after it (ADR-021 Locking).
- Index `(workspace_id, member_id, date)`; a week's timesheet is one query ([ADR-010](../docs/adr/ADR-010-performance-budget.md)).
- Activity: logging time on an issue is not an activity feed item (it would flood the feed).

### CLI and MCP

- `kupro time log <duration> <KEY> [--date] [--note] [--done|--canceled]` (`1h30m`, `90m`, `1.5h`), `kupro time week [--date]`,
  `kupro time edit|delete <id>`; Owners: `kupro time list --from --to [--member]` and
  `kupro time export --from --to [--member] [--format csv|json]`.
- MCP: a `time` tool (`log | edit | delete | week`; `log` takes an optional `state`) and `relate_issues`/`unrelate_issues` merged into `relations`
  (ADR-021); `get_issue` gains the caller's time. Export stays off MCP, an opt-out in the parity test.

### Web

Prototypes first ([AGENTS.md](../AGENTS.md) rule 11), in [prototypes/time/](../prototypes/time/index.html): 01 week grid,
02 day journal, 03 log where you work. **Picked (2026-11-23): 01 week grid**, revised on the owner's ask ("too small,
not enough structure"): rows grouped by project with subtotals, locked issues in their own group, summary tiles
(week, today, weekdays with no time, locked), today's column highlighted, a panel for the selected cell's notes, and
48 px rows. The issue panel, Log time dialog, board, Team time and Settings are shared by all three. Each covers:

- **Log time on an issue**: a field in the issue detail and a command palette action ("Log time", shortcut).
  Setting the state to Done or Canceled, in the issue or by dragging on the board, asks "Log time and mark Done" in
  one dialog, saved together, when the person has logged no time on the issue.
- **Week timesheet**: rows per issue (add an issue to the week), columns for all seven days, Monday to Sunday, day totals, locked cells for
  resolved issues, keyboard entry.
- **Owner's export**: pick people and a period, download CSV.
- **Settings**: turn it on for the workspace.

EN and DA strings; the DA term is "tidsregistrering" (add it to the [branding.md](../docs/branding.md) glossary).

### Import

- Jira worklogs (024) and YouTrack work items (027) become time entries for the mapped person, on the imported
  issue, when the workspace has time registration on. Otherwise they stay summed in the details block, as today.
  Azure DevOps has only a completed-work total per item, which stays in the details block.

### Site

- When it ships: a line on the landing page and in the pricing FAQ, and the switch pages' "No time tracking" line
  changes ([024](024-import-jira.md) and the other `/switch/<source>` pages).

## Acceptance criteria

- [x] ADR-021 accepted; product.md, ADR-020 and task 024 updated to match.
- [ ] Legal reading confirmed (manual step), and the ADR notes what the confirmation changed.
- [ ] A member logs time on an issue in the web app, CLI and MCP, and sees it in their week.
- [ ] Entries on a Done or Canceled issue can't be added, edited or deleted; reopening the issue unlocks them.
- [ ] One save marks an issue Done and logs time on it, in the web app, CLI and MCP; if either fails, neither is saved.
- [ ] An edit keeps the old value in the entry's history.
- [ ] An Owner exports a month for all members as CSV; a Member can't.
- [ ] Entries of a member who left are kept and still exported.
- [ ] Jira worklogs import as entries when the setting is on.
- [ ] A week's timesheet loads within the latency budget with a year of entries for 50 members.

## Out of scope

- Payroll, overtime pay, holiday and absence management (ferie, sygdom): the export feeds the payroll system.
- Timers ("start/stop").
- Billing customers for time, rates, invoices.
- Approval flows for timesheets.
