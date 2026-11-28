---
title: 'Kupro — Product Vision & Scope'
description: >-
  What Kupro is, who it is for, the principles that decide feature fights, and what is in/out of v1.
  Technical decisions live in ADRs (docs/adr/INDEX.md); this doc only covers product intent.
status: 'Draft'
purpose: 'Canonical source for product vision, target customer, principles and v1 scope.'
---

# Kupro — Product Vision & Scope

## TL;DR

Danish, EU-sovereign, agent-first issue tracker and planner. Linear's speed, Jira's planning depth without the bloat,
fully controllable by agents (CLI + MCP), and a meeting agent that updates tickets while the team talks. Why: [vision.md](vision.md).
First users: developers and sales at Danish/EU startups and scaleups — and the team building it.
Bootstrapped; offered as SaaS, open-core AGPL.

## Pitch

A European alternative to Jira, Azure DevOps, Linear, Trello and Monday. Say it in standup and it's in the tracker: the
meeting agent listens and proposes the ticket changes. Everything a human can do in the UI, an agent
can do via CLI or MCP. Planning is a first-class citizen: the delivery plan lays out every project's epics, features
and delivery dates over the year and shows whether the team you have can deliver it — and you create the work in it.

## Target customer

- **v1**: teams of 2–30 people at Danish/EU startups and scaleups, developers and sales in the same tool, who care
  about data sovereignty and speed. Sales uses the same concepts as developers (labels and views, no sales vocabulary).
- **Later**: public sector / enterprise leaving Jira (needs self-hosting, SSO).

## Principles (in priority order)

1. **Agent parity** — every action available in UI is available via API, CLI and MCP. See [ADR-001](adr/ADR-001-agent-parity.md).
2. **Fast** — every action < 500 ms p99, UI feels instant. See [ADR-010](adr/ADR-010-performance-budget.md).
3. **Simple** — cut features rather than add confusion. One obvious way to do each thing.
4. **Sovereign** — EU vendors and infra only. See [ADR-006](adr/ADR-006-eu-vendor-policy.md).
5. **Planning that works** — delivery plan is a differentiator, not an afterthought. See [delivery-plan.md](delivery-plan.md).

## v1 scope

| In | Out (v1) |
|---|---|
| Issues: types, hierarchy, fixed workflow, effort, assignee, priority, labels, dates, relations ([ADR-008](adr/ADR-008-work-item-model.md)) | Custom fields |
| Markdown descriptions, images, links, comments, activity log | Custom workflows |
| Sprints | Timers, payroll, absence |
| Time registration on work items, off by default ([ADR-021](adr/ADR-021-time-registration.md)) | |
| List, board, delivery plan views | Dashboards / reports |
| Search, command palette, keyboard-first | Per-project permissions |
| Web, REST API, CLI, MCP; one-line CLI install, `kupro install mcp` ([ADR-018](adr/ADR-018-cli-distribution.md)) | AI beyond the meeting agent and voice control (chat, summaries) |
| Meeting agent: suggest mode by default, full auto opt-in, within a monthly AI budget ([task 015](../tasks/015-meeting-agent.md), [ADR-013](adr/ADR-013-seats-billing.md)) | Self-hosting package |
| Members page: roles, free guests, billing access ([ADR-013](adr/ADR-013-seats-billing.md)) | Billing (subscriptions, invoices) |
| Voice control: push to talk for changes, navigation and dictation ([task 036](../tasks/036-voice-control.md)) | |
| A workspace's own AI key or EU endpoint, unlimited voice ([ADR-022](adr/ADR-022-ai-providers-byok.md)) | |
| Workspace export: everything, at any time ([task 016](../tasks/completed/016-workspace-export.md)) | |
| Import from Jira, Azure DevOps and CSV, re-runnable until cutover ([ADR-019](adr/ADR-019-import.md)) | Two-way sync with other trackers |
| Public website: landing, pricing, legal pages ([ADR-017](adr/ADR-017-public-site.md)) | |
| English + Danish UI ([ADR-011](adr/ADR-011-i18n.md)) | |

## Integrations

- **Git**: GitHub first (link `KEY-123` in branch/PR → link + state transitions), then GitLab, Gitea, Bitbucket.
- **Import**: Jira and Azure DevOps first, then Linear and Trello, then GitHub Issues, Monday and YouTrack ([ADR-019](adr/ADR-019-import.md)).

## After v1

- Self-hosting package (single compose file).
- Webhooks, billing ([ADR-013](adr/ADR-013-seats-billing.md)), enterprise SSO (open-core, see [ADR-007](adr/ADR-007-licensing.md)).
