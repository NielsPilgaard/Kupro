---
title: 'ADR-001: Agent Parity — One Service Layer, Four Surfaces'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['architecture', 'api', 'cli', 'mcp', 'agents']
supersedes: ''
superseded_by: ''
description: >-
  Every user action is a method on a feature service. REST controllers, MCP tools and the web UI are thin adapters over
  it; the CLI is a client of the REST API. A CI parity test fails if an API operation lacks a CLI command or MCP tool.
---

# ADR-001: Agent Parity — One Service Layer, Four Surfaces

## TL;DR

- Business logic lives in one plain service per feature. Nothing else contains rules.
- REST controllers and MCP tools (hosted in the API process at `/mcp`) call those services directly.
- CLI is a .NET AOT binary using a client generated from the OpenAPI spec; `kupro mcp` exposes a local stdio MCP bridge.
- CI parity test: every OpenAPI `operationId` must map to a CLI command and an MCP tool (or be explicitly excluded).

## Status

**Accepted**

## Context

"Must be controllable entirely from an agent" is a core requirement. Parity rots fast if each surface is hand-built
without a check. Agents also need things humans don't: machine-readable output, stable IDs, idempotency, clear errors.

## Decision

- **Service layer is the command layer.** `IssueService.Create(...)`, `IssueService.Move(...)` etc.
- **REST** (`/api/v1/...`) is the canonical contract. OpenAPI generated from controllers.
- **MCP** server in the API process (streamable HTTP, OAuth via Keycloak, see [ADR-005](ADR-005-auth-keycloak.md)).
  Tools are coarse-grained (~12–20) to keep agent context small, e.g. `search_issues`, `get_issue`, `create_issues` (bulk),
  `update_issues` (bulk), `add_comment`, `list_sprints`, `get_plan`.
- **CLI** `kupro` (alias TBD): resource-verb shape (`kupro issue create`, `kupro issue list --json`).
- **Agent ergonomics, everywhere**:
  - Human-readable stable IDs (`KEY-123`) accepted wherever an ID is.
  - Markdown in/out for descriptions and comments.
  - `Idempotency-Key` header on all mutations.
  - Bulk create/update endpoints.
  - Problem Details (RFC 9457) errors with stable `type` codes.
  - Actor attribution: activity log records "agent X on behalf of user Y" when a token is an agent token.

## Consequences

### Positive

- **POS-001**: Parity is enforced by CI, not discipline.
- **POS-002**: Agents are a first-class audience; doubles as a great public API.

### Negative

- **NEG-001**: Every feature costs three adapters (controller, MCP tool, CLI command). Mitigated by keeping adapters trivial.
- **NEG-002**: MCP tool design (granularity) needs care; too many tools degrades agent performance.

## Alternatives Considered

### Generate CLI + MCP fully from OpenAPI

- **ALT-001**: **Description**: Auto-generate MCP tools and CLI commands 1:1 from operations.
- **ALT-002**: **Rejection Reason**: 1:1 mapping produces too many fine-grained tools; agent UX suffers. Generate the client, hand-write the thin surface.

### CLI talks to DB or embeds services

- **ALT-003**: **Rejection Reason**: Breaks SaaS model and auth; CLI must be a pure API client.

## Implementation Notes

- **IMP-001**: Parity test reads OpenAPI doc + MCP tool registry + CLI command tree; an `[NoAgentSurface("reason")]` attribute opts out.
- **IMP-002**: CLI output: table for TTY, JSON when `--json` or stdout is not a TTY.
- **IMP-003**: Tool budget: 22 tools after the meeting agent (task 015) added `meeting` and `review_proposals`, enforced
  by the MCP test. A new feature extends an existing tool's actions or consolidates before the budget grows.
  Import adds a read-only `imports` tool (23, [ADR-019](ADR-019-import.md)).

## Related Decisions

- [ADR-002](ADR-002-backend-stack.md) — controllers and services
- [ADR-005](ADR-005-auth-keycloak.md) — tokens for CLI and MCP
- [ADR-018](ADR-018-cli-distribution.md) — how the CLI is installed and MCP is set up
- [ADR-019](ADR-019-import.md) — import surfaces, MCP read-only
