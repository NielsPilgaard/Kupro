---
title: 'Task 034: Time registration'
description: >-
  Let members log time, either per issue or for the day, so a Danish employer can meet the working-time registration
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
- **One model**: a time entry is a member, a date, a duration, and optionally an issue and a note. A day's working
  time is the sum of that day's entries, so logging time on issues also covers the legal duty. No second register.
- **Surfaces**: log time on an issue, a week timesheet for each person, and an owner's export per person and period.
  REST, CLI and MCP in parity ([ADR-001](../docs/adr/ADR-001-agent-parity.md)).
- **Order**: ADR first (scope, retention, who sees what), then prototypes, then the API, then the UI.
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
- The duty is the **length of the working day**. Recording what the time went on (per issue) is not required; that is
  the part teams want for themselves (estimates, billing, worklogs from Jira).
- Recording only deviations from an agreed schedule may be enough. If the check confirms this, it is the cheapest way
  for a member to comply ("my normal week, except Tuesday").

## Decisions for the ADR

Write `ADR-021: Time registration` before any code. Proposed answers; the ADR decides.

1. **Scope**: move "Time tracking" from Out to In in [product.md](../docs/product.md)'s v1 table, linking the ADR.
   Update ADR-020 ALT-003's reason, and task 024's out-of-scope line on worklogs.
2. **Model**: `time_entry` (workspace, member, date, minutes, issue nullable, note, created/updated, `via`). Entries
   only for human members; agents and guests have none. An agent using a person's token logs as that person, like
   every other action.
3. **Schedule**: an optional weekly schedule per member (hours per weekday). A day with no entries counts as the
   schedule, so a member only logs deviations. Only if the legal check allows it.
4. **Who sees what**: a member sees and edits their own entries. Owners see everyone's and export them (the employer
   needs them). Members don't see each other's day totals; time logged on an issue shows as a total on the issue to
   everyone who can see the issue, with names only to Owners. To decide.
5. **Reliability**: every change to an entry is kept in its history (who, when, old and new). Optionally, days older
   than N days lock unless an Owner reopens them.
6. **Retention**: entries are kept for 5 years, also after a member leaves. This needs a line in the privacy policy and
   the DPA (the workspace is the controller), and an exception in workspace deletion: an Owner exports before deletion,
   and the export page says so. Self-hosters keep their own.
7. **Workspace setting**: time registration is off by default and an Owner turns it on, so teams that don't want it
   never see it ("Simple", product principle 3).
8. **Price**: included in the seat price. To confirm with the owner ([ADR-013](../docs/adr/ADR-013-seats-billing.md)).
9. **MCP**: the tool budget is full ([ADR-001](../docs/adr/ADR-001-agent-parity.md) IMP-003). Add the actions to an existing tool
   rather than a new one.

## Scope

### API

- Create, update and delete own entries; list own entries for a date range; an issue's total.
- Owners: list and export (CSV and JSON) for one or all members and a date range: date, member, minutes, issue key,
  note, and whether the day came from the schedule.
- Validation: minutes 1–1440 per entry, a day's total at most 24 h, date not in the future beyond today.
- Index `(workspace_id, member_id, date)`; a week's timesheet is one query ([ADR-010](../docs/adr/ADR-010-performance-budget.md)).
- Activity: logging time on an issue is not an activity feed item (it would flood the feed); the issue shows the total.

### CLI and MCP

- `kupro time log <duration> [KEY] [--date] [--note]` (`1h30m`, `90m`, `1.5h`), `kupro time week [--date]`,
  `kupro time edit|delete <id>`, `kupro time export --from --to [--member] [--format csv|json]` (Owners).
- MCP: log, list a week and an issue's total, as actions on an existing tool. Export stays off MCP (personal data in
  bulk), recorded as an opt-out in the parity test.

### Web

Prototypes first ([AGENTS.md](../AGENTS.md) rule 11), in `prototypes/time/`:

- **Log time on an issue**: a field in the issue detail and a command palette action ("Log time", shortcut).
- **Week timesheet**: rows per issue plus a "Not on an issue" row, columns per weekday, day totals against the
  schedule, keyboard entry.
- **Owner's export**: pick people and a period, download CSV.
- **Settings**: turn it on for the workspace; a member's own weekly schedule.

EN and DA strings; the DA term is "tidsregistrering" ([branding.md](../docs/branding.md) glossary gets the entry).

### Import

- Jira worklogs (024) and YouTrack work items (027) become time entries for the mapped person, on the imported
  issue, when the workspace has time registration on. Otherwise they stay summed in the details block, as today.
  Azure DevOps has only a completed-work total per item, which stays in the details block.

### Site

- When it ships: a line on the landing page and in the pricing FAQ, and the switch pages' "No time tracking" line
  changes ([024](024-import-jira.md) and the other `/switch/<source>` pages).

## Acceptance criteria

- [ ] ADR-021 accepted; product.md, ADR-020 and task 024 updated to match.
- [ ] Legal reading confirmed (manual step), and the ADR notes what the confirmation changed.
- [ ] A member logs time on an issue in the web app, CLI and MCP, and sees it in their week.
- [ ] A day with no entries counts as the schedule when the member has one (if the ADR keeps schedules).
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
