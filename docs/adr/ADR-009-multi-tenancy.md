---
title: 'ADR-009: Multi-Tenancy — Shared Schema with workspace_id'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['architecture', 'database', 'security']
supersedes: ''
superseded_by: ''
description: >-
  All tenants share one database and schema. Every tenant-owned row has workspace_id, enforced by an EF Core global
  query filter bound to the request's workspace and covered by tests.
---

# ADR-009: Multi-Tenancy — Shared Schema with workspace_id

## TL;DR

- One DB, one schema. `workspace_id` on every tenant-owned table, first column in composite indexes.
- EF Core global query filter sets workspace from the authenticated request context.
- References between tenant-owned rows are composite FKs `(workspace_id, x) → (workspace_id, id)`, so Postgres
  rejects a cross-workspace link even if application code misses one.
- Cross-tenant leakage tests run in CI. Self-hosted installs are just a single-workspace case.

## Status

**Accepted**

## Context

Startups → many small workspaces. Schema-per-tenant or DB-per-tenant adds ops burden on a single VPS.

## Consequences

### Positive

- **POS-001**: Simple migrations and ops; cheap per tenant.

### Negative

- **NEG-001**: A missing filter leaks data. Mitigated by global filter, composite workspace FKs, and model guard tests
  (every `workspace_id` entity is filtered; every tenant-to-tenant FK includes `workspace_id`). Postgres RLS remains
  the next layer if reads ever need database-level enforcement.
- **NEG-002**: Every referenced table carries an extra unique `(workspace_id, id)` constraint for the FKs to target.

## Alternatives Considered

### Database per tenant

- **ALT-001**: **Rejection Reason**: Ops overhead; revisit only for enterprise customers who demand it.

## Related Decisions

- [ADR-002](ADR-002-backend-stack.md) — EF Core
- [ADR-008](ADR-008-work-item-model.md) — entities
- [ADR-013](ADR-013-seats-billing.md) — workspace is the billing unit
