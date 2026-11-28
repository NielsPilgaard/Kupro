---
title: 'Task 034: Time registration'
description: >-
  Let members log the time they spend on issues, so a Danish employer can meet the working-time registration
  duty (in force since 1 July 2024) inside Kupro, and teams leaving Jira or Azure DevOps keep their worklogs. One time
  entry model, a week timesheet, an owner's export, and full parity. It starts with an ADR, because it moves time
  tracking into v1 scope.
status: 'In progress'
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

## Progress

API, CLI and MCP are in (`Kupro.Api/Features/Time`, `kupro time`, the MCP `time` and `relations` tools), tested in
`TimeTests`, `McpTests`, `CliTests` and `PerfBudgetTests`. The web UI (prototype 01) is in `web/src/features/time`,
tested in `time.test.ts` and the E2E `time.spec.ts`. The import commit turns snapshot worklogs into entries. Left:
the connectors reading Jira worklogs and YouTrack work items, the summed field and the plan's warning (see Import
below), the site lines, and the manual steps (legal check, privacy policy and DPA lines).

- **Web**: My time (`/w/:workspace/time`, the week grid with `?week=`), Team time (`/team-time`, Owners, with the
  export dialog), and workspace settings (`/settings`, Owners, the time registration switch). The Log time dialog opens
  from `T`, the palette and the issue panel's time box; setting Done or Canceled from the panel, the palette or the
  board asks for time in the same save when you have logged none (`useCloseWithTime`). A cell with several entries is
  edited in the panel under the grid, so no note is lost.

- **Model**: `time_entries` and the append-only `time_entry_changes` (who, when, via, old and new date, minutes and
  note), per [ADR-021 Time entry](../docs/adr/ADR-021-time-registration.md#time-entry). The workspace's
  `timeRegistration` flag (`PATCH` the workspace, `kupro workspace update --time-registration true`) follows
  [ADR-021 Workspace setting](../docs/adr/ADR-021-time-registration.md#workspace-setting); off, writes answer
  `time_registration_off`.
- **REST**: `POST time` (`logTime`, optional `state` done or canceled closes the issue in the same transaction),
  `PATCH|DELETE time/{id}`, `GET time/mine` (a week by default, up to 93 days), `GET issues/{key}/time`, `GET
  time/changes` (own, or any member's for Owners). Owners only: `GET time/entries` (93 days) and `GET time/export`
  (366 days, CSV with a BOM or JSON, streamed; formula characters escaped). Errors: `time_locked`, `day_full`,
  `future_date`, `not_your_entry`, `time_entry_not_found`.
- **Locking**: checked from the issue's saved state inside the write's transaction, holding a row lock on the issue
  (`FOR SHARE`, or `FOR NO KEY UPDATE` when closing), so the issue can't close between the check and the save. The
  day's cap takes a per-person-and-day advisory lock.
- **Agents**: attribution per [ADR-021 Who sees what](../docs/adr/ADR-021-time-registration.md#who-sees-what);
  `TimeService.PersonAsync` resolves an agent token to its person.
- **CLI**: `kupro time log|week|edit|delete|issue|history|list|export`; durations `1h30m`, `90m`, `1.5h`, `1:30`.
- **MCP**: `time` (`log | edit | delete | week`), `relations` (`relate | unrelate`) replaces `relate_issues` and
  `unrelate_issues`, and `get_issue` returns `time` when the setting is on. To keep `tools/list` under 19 KB the
  `workspace` parameter lost its per-tool description (ADR-001 IMP-003). `time/changes`, `time/entries` and
  `time/export` are MCP opt-outs.
- **Retention**: `TimeRetentionJob` applies [ADR-021 Retention and privacy](../docs/adr/ADR-021-time-registration.md#retention-and-privacy),
  10 minutes after startup and then every 6 hours.
- **Import** (commit side): snapshots carry worklogs (`SourceWorklog`, the `worklogs` part). With the setting on, the
  commit writes one entry per worklog for a mapped person or their own placeholder (`TimeService.AddImported`,
  `ImportedKinds.Worklog`, counted as `timeEntries`), and a re-run writes none twice. Worklogs by a skipped person or
  by nobody are not written. Undo deletes the imported entries and their history, and waits (blocker `time`) while
  the issues hold time logged in Kupro or an imported entry changed since. Tested in `ImportCommitTests.Worklogs.cs`.
  Not yet: the Jira and YouTrack connectors don't read worklogs; the summed field (`SourceField.SumsWorklogs`) still
  shows the whole sum rather than only the time nobody in Kupro holds; the plan doesn't count worklogs or warn about
  days over 24 hours; the run report and the runs table don't show `timeEntries`.
- **Speed**: with a year of workday entries for 50 people, the week, an issue's time and the Owners' week list answer in
  under 8 ms p50, and logging in under 10 ms p50 (`PerfBudgetTests`).

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
- Validation as in ADR-021 Model and Locking (imports per ADR-021 Import): an issue is required, minutes 1–1440 per
  entry, a day's total at most 24 h, date not later than today, and no add, edit or delete while the issue is Done
  or Canceled.
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
- [x] A member logs time on an issue in the web app, CLI and MCP, and sees it in their week.
- [x] Entries on a Done or Canceled issue can't be added, edited or deleted; reopening the issue unlocks them.
- [x] One save marks an issue Done and logs time on it, in the web app, CLI and MCP; if either fails, neither is saved.
- [x] An edit keeps the old value in the entry's history.
- [x] An Owner exports a month for all members as CSV; a Member can't.
- [x] Entries of a member who left are kept and still exported.
- [ ] Jira worklogs import as entries when the setting is on.
- [x] A week's timesheet loads within the latency budget with a year of entries for 50 members.

## Out of scope

- Payroll, overtime pay, holiday and absence management (ferie, sygdom): the export feeds the payroll system.
- Timers ("start/stop").
- Billing customers for time, rates, invoices.
- Approval flows for timesheets.
