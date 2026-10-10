---
title: 'Flowstate — Product Vision & Scope'
description: >-
  What Flowstate is, who it is for, the principles that decide feature fights, and what is in/out of v1.
  Technical decisions live in ADRs (docs/adr/INDEX.md); this doc only covers product intent.
status: 'Draft'
purpose: 'Canonical source for product vision, target customer, principles and v1 scope.'
---

# Flowstate — Product Vision & Scope

## TL;DR

Danish, EU-sovereign, agent-first issue tracker and planner. Linear's speed, Jira's planning depth without the bloat,
fully controllable by agents (CLI + MCP). First users: dev teams at Danish/EU startups and scaleups — and the author.
Bootstrapped, built for fun and own use; sellable as SaaS, open-core AGPL.

## Pitch

A European alternative to Jira, Azure DevOps, Linear, Trello and Monday. Everything a human can do in the UI, an agent
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
| Web, REST API, CLI, MCP | Built-in AI |
| English + Danish UI ([ADR-011](adr/ADR-011-i18n.md)) | Self-hosting package |

## Integrations

- **Git**: GitHub first (link `KEY-123` in branch/PR → link + state transitions), then GitLab, Gitea, Bitbucket.
- **Import**: GitHub Issues and Linear first (startup audience), then Jira and Azure DevOps.

## After v1

- **AI in the room**: agent listens during meetings and updates tickets live; voice control of the UI
  ("move FLOW-12 to next sprint"). Speech-to-text via EU vendor (syv.ai Hviske or Scaleway), LLM via EU vendor (e.g. Mistral).
  Starts in suggest mode (every action approved per ticket) so teams see it work safely; full auto is opt-in.
  Spec: [task 015](../tasks/015-meeting-agent.md).
- Self-hosting package (single compose file).
- Webhooks, billing ([ADR-013](adr/ADR-013-seats-billing.md)), enterprise SSO (open-core, see [ADR-007](adr/ADR-007-licensing.md)).
