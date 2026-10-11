---
title: 'ADR Index'
description: 'Concept → ADR map. Every concept is owned by exactly one ADR.'
status: 'Active'
purpose: 'Lookup table from concept to the ADR that owns it.'
---

# ADR Index

| Concept | ADR | Status |
|---|---|---|
| Agent parity (API / CLI / MCP), feature services as commands | [ADR-001](ADR-001-agent-parity.md) | Accepted |
| Backend stack, controllers, EF Core, PostgreSQL | [ADR-002](ADR-002-backend-stack.md) | Accepted |
| Frontend stack, optimistic updates, component library, TS client codegen (hey-api) | [ADR-003](ADR-003-frontend-stack.md) | Accepted |
| Hosting, deployment, backups, object storage | [ADR-004](ADR-004-hosting.md) | Accepted |
| Authentication, API tokens, MCP OAuth | [ADR-005](ADR-005-auth-keycloak.md) | Accepted |
| EU vendor policy, vendor register | [ADR-006](ADR-006-eu-vendor-policy.md) | Accepted |
| Licensing, open-core split | [ADR-007](ADR-007-licensing.md) | Accepted |
| Work item model: types, hierarchy, states, effort, sprints, relations, milestones, project capacity | [ADR-008](ADR-008-work-item-model.md) | Accepted |
| Multi-tenancy, workspace isolation | [ADR-009](ADR-009-multi-tenancy.md) | Accepted |
| Performance budget and enforcement | [ADR-010](ADR-010-performance-budget.md) | Accepted |
| Internationalization (English + Danish) | [ADR-011](ADR-011-i18n.md) | Accepted |
| Local orchestration (Aspire), prod compose generation (`aspire publish`) | [ADR-012](ADR-012-aspire.md) | Accepted |
| Seats, billing unit, member status | [ADR-013](ADR-013-seats-billing.md) | Proposed |
| Observability: self-hosted Grafana stack, request log, dashboards, alerts, uptime check | [ADR-014](ADR-014-observability.md) | Accepted |
