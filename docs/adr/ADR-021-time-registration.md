---
title: 'ADR-021: Time Registration'
status: 'Accepted'
date: '2026-11-23'
authors: 'Niels'
tags: ['domain', 'time', 'privacy', 'legal']
supersedes: ''
superseded_by: ''
description: >-
  Time registration moves into v1. Members log the time they spend on work items: a time entry is a member, a date,
  minutes, an issue and an optional note, and a day's working time is the sum of its entries. That records what the
  working day went on, which is what Danish employers must show, and keeps Jira and YouTrack worklogs on import. It is
  off by default per workspace. A member sees their own time, Owners see and export everyone's. Entries lock when the
  issue is resolved, every change is kept, and entries are kept for 5 years.
---

# ADR-021: Time Registration

## TL;DR

- **Model**: a time entry = member, date, minutes, issue, optional note. Every entry is on a work item (an epic,
  feature or task, bugs included). A day's working time is the sum of its entries.
- **Access**: off by default per workspace. A member sees and edits only their own; Owners see and export everyone's.
- **Records**: entries on a Done or Canceled issue are locked; every change kept (who, when, old and new); kept 5
  years, also after the member leaves.
- **Parity**: REST and CLI do everything; MCP gets a `time` tool in place of a merged `relations` tool; export is not on
  MCP.

## Status

**Accepted** — 2026-11-23. OQ-1 to OQ-5 resolved by the owner (see Resolved questions). The legal reading in Context
is checked before the site or the app claims compliance (a manual step), and the ADR records what that check changes.

## Context

Danish employers must record their employees' working time: since 1 July 2024 the Working Time Act (lov om
tilrettelæggelse af arbejdstiden) requires a system that records each employee's daily working time. The law follows
the EU Court of Justice's judgment C-55/18 (CCOO). As we read it:

- It covers all employees. Employees who largely set their own hours ("selvtilrettelæggende") can be exempt, and
  collective agreements can set their own rules.
- The system must be objective, reliable and accessible, the employee must be able to see their own records, and the
  records are kept for 5 years.

The owner's requirement is that time is recorded **on the work items it was spent on**, so the records show what the
working time went on, not only how long the day was. The day's length follows as the sum.

Teams leaving Jira or Azure DevOps often log worklogs on issues today, and they will not move to a tool that sends
them to a second system. [product.md](../product.md) had time tracking out of v1, and
[ADR-020](ADR-020-sprint-close-burndown.md) ALT-003 rejected completed-effort fields because of it. Imports
([ADR-019](ADR-019-import.md)) sum Jira worklogs and YouTrack spent time into the details block, because there was
nowhere to put them.

## Decision

### Scope

Time registration is in v1. Payroll, overtime, holiday and absence management, timers, billing customers for time and
approval flows stay out (task 034).

### Time entry

| Column | Notes |
|---|---|
| `workspace_id`, `id` | Tenant-owned ([ADR-009](ADR-009-multi-tenancy.md)) |
| `member_id` | A human member: Owner, Member, or a placeholder from an import ([ADR-019](ADR-019-import.md)). Guests and agents have no entries. |
| `issue_id` | Required. Any issue type ([ADR-008](ADR-008-work-item-model.md)). |
| `date` | A calendar date, not a time. Not later than today in Europe/Copenhagen (as in ADR-020). |
| `minutes` | 1–1440. Entries made in Kupro keep a member's day at 1440 or less (imports: see Import). |
| `note` | Optional plain text. |
| `via` | The activity log's `Surface` (`web`, `cli`, `mcp`, `api`, `import`); the meeting agent doesn't log time. |
| `created_at`, `updated_at` | |

- Time that isn't on a feature or a bug (meetings, support, admin) goes on a work item for it, such as a "Meetings"
  task. There are no entries without an issue.
- A day's working time is the sum of that day's entries. There are no schedules: a member logs every day they work.
- An agent logs for the person it acts for ([ADR-001](ADR-001-agent-parity.md) attribution). The entry is that
  person's, and its history names the agent.
- Time entries don't change an issue's effort, the sprint ledger or burndown ([ADR-008](ADR-008-work-item-model.md),
  [ADR-020](ADR-020-sprint-close-burndown.md)). Effort stays the estimate, and logged time is what was spent.
- Logging time is not an activity feed item, because it would flood the feed.
- Index `(workspace_id, member_id, date)`, so a week's timesheet is one query
  ([ADR-010](ADR-010-performance-budget.md)), and `(workspace_id, issue_id)` for an issue's time.

### Who sees what

- A member sees and edits their own entries. Members don't see each other's entries or day totals.
- On an issue, a member sees their own time on it. Owners see the issue's total and each person's share. Guests see
  no time.
- Owners see everyone's entries and export them, but don't edit them: the employee's record is the employee's. A
  member corrects their own; a correction for someone who has left is a note in the export, not an edit.
- A deactivated member can't sign in, so their access to their own records goes through an Owner's export.

### Locking

- An entry is locked while its issue is Done or Canceled: it can't be edited or deleted, and no entry can be added to
  that issue. Reopening the issue unlocks its entries, and the issue's activity log shows who reopened it.
- The lock starts once the state change is saved, not before. One save can set an issue to Done or Canceled and log
  time on it: both commit in one transaction, or neither does. Closing a work item never needs a reopen to log the
  last hours (Azure DevOps blocks this).
- Imports are exempt: entries imported on a closed issue are created locked.

### Reliability

Every create, edit and delete of an entry writes a row to an append-only `time_entry_change` table: who, when, via,
and old and new values. Deleting an entry keeps its history, so an Owner can see what was removed and by whom. An
Owner sees this history per member, and a member sees the history of their own entries.

### Retention and privacy

- Entries and their history are kept for 5 years after the day they record, also after the member is deactivated,
  and are still exported. A nightly job deletes them after that, by the recorded day, not by when the row changed.
- The legal basis is the employer's legal obligation, so an erasure request doesn't delete entries inside the 5 years.
  The privacy policy and the DPA say so (a manual step).
- Deleting an issue is not possible in Kupro, so an entry never loses its issue. If issue deletion is added, an issue
  with time entries can't be deleted.
- Kupro has no workspace deletion yet (ADR-016 NEG-004). When it gets one, deleting a workspace with time entries
  first asks an Owner to export them, and the export page says why. Self-hosters keep their own records.

### Workspace setting

`time_registration` is off by default and an Owner turns it on ("Simple", product principle 3). Off, the issue panel,
the Log time action and the week view are hidden, and new entries are refused (`time_registration_off`). Turning it
off after use keeps the data: My time stays visible read-only for members who have entries, Owners can still export,
and the retention above still applies.

### Price

Included in the seat price ([ADR-013](ADR-013-seats-billing.md)). No add-on.

### Parity and MCP

- REST and CLI: log, edit and delete own entries; list own entries for a range; an issue's time; Owners' list and
  export (CSV and JSON) per member or all, for a date range. The commands are in task 034.
- MCP: the tool budget is full ([ADR-001](ADR-001-agent-parity.md) IMP-003). `relate_issues` and `unrelate_issues`
  merge into one `relations` tool (`action: relate | unrelate`), and a new `time` tool takes the freed place
  (`action: log | edit | delete | week`), because the week view and editing a past entry don't fit an issue tool
  (ALT-007). `get_issue` gains the caller's time on the issue (an Owner's call gets the total). The budget stays at
  20.
- Not on MCP, an opt-out in the parity test: the export and the Owners' list of everyone's entries (personal data in
  bulk).

### Import

When the setting is on, Jira worklogs and YouTrack work items become time entries for the mapped person, on the
imported issue, with `via = import`. When it is off, they stay summed in the details block, as today. Azure DevOps
has only a completed-work total per item, which stays in the details block.

- A person mapped to a member or their own placeholder gets entries. Skipped users' worklogs (the shared placeholder,
  [ADR-019](ADR-019-import.md)) stay summed in the details block: one member must not hold several people's time.
- Imported entries are kept as the source recorded them: the daily cap, the future-date check and the lock don't
  apply, and the plan warns about days over 24 hours. The same holds when a placeholder is merged into a member and
  its entries move.

## Resolved questions

- **OQ-1** (2026-11-23): Deviation-only recording (a weekly schedule that counts when a day has no entries)? **No.**
  Every working day is logged on work items. Schedules and day markers are dropped.
- **OQ-2** (2026-11-23): Who sees names on an issue's time? **Owners and the person themselves.** A member sees only
  their own time on an issue, not the total.
- **OQ-3** (2026-11-23): Lock old entries? **Yes, when the issue is resolved** (Done or Canceled); reopening unlocks.
- **OQ-4** (2026-11-23): Included in the seat price? **Yes.**
- **OQ-5** (2026-11-23): Merge `relate_issues` and `unrelate_issues` to free a place for a `time` tool? **Yes.**

## Consequences

### Positive

- **POS-001**: A Danish employer records working time in the tool the team already uses, so it's one reason less to
  stay in Jira.
- **POS-002**: One model: time on work items and the day's length are the same rows, so nobody logs twice.
- **POS-003**: Jira and YouTrack worklogs survive the move instead of being flattened into a number.
- **POS-004**: The lock on resolved issues keeps finished work's time stable without an Owner chore.
- **POS-005**: Off by default, so teams that don't want it never see it.

### Negative

- **NEG-001**: Personal data about employees: retention, an erasure exception and Owner-only views to get right, and
  lines in the privacy policy and the DPA.
- **NEG-002**: Scope grows in v1: a new table pair, a timesheet screen, a setting, an export, and an MCP tool merge.
- **NEG-003**: Overhead time needs a work item to log on, such as a "Meetings" task, and members log every day.
- **NEG-004**: Merging `relate_issues` and `unrelate_issues` changes the MCP surface for existing agent setups.
- **NEG-005**: Compliance rests on our reading of the law until the legal check is done.

## Alternatives Considered

### A separate working-time register

- **ALT-001**: **Description**: Record the day's start, end and breaks on their own, apart from time logged on issues.
- **ALT-002**: **Rejection Reason**: Two places to log the same day, and it doesn't show what the time went on.

### Leave it to a time tool

- **ALT-003**: **Description**: Keep time out of v1 and point employers at a Danish time-registration product.
- **ALT-004**: **Rejection Reason**: Teams that log worklogs in Jira today lose them on import and need a second tool,
  which keeps them in Jira.

### Start and end times per entry

- **ALT-005**: **Description**: Entries with a start and end time instead of a duration.
- **ALT-006**: **Rejection Reason**: More typing, and timers are out of scope. Durations also match Jira and YouTrack
  worklogs.

### Add time actions to `update_issues`

- **ALT-007**: **Description**: Log time as a field on `update_issues` and read it through `get_issue`, keeping 20 tools.
- **ALT-008**: **Rejection Reason**: The week view and editing a past entry by ID don't fit an issue tool and would
  overload it.

### Weekly schedules (deviation-only recording)

- **ALT-009**: **Description**: A dated weekly schedule per member that counts on days with no entries, with a "not
  worked" day marker.
- **ALT-010**: **Rejection Reason**: The owner's decision (OQ-1): the records must show what the time was spent on,
  which a schedule doesn't.

### Entries without an issue

- **ALT-011**: **Description**: Allow time not on an issue, as a "Not on an issue" row in the timesheet.
- **ALT-012**: **Rejection Reason**: Time is recorded against the work it was spent on; overhead gets its own work
  item.

## Implementation Notes

- **IMP-001**: Order: prototypes in `prototypes/time/`, then the API, CLI and MCP, then the UI (task 034).
- **IMP-002**: On acceptance, done: product.md's v1 table, ADR-020 ALT-003's reason, task 024's out-of-scope line on
  worklogs. With the MCP merge: ADR-001 IMP-003's tool list, the agent skill (`src/Kupro.Cli/Skill/kupro/SKILL.md`),
  `/llms.txt` and the MCP tests. When it ships: the privacy policy and DPA lines, the landing page and pricing FAQ, and
  the switch pages' "No time tracking" line.
- **IMP-003**: The lock is checked in the feature service from the issue's saved state, not stored on the entry. The
  close-and-log save is one request (log time with a target state), so the service applies both in one transaction.
- **IMP-004**: A test holds the latency budget for a week's timesheet with a year of entries for 50 members.
- **IMP-005**: Danish term "tidsregistrering", to add to the [branding.md](../branding.md) glossary.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md): parity, and the MCP tool budget
- [ADR-008](ADR-008-work-item-model.md): entries are on issues; effort stays the estimate; Done and Canceled lock entries
- [ADR-009](ADR-009-multi-tenancy.md): time entries are tenant-owned
- [ADR-013](ADR-013-seats-billing.md): included in the seat; guests and agents have no entries
- [ADR-016](ADR-016-postgres-backups.md): no workspace deletion yet; the export comes before one
- [ADR-019](ADR-019-import.md): worklogs and work items as entries
- [ADR-020](ADR-020-sprint-close-burndown.md): time entries don't feed the sprint ledger

## References

- **REF-001**: [Task 034](../../tasks/034-time-registration.md)
- **REF-002**: Lov om tilrettelæggelse af arbejdstiden, the registration duty in force since 1 July 2024
- **REF-003**: EU Court of Justice, judgment C-55/18 (CCOO), 14 May 2019
