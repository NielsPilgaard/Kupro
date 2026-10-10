---
title: 'ADR-003: Frontend — React SPA with Optimistic Updates'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['architecture', 'frontend']
supersedes: ''
superseded_by: ''
description: >-
  Web UI is a React + TypeScript SPA built with Vite, served as static files. Server state via TanStack Query with
  optimistic mutations so the UI feels instant. The delivery plan view is custom-built. Visual direction is chosen via
  task 000 and specified in design-guide.md.
---

# ADR-003: Frontend — React SPA with Optimistic Updates

## TL;DR

- React 19 + TypeScript + Vite, static SPA (no SSR — app is behind login). Bun is the JS toolchain (install, scripts, CI, image build).
- TanStack Query for server state, every mutation optimistic; TanStack Router for typed routes.
- TS API client generated from the OpenAPI spec ([ADR-001](ADR-001-agent-parity.md)) with `@hey-api/openapi-ts` (fetch client + TanStack Query plugin).
- Styling/components: Tailwind + headless primitives (shadcn/ui-style) — visual language in [design-guide.md](../design-guide.md).
- Delivery plan view custom-built (see [delivery-plan.md](../delivery-plan.md)).

## Status

**Accepted**

## Context

Largest community and best agent knowledge matter for a solo dev working with agents. Perceived speed comes from the
client, not the server: optimistic updates + realtime sync make actions feel 0 ms.

## Decision

React SPA as above. Keyboard-first: global command palette (`Ctrl/Cmd+K`), single-key shortcuts for common actions.
Realtime updates via SignalR client invalidate/patch TanStack Query caches.

## Consequences

### Positive

- **POS-001**: Biggest ecosystem; agents produce good React code.
- **POS-002**: Static hosting of the SPA; API is the only dynamic component.

### Negative

- **NEG-001**: Two languages (C#, TS). Mitigated by generated client.
- **NEG-002**: Optimistic updates need careful rollback/conflict handling.

## Alternatives Considered

### Blazor Server

- **ALT-001**: **Rejection Reason**: Every interaction round-trips; laggy drag interactions in plan view.

### SvelteKit

- **ALT-002**: **Rejection Reason**: Smaller community and less agent training data.

### Local-first sync engine (Linear-style, full dataset in IndexedDB)

- **ALT-003**: **Rejection Reason**: Big complexity for v1. Revisit if optimistic + cache is not fast enough.

### Orval / openapi-typescript + openapi-fetch (TS codegen)

- **ALT-004**: **Rejection Reason**: hey-api's TanStack Query plugin gives typed query/mutation options with less glue; actively maintained.

### npm / pnpm

- **ALT-005**: **Rejection Reason**: Bun installs and runs scripts much faster; one tool for package manager + script runner.

## Implementation Notes

- **IMP-001**: Strings via i18n library from day 1 ([ADR-011](ADR-011-i18n.md)).
- **IMP-002**: E2E tests with Playwright.
- **IMP-003**: Codegen: API writes the spec to `openapi/flowstate.json` at repo root on build
  (`Microsoft.Extensions.ApiDescription.Server`, `OpenApiDocumentsDirectory`). Spec is committed; CI fails on drift.
  `@hey-api/openapi-ts` reads `../openapi/` and generates `web/src/api/gen/` (gitignored) with `@hey-api/client-fetch` +
  `@tanstack/react-query` plugins (typed `queryOptions` / `mutationOptions`); runs in `predev`/`prebuild` scripts, so
  the Docker build generates it too.
- **IMP-004**: Pin `@hey-api/openapi-ts` to an exact version — it is pre-1.0 and ships breaking changes in minors.
- **IMP-005**: Bun: commit `bun.lock`, no `package-lock.json`; pin Bun version (`packageManager` field + CI setup + `oven/bun` image tag).
  Bun is build-time only — prod serves static files from nginx ([ADR-012](ADR-012-aspire.md) IMP-002), no Bun runtime in prod.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md) — generated API client
- [ADR-010](ADR-010-performance-budget.md) — perceived latency
- [design-guide.md](../design-guide.md) — tokens and component specs
- [ADR-012](ADR-012-aspire.md) — SPA runs under Aspire locally
