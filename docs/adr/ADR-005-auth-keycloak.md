---
title: 'ADR-005: Authentication — Keycloak + Flowstate API Tokens'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['security', 'auth', 'mcp', 'cli']
supersedes: ''
superseded_by: ''
description: >-
  Keycloak (self-hosted) is the identity provider for web (OIDC + PKCE), CLI (device authorization grant) and remote MCP
  (OAuth 2.1). Flowstate additionally issues hashed personal/agent API tokens for CI and headless agents.
---

# ADR-005: Authentication — Keycloak + Flowstate API Tokens

## TL;DR

- Keycloak self-hosted in the compose stack; one realm for SaaS.
- Web: OIDC authorization code + PKCE. API validates JWTs.
- CLI: `flowstate login` via OAuth device authorization grant.
- MCP (remote): OAuth 2.1 per MCP authorization spec with Keycloak as authorization server.
- Headless: Flowstate-issued API tokens (personal or agent), stored hashed, scoped to workspace, revocable.

## Status

**Accepted**

## Context

Agents authenticate in three ways: interactive CLI, MCP clients (Claude, Cursor, …) and headless CI. Keycloak is EU-friendly
OSS, supports social login brokering (GitHub/GitLab) and enterprise SSO later.

## Decision

As above. Authorization (workspace membership, roles Owner/Member/Guest) lives in Flowstate, not Keycloak — Keycloak only answers "who".

## Consequences

### Positive

- **POS-001**: Standards-based; SSO/SAML for enterprise comes nearly free.
- **POS-002**: No password handling in our code.

### Negative

- **NEG-001**: Keycloak is heavy (JVM, ~1 GB RAM) on a small VPS.
- **NEG-002**: Two token types to validate (JWT + API token).

## Implementation Notes

- **IMP-001**: Keycloak MCP support (verified 2026-10-03, [Keycloak MCP guide](https://www.keycloak.org/securing-apps/mcp-authz-server)):
  MCP 2025-03-26 fully supported; newer spec versions experimental. Dynamic Client Registration (RFC 7591) supported —
  enable anonymous DCR with a Trusted Hosts policy. Resource Indicators (RFC 8707) and Client ID Metadata Documents
  are **experimental**: start Keycloak with `--features=resource-indicators,cimd`. Known gap: ChatGPT's client metadata
  is rejected. Fallback: pre-registered public client for known MCP hosts. Pin a Keycloak version and re-check on upgrade.
- **IMP-002**: API tokens prefixed (`fs_pat_`, `fs_agent_`) for secret scanning; SHA-256 hashed at rest.
- **IMP-003**: Agent tokens carry an agent name shown in the activity log ([ADR-001](ADR-001-agent-parity.md)).
- **IMP-004**: The API (MCP resource server) serves Protected Resource Metadata (RFC 9728) itself — Keycloak doesn't.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md) — actor attribution
- [ADR-004](ADR-004-hosting.md) — deployment
- [ADR-013](ADR-013-seats-billing.md) — member status; deactivation revokes tokens
