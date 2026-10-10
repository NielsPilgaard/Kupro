---
title: 'Delivery Plan View — Design'
description: >-
  Design brief for the delivery plan: a high-level, multi-project timeline (think Azure DevOps Delivery Plans) where a
  team lays out epics, features and delivery dates for a year and sees whether the plan fits the people they have.
  This is Flowstate's main differentiator.
status: 'Draft'
purpose: 'Canonical requirements and open questions for the delivery plan view.'
---

# Delivery Plan View — Design

## TL;DR

A portfolio timeline, not a person scheduler. Rows are **projects**, cards are **epics and features** with start and
target dates, overlaid with **milestones** and **cross-project dependencies**, over a horizon of months to a year
("our 10 projects in 2027"). A **capacity band** compares the effort the plan demands per month with the people
available, so the team can see when it must hire, where it has spare hands, and whether delivery dates are realistic.
Built from the same issues as everything else.

## Who and when

Leads, PMs and founders doing quarterly or yearly planning: "Does every project have a plan for 2027? Can we deliver
all of it with the team we have? When do we need to hire?" Day-to-day "who does what this week" is out of scope — that
is the board and sprints ([ADR-008](adr/ADR-008-work-item-model.md)).

## Why existing tools fail

- **ADO Delivery Plans**: right shape (teams × time, cards, markers, dependencies), but no capacity view at all — you
  can't see if the plan fits headcount. Slow, clunky to edit, tied to ADO iteration setup.
- **Jira Plans (Advanced Roadmaps)**: has capacity, but heavy, paid tier, painful to configure.
- **Linear**: clean project timelines, no capacity, no effort-to-headcount view.
- **Spreadsheets**: where this planning really happens today — disconnected from the actual work.

## Core questions the view must answer at a glance

1. Does each project have a plan for the horizon — which projects have nothing (or only undated work) for 2027?
2. When does each project deliver, and which features land when?
3. What depends on what across projects, and what slips if X slips?
4. Per month/quarter: how much effort does the plan demand vs. how many people we have — where are we short (hire)
   and where do we have spare capacity?
5. Is a delivery date realistic given the effort and the people allocated?

## Requirements (v1)

- **Rows**: one per project (default), selectable set of projects; optionally grouped by epic within a project.
- **Cards**: epics and features as bars spanning start → target date. Tasks never appear. Card shows title, state,
  effort, progress (done children / total).
- **Horizon & zoom**: month / quarter / year; default = rest of current year + next year. Smooth horizontal scroll.
- **Milestones**: dated markers, workspace-wide (e.g. "Launch", "Fiscal year end") or per project ("Atlas GA").
- **Dependencies**: "blocks" links between cards, including across projects; violated ones (blocker ends after
  dependent starts) highlighted.
- **Capacity band**: per month, **demand** (card effort in hours, spread evenly over each card's span) vs. **supply**
  (project capacity, see [ADR-008](adr/ADR-008-work-item-model.md) Planning). Shown as people: "needs 4.5, has 3".
  Colour deficits. Per project and workspace total. No double counting: an epic's own estimate only counts when none
  of its features are estimated.
- **Capacity editing**: set people per project from a month onward, right in the row header ("3 from Jan, 5 from Jul").
- **Plan health**: flag projects with no dated work in the horizon, cards without effort, cards without dates,
  target dates past their milestone.
- **Create inline**: drag on empty space in a project row → new feature (or epic) with dates prefilled; type title,
  Enter. Keyboard: select row + month, press `C`.
- **Edit inline**: drag to move, drag edges to change dates, drag between rows to move project. Capacity band updates
  live. Optimistic, < 100 ms feel.
- **No plan entity**: the view is a pure reflection of issues. Selected projects and horizon live in the URL.
- **Agent parity**: everything maps to plain issue mutations (dates, effort, relations) plus milestone and capacity
  CRUD. Add a `plan` query returning per-month demand / supply / gap per project, so an agent can answer "do we need
  to hire for Q2?" or "draft a 2027 plan for project X".

## Out of scope (v1)

- Per-person scheduling, per-person load, task-level bars.
- Automatic scheduling / levelling.
- Budget and cost (people and hours only, no salaries).
- Teams as an entity — projects are the rows.

## Resolved questions

- **OQ-1**: Effort unit → **hours**, on epics/features too (top-down estimate + child roll-up). See
  [ADR-008](adr/ADR-008-work-item-model.md).
- **OQ-2**: Supply → **people per project, from a month onward**; workspace total = sum. See ADR-008 Planning.
- **OQ-4**: Plan entity → **none**; the view is a reflection of issues.
- **OQ-6**: Rows → **projects only**; no team entity.

## Open questions

- **OQ-3**: Demand spreading. Proposal: even spread over the card's span. Later: front/back-loaded profiles.
- **OQ-5**: What-if scenarios ("hire 2 in March", "drop project Y") — v1 or later? Proposal: later; v1 is edit-in-place.
- **OQ-7**: Rendering — SVG/DOM vs. canvas. Proposal: SVG/DOM; card counts at this level are hundreds, not thousands.
- **OQ-8**: Build vs. library. Proposal: build custom — this is the differentiator; libraries constrain interaction.

## Related

- [ADR-003](adr/ADR-003-frontend-stack.md) — frontend stack
- [ADR-008](adr/ADR-008-work-item-model.md) — work item model (types, dates, effort, relations)
