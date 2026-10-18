---
title: 'ADR-005: Authentication — Keycloak + Kupro API Tokens'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['security', 'auth', 'mcp', 'cli']
supersedes: ''
superseded_by: ''
description: >-
  Keycloak (self-hosted) is the identity provider for web (OIDC + PKCE), CLI (device authorization grant) and remote MCP
  (OAuth 2.1). Kupro additionally issues hashed personal/agent API tokens for CI and headless agents.
---

# ADR-005: Authentication — Keycloak + Kupro API Tokens

## TL;DR

- Keycloak self-hosted in the compose stack; one realm for SaaS.
- Web: OIDC authorization code + PKCE. API validates JWTs.
- CLI: `kupro login` via OAuth device authorization grant.
- MCP (remote): OAuth 2.1 per MCP authorization spec with Keycloak as authorization server.
- Headless: Kupro-issued API tokens (personal or agent), stored hashed, scoped to workspace, revocable.

## Status

**Accepted**

## Context

Agents authenticate in three ways: interactive CLI, MCP clients (Claude, Cursor, …) and headless CI. Keycloak is EU-friendly
OSS, supports social login brokering (GitHub/GitLab) and enterprise SSO later.

## Decision

As above. Authorization (workspace membership, roles Owner/Member/Guest) lives in Kupro, not Keycloak — Keycloak only answers "who".

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
- **IMP-002**: API tokens prefixed (`kp_pat_`, `kp_agent_`) for secret scanning; SHA-256 hashed at rest.
- **IMP-003**: Agent tokens carry an agent name shown in the activity log ([ADR-001](ADR-001-agent-parity.md)).
- **IMP-004**: The API (MCP resource server) serves Protected Resource Metadata (RFC 9728) itself — Keycloak doesn't.
- **IMP-005**: Our own image (`infrastructure/keycloak`, published by CI): stock Keycloak plus a `kupro` theme and
  the realm. The login theme restyles `keycloak.v2` with the web app's tokens, CSS only, so every Keycloak page keeps
  working; the email theme replaces the layout and the main mails (EN/DA). The realm reads `${REALM_*}` from the
  environment on first import (client URLs, SMTP); dev users live in a separate file that only dev and E2E mount.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md) — actor attribution
- [ADR-004](ADR-004-hosting.md) — deployment
- [ADR-013](ADR-013-seats-billing.md) — member status; deactivation revokes tokens
