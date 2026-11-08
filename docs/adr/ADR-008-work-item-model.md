---
title: 'ADR-008: Work Item Model'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['domain', 'issues', 'sprints', 'planning']
supersedes: ''
superseded_by: ''
description: >-
  Defines issues: fixed types (Epic, Feature, Task) with a strict hierarchy, a fixed workflow, effort in hours,
  dates, relations and per-project sprints, plus planning data (milestones, project capacity). Bug is a label; only
  people can be assignees. No custom fields or workflows in v1.
---

# ADR-008: Work Item Model

## TL;DR

- Workspace → Projects → Issues. Issue key `PROJ-123`.
- Types: **Epic → Feature → Task** (strict parent/child). Types are filterable and searchable. Parents are optional;
  two levels (Epic → Task) is fully supported. Projects are not epics.
- Fixed workflow: Backlog, Todo, In Progress, In Review, Done, Canceled.
- Effort in **hours**, on every type. Bug = label on Task. Only people can be assignees.
- Sprints per project, issue in ≤ 1 sprint.
- Relations: blocks / blocked by, relates to, duplicates.
- Planning: milestones (dated markers) and project capacity (people per project, from a month onward).

## Status

**Accepted** — OQ-1 to OQ-4 resolved 2026-10-03; OQ-3 reversed 2026-10-31.

## Context

Simplicity is a top principle, but the delivery plan ([delivery-plan.md](../delivery-plan.md)) needs effort, dates and
dependencies to be first-class. Types make top-level work (epics) easy to find.

## Decision

### Issue fields

| Field | Notes |
|---|---|
| key, title, description | Description is Markdown with images and links |
| type | Epic / Feature / Task |
| parent | Epic ← Feature ← Task; Task may also sit directly under Epic. Every parent is optional (see Using the types) |
| state | Fixed workflow above |
| assignee | Single human member. The assignee answers for the work; agents act on issues but are never assignees |
| priority | None, Low, Medium, High, Urgent |
| labels | Workspace-level; `bug` is a built-in label |
| effort | Hours (decimal), any type. Epics/features hold their own top-down estimate and show child roll-up beside it |
| start date, due date | Drive the delivery plan |
| sprint | Optional |
| relations | blocks, relates, duplicates |
| comments, attachments, activity | Activity log is append-only, includes actor (human/agent) |

### Using the types

- **Epic**: a theme of a quarter or more. **Feature**: ships within weeks, a bar on the delivery plan. **Task**: the
  work. The create dialog shows the line for the chosen type.
- Every parent is optional, and two levels (**Epic → Task**) is a fully supported way to work: a team that never
  creates a Feature needs no setting, and the delivery plan shows its epics as bars.
- New issues default to Task, so filing work never asks for a type. The type can change later (a task that grows
  becomes a Feature); the change is checked against the parent and children.
- Projects are not epics. A project is long-lived and owns the key, sprints, capacity and members; time-boxed
  deliverables are epics (Linear projects import as epics, [ADR-019](ADR-019-import.md)).

### Sprints

Per project, fixed start/end, one active at a time. Sprint view = board filtered by sprint. Closing, carry-over and
burndown: [ADR-020](ADR-020-sprint-close-burndown.md).

### Planning

Data the [delivery plan](../delivery-plan.md) needs beyond issues. Kept deliberately small.

| Entity | Fields | Notes |
|---|---|---|
| Milestone | name, date, project (optional) | No project = workspace-wide |
| Project capacity | project, from month, people | Step function: "3 from Jan 2027, 5 from Jul 2027". Latest entry ≤ month applies |
| Workspace setting | hours per person per week | Default 37 (Danish full-time). Lower it to account for meetings and vacation |

Supply per month = people × hours per week × weeks in month. No per-person capacity, absence or holidays in v1.

## Resolved questions

- **OQ-1**: Effort unit → **hours**. One unit across all types, so sprint load and delivery plan demand use the same
  number. Plan UI may display large totals as people per month — display only.
- **OQ-2**: Bug → **label on Task**, not a type.
- **OQ-3**: Agents as assignees → **no** (reversed 2026-10-31; was yes). Assignee means responsibility, and an agent
  can't hold that. Agents still create and change issues as members of kind `agent`; the activity log attributes it.
  The API rejects an agent assignee with `assignee_not_human`; existing agent assignees were cleared.
- **OQ-4**: Sprints → **per project** in v1.

## Consequences

### Positive

- **POS-001**: Agents can rely on fixed types and states — no per-workspace discovery needed.

### Negative

- **NEG-001**: Teams with exotic workflows won't fit. Accepted cost of simplicity.

## Alternatives Considered

### Single issue type with free hierarchy

- **ALT-001**: **Rejection Reason**: Owner wants top-level work (epics) searchable by type.

## Related Decisions

- [ADR-009](ADR-009-multi-tenancy.md) — every entity carries `workspace_id`
- [ADR-013](ADR-013-seats-billing.md) — whether agent members count as seats
- [ADR-019](ADR-019-import.md) — how imports from deeper or looser models fit this one
- [ADR-020](ADR-020-sprint-close-burndown.md) — sprint close outcomes, split carry-over, burndown
