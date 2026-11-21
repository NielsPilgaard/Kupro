---
title: 'ADR-021: Time Registration'
status: 'Proposed'
date: '2026-11-21'
authors: 'Niels'
tags: ['domain', 'time', 'privacy', 'legal']
supersedes: ''
superseded_by: ''
description: >-
  Time registration moves into v1. A time entry is a member, a date, minutes, and optionally an issue and a note. A
  day's working time is the sum of its entries, or the member's dated weekly schedule when the day has none. That
  answers the Danish duty to record daily working time and keeps Jira and YouTrack worklogs on import. It is off by
  default per workspace. Members see their own entries, Owners see and export everyone's. Every change is kept, and
  entries are kept for 5 years.
---

# ADR-021: Time Registration

## TL;DR

- **Model**: a time entry = member, date, minutes, optional issue and note. A day's working time is the sum of its
  entries, else the member's dated weekly schedule. One register covers worklogs and the legal duty.
- **Access**: off by default per workspace. Members see and edit only their own; Owners see and export everyone's.
- **Records**: every change kept (who, when, old and new); kept 5 years, also after the member leaves.
- **Parity**: REST and CLI do everything; MCP logs and reads time (OQ-5); export is not on MCP.

## Status

**Proposed** — 2026-11-21. Awaiting the owner's acceptance. Open questions OQ-1 to OQ-5 are below. The legal reading
in Context is ours and is checked before the site or the app claims compliance (a manual step). The ADR records what
that check changes.

## Context

Since 1 July 2024 the Danish Working Time Act (lov om tilrettelæggelse af arbejdstiden) requires employers to have a
system that records the length of each employee's daily working time. The law follows the EU Court of Justice's
judgment C-55/18 (CCOO). As we read it:

- It covers all employees. Employees who largely set their own hours ("selvtilrettelæggende") can be exempt, and
  collective agreements can set their own rules.
- The system must be objective, reliable and accessible, the employee must be able to see their own records, and the
  records are kept for 5 years.
- The duty is the **length** of the day, not what the time went on. Recording only deviations from an agreed schedule
  may be enough.

Teams leaving Jira or Azure DevOps often do this in their tracker today, with worklogs on issues, and they will not
move to a tool that sends them to a second system. [product.md](../product.md) has time tracking out of v1, and
[ADR-020](ADR-020-sprint-close-burndown.md) ALT-003 rejected completed-effort fields because of it. Imports
([ADR-019](ADR-019-import.md)) sum Jira worklogs and YouTrack spent time into the details block, because there is
nowhere to put them.

Time logged on work is something teams want for themselves (estimates against actuals, worklogs). The legal duty only
needs the day's length. One model that serves both is simpler than two.

## Decision

### Scope

Time registration is in v1. product.md moves "Time tracking" from Out to In and links this ADR. Payroll, overtime,
holiday and absence management, timers, billing customers for time and approval flows stay out (task 034).

### Time entry

| Column | Notes |
|---|---|
| `workspace_id`, `id` | Tenant-owned ([ADR-009](ADR-009-multi-tenancy.md)) |
| `member_id` | A human member: Owner, Member, or a placeholder from an import ([ADR-019](ADR-019-import.md)). Guests and agents have no entries. |
| `date` | A calendar date, not a time. Not later than today in Europe/Copenhagen (as in ADR-020). |
| `minutes` | 1–1440. Entries made in Kupro keep a member's day at 1440 or less (imports: see Import). |
| `issue_id` | Nullable: time not on an issue. |
| `note` | Optional plain text. |
| `via` | The activity log's `Surface` (`web`, `cli`, `mcp`, `api`, `import`); the meeting agent doesn't log time. |
| `created_at`, `updated_at` | |

- An agent logs for the person it acts for ([ADR-001](ADR-001-agent-parity.md) attribution). The entry is that
  person's, and its history names the agent.
- Time entries don't change an issue's effort, the sprint ledger or burndown ([ADR-008](ADR-008-work-item-model.md),
  [ADR-020](ADR-020-sprint-close-burndown.md)). Effort stays the estimate, and logged time is what was spent.
- Logging time is not an activity feed item, because it would flood the feed. The issue shows its total.
- Index `(workspace_id, member_id, date)`, so a week's timesheet is one query
  ([ADR-010](ADR-010-performance-budget.md)), and `(workspace_id, issue_id)` for an issue's total.

### Schedule and day markers

- A member may have a weekly schedule: minutes per weekday. The member sets it; Owners see it but don't set or
  approve it (approval flows are out of scope). Schedules are **dated versions**. A change applies from a
  date the member picks (today or later), and earlier days keep the schedule in force then, so past exports don't
  change.
- A day's working time = the sum of its entries, if it has any. Otherwise it is the schedule's minutes for that
  weekday, unless the day has a **not worked** marker, in which case it is 0. The marker only turns the schedule off:
  entries on a marked day count as usual.
- The export says, per day, whether its minutes came from entries or from the schedule.
- Schedules and markers apply only if the legal check allows deviation-only recording (OQ-1). Without them, a member
  logs every working day.

### Who sees what

- A member sees and edits their own entries, schedule and markers. Members don't see each other's entries or day totals.
- Owners see everyone's and export them, but don't edit them: the employee's record is the employee's. A member
  corrects their own; a correction for someone who has left is a note in the export, not an edit.
- A deactivated member can't sign in, so their access to their own records goes through an Owner's export.
- An issue shows its logged total to Owners and Members who can see it, not to guests. Names per entry are shown to
  Owners only (OQ-2).

### Reliability

- Every create, edit and delete of an entry, schedule or marker writes a row to an append-only `time_entry_change`
  table: who, when, via, and old and new values. Deleting an entry keeps its history, so an Owner can see what was
  removed and by whom. An Owner sees this history per member, and a member sees the history of their own entries.
- Optionally, days older than N days lock unless an Owner reopens them (OQ-3).

### Retention and privacy

- Entries, markers and their history are kept for 5 years after the day they record, also after the member is
  deactivated, and are still exported. A nightly job deletes them after that, by the recorded day, not by when the row
  changed. A schedule version is deleted only once no day in the last 5 years falls under it, so past counts don't
  change.
- The legal basis is the employer's legal obligation, so an erasure request doesn't delete entries inside the 5 years.
  The privacy policy and the DPA say so (a manual step).
- Kupro has no workspace deletion yet (ADR-016 NEG-004). When it gets one, deleting a workspace with time entries
  first asks an Owner to export them, and the export page says why. Self-hosters keep their own records.

### Workspace setting

`time_registration` is off by default and an Owner turns it on ("Simple", product principle 3). Off, nothing about
time shows in the app and new entries are refused (`time_registration_off`). Turning it off after use keeps the data:
members can still read their own entries, Owners can still export, and the retention above still applies.

### Price

Included in the seat price ([ADR-013](ADR-013-seats-billing.md)). No add-on (OQ-4).

### Parity and MCP

- REST and CLI: log, edit and delete own entries; list own entries for a range; schedule and markers; an issue's total;
  Owners' list and export (CSV and JSON) per member or all, for a date range. The commands are in task 034.
- MCP: the tool budget is full ([ADR-001](ADR-001-agent-parity.md) IMP-003). Task 034 asks for actions on an
  existing tool. This ADR departs from that: `relate_issues` and `unrelate_issues` merge into one `relations` tool
  (`action: relate | unrelate`), and a new `time` tool takes the freed place (`action: log | edit | delete | week`),
  because time not on an issue and the week view don't fit an issue tool (ALT-007). `get_issue` gains the issue's
  total. The budget stays at 20 (OQ-5).
- Not on MCP, each an opt-out in the parity test: the export and the Owners' list of everyone's entries (personal
  data in bulk), and schedules and day markers (settings a person sets once, in the web app or the CLI).

### Import

When the setting is on, Jira worklogs and YouTrack work items become time entries for the mapped person, on the
imported issue, with `via = import`. When it is off, they stay summed in the details block, as today. Azure DevOps
has only a completed-work total per item, which stays in the details block.

- A person mapped to a member or their own placeholder gets entries. Skipped users' worklogs (the shared placeholder,
  [ADR-019](ADR-019-import.md)) stay summed in the details block: one member must not hold several people's time.
- Imported entries are kept as the source recorded them: the daily cap and the future-date check don't apply, and the
  plan warns about days over 24 hours. The same holds when a placeholder is merged into a member and its entries move.

## Open questions

- **OQ-1**: Is deviation-only recording (schedules and markers) enough under the law? Proposed: **yes, if the legal
  check confirms**. Otherwise drop schedules and markers.
- **OQ-2**: An issue's total: names visible to Owners only, or to everyone who sees the issue? Proposed: **Owners only**.
  Members see a total, and their own share. On an issue one person worked on, the total is that person's time anyway;
  proposed to accept that, since the issue's assignee is visible too.
- **OQ-3**: Lock days older than N days? Proposed: **no lock in v1**. The change history already shows every edit,
  and a lock adds an Owner chore. Revisit if the legal check asks for one.
- **OQ-4**: Included in the seat price? Proposed: **yes**.
- **OQ-5**: MCP shape: merge `relate_issues` and `unrelate_issues` to free a place for a `time` tool, or put the time
  actions on existing tools as task 034 says? Proposed: **merge**. It changes the MCP surface for existing agent
  setups (NEG-004).

## Consequences

### Positive

- **POS-001**: A Danish employer meets the registration duty in the tool the team already uses, so it's one reason
  less to stay in Jira.
- **POS-002**: One model: time on issues and the day's length are the same rows, so nobody logs twice.
- **POS-003**: Jira and YouTrack worklogs survive the move instead of being flattened into a number.
- **POS-004**: With schedules, a member with a normal week logs nothing most days.
- **POS-005**: Off by default, so teams that don't want it never see it.

### Negative

- **NEG-001**: Personal data about employees: retention, an erasure exception and Owner-only views to get right, and
  lines in the privacy policy and the DPA.
- **NEG-002**: Scope grows in v1: a new table set, a timesheet screen, settings, an export, and an MCP tool merge.
- **NEG-003**: Dated schedules make a day's count depend on history. Exports must read the version in force, not the
  current one.
- **NEG-004**: Merging `relate_issues` and `unrelate_issues` changes the MCP surface for existing agent setups.
- **NEG-005**: Compliance rests on our reading of the law until the legal check is done.

## Alternatives Considered

### A separate working-time register

- **ALT-001**: **Description**: Record the day's start, end and breaks on their own, apart from time logged on issues.
- **ALT-002**: **Rejection Reason**: Two places to log the same day. The law needs only the length, and the sum of the
  entries is the length.

### Leave it to a time tool

- **ALT-003**: **Description**: Keep time out of v1 and point employers at a Danish time-registration product.
- **ALT-004**: **Rejection Reason**: Teams that log worklogs in Jira today lose them on import and need a second tool,
  which keeps them in Jira.

### Start and end times per entry

- **ALT-005**: **Description**: Entries with a start and end time instead of a duration.
- **ALT-006**: **Rejection Reason**: More typing for no legal gain (the duty is the length), and timers are out of scope.
  Durations also match Jira and YouTrack worklogs.

### Add time actions to `update_issues`

- **ALT-007**: **Description**: Log time as a field on `update_issues` and read it through `get_issue`, keeping 20 tools.
- **ALT-008**: **Rejection Reason**: Time not on an issue and the week view don't fit an issue tool, and editing a past
  entry by ID would overload it.

## Implementation Notes

- **IMP-001**: Order: this ADR accepted, then prototypes in `prototypes/time/`, then the API, CLI and MCP, then the UI
  (task 034).
- **IMP-002**: On acceptance: product.md's v1 table, ADR-020 ALT-003's reason (time is tracked as entries, not effort
  fields), task 024's out-of-scope line on worklogs. With the MCP merge (OQ-5): ADR-001 IMP-003's tool list, the agent
  skill (`src/Kupro.Cli/Skill/kupro/SKILL.md`), `/llms.txt` and the MCP tests. When it ships: the privacy policy and
  DPA lines, the landing page and pricing FAQ, and the switch pages' "No time tracking" line.
- **IMP-003**: The week view and the export compute a day's count in one query that joins entries, the schedule
  version in force and markers. They don't loop over days in code.
- **IMP-004**: A test holds the latency budget for a week's timesheet with a year of entries for 50 members.
- **IMP-005**: Danish term "tidsregistrering", added to the [branding.md](../branding.md) glossary.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md): parity, and the MCP tool budget
- [ADR-008](ADR-008-work-item-model.md): effort stays the estimate, and time entries don't change it
- [ADR-009](ADR-009-multi-tenancy.md): time entries are tenant-owned
- [ADR-013](ADR-013-seats-billing.md): included in the seat; guests and agents have no entries
- [ADR-016](ADR-016-postgres-backups.md): no workspace deletion yet; the export comes before one
- [ADR-019](ADR-019-import.md): worklogs and work items as entries
- [ADR-020](ADR-020-sprint-close-burndown.md): time entries don't feed the sprint ledger

## References

- **REF-001**: [Task 034](../../tasks/034-time-registration.md)
- **REF-002**: Lov om tilrettelæggelse af arbejdstiden, the registration duty in force since 1 July 2024
- **REF-003**: EU Court of Justice, judgment C-55/18 (CCOO), 14 May 2019
