---
title: 'ADR-002: Backend — .NET 10, Plain Controllers, EF Core, PostgreSQL'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['architecture', 'backend', 'database']
supersedes: ''
superseded_by: ''
description: >-
  Backend is ASP.NET Core (.NET 10) with plain MVC controllers, one service per feature, EF Core with Npgsql, and
  self-hosted PostgreSQL 18. No DDD/CQRS/repository layers; latency is dominated by I/O, so keep the stack boring.
---

# ADR-002: Backend — .NET 10, Plain Controllers, EF Core, PostgreSQL

## TL;DR

- ASP.NET Core on .NET 10, plain `[ApiController]` controllers — thin, call one feature service.
- One plain service per feature owns its data access (EF Core `DbContext` via factory) and rules. No repositories, no MediatR.
- PostgreSQL 18 (self-hosted, see [ADR-004](ADR-004-hosting.md)). Full-text search via `tsvector` with `english` + `danish` configs.
- Realtime: SignalR; cross-instance fan-out via Postgres `LISTEN/NOTIFY` when we scale beyond one instance.

## Status

**Accepted**

## Context

Solo developer, familiar with .NET. Performance target ([ADR-010](ADR-010-performance-budget.md)) is mostly I/O-bound;
framework overhead is negligible next to query design.

## Decision

- Feature folders: `Features/Issues/{IssuesController, IssueService, Issue entity, DTOs}`.
- `IDbContextFactory<FlowstateDbContext>`, `await using`, `AsNoTracking()` for reads.
- Projections to DTOs in queries (`Select`) — never load full graphs for lists.
- EF migrations, applied on startup in dev; explicit migration step in deploy.
- Background jobs: start with `BackgroundService` + a Postgres-backed queue table; no broker until needed.
- Errors: Problem Details with stable codes ([ADR-001](ADR-001-agent-parity.md)).

## Consequences

### Positive

- **POS-001**: Boring, well-known stack; agents write it well.
- **POS-002**: Postgres covers relational, search, queue and pub/sub — one stateful component.

### Negative

- **NEG-001**: Postgres FTS less capable than a search engine (typo tolerance). Add `pg_trgm` for fuzzy title match.

## Alternatives Considered

### Minimal APIs

- **ALT-001**: **Rejection Reason**: Preference for controllers; no measurable speed difference for an I/O-bound app.

### Separate search engine (Meilisearch/Elastic)

- **ALT-002**: **Rejection Reason**: Extra stateful service on a single VPS; Postgres FTS + trigram is enough for v1.

## Implementation Notes

- **IMP-001**: IDs: `Guid` v7 (time-ordered) as PK; per-project sequence number for `KEY-123`.
- **IMP-002**: Integration tests use Testcontainers Postgres — no in-memory provider.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md) — service layer is the command layer
- [ADR-009](ADR-009-multi-tenancy.md) — workspace isolation in EF
- [ADR-010](ADR-010-performance-budget.md) — latency budget
- [ADR-012](ADR-012-aspire.md) — local orchestration, migrations in publish
