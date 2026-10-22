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
fully controllable by agents (CLI + MCP), and a meeting agent that updates tickets while the team talks. First users: dev teams at Danish/EU startups and scaleups — and the team building it.
Bootstrapped; offered as SaaS, open-core AGPL.

## Pitch

A European alternative to Jira, Azure DevOps, Linear, Trello and Monday. Say it in standup and it's in the tracker: the
meeting agent listens and proposes the ticket changes. Everything a human can do in the UI, an agent
can do via CLI or MCP. Planning is a first-class citizen: the delivery plan lays out every project's epics, features
and delivery dates over the year and shows whether the team you have can deliver it — and you create the work in it.

## Target customer

- **v1**: dev teams (2–30 people) at Danish/EU startups and scaleups who care about data sovereignty and speed.
- **Later**: public sector / enterprise leaving Jira (needs self-hosting, SSO, Jira importer).

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
| Sprints | Time tracking |
| List, board, delivery plan views | Dashboards / reports |
| Search, command palette, keyboard-first | Per-project permissions |
| Web, REST API, CLI, MCP; one-line CLI install, `kupro install mcp` ([ADR-018](adr/ADR-018-cli-distribution.md)) | AI beyond the meeting agent (chat, summaries, voice control) |
| Meeting agent: suggest mode by default, full auto opt-in, within a monthly AI budget ([task 015](../tasks/015-meeting-agent.md), [ADR-013](adr/ADR-013-seats-billing.md)) | Self-hosting package |
| Members page: roles, free guests, billing access ([ADR-013](adr/ADR-013-seats-billing.md)) | Billing (subscriptions, invoices) |
| Workspace export: everything, at any time ([task 016](../tasks/completed/016-workspace-export.md)) | |
| Public website: landing, pricing, legal pages ([ADR-017](adr/ADR-017-public-site.md)) | |
| English + Danish UI ([ADR-011](adr/ADR-011-i18n.md)) | |

## Integrations

- **Git**: GitHub first (link `KEY-123` in branch/PR → link + state transitions), then GitLab, Gitea, Bitbucket.
- **Import**: GitHub Issues and Linear first (startup audience), then Jira and Azure DevOps.

## After v1

- Voice control of the UI outside meetings ("move KUP-12 to next sprint"), on the meeting agent's vendors
  ([ADR-015](adr/ADR-015-meeting-agent-vendors.md)).
- Self-hosting package (single compose file).
- Webhooks, billing ([ADR-013](adr/ADR-013-seats-billing.md)), enterprise SSO (open-core, see [ADR-007](adr/ADR-007-licensing.md)).
