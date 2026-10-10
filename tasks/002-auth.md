---
title: 'Task 002: Authentication and API tokens'
description: >-
  Keycloak OIDC for web, device flow for CLI, JWT validation in API, Flowstate-issued personal and agent API tokens.
status: 'Todo'
phase: 0
depends_on: [001]
purpose: 'Implementation spec for task 002.'
---

# Task 002: Authentication and API tokens

See [ADR-005](../docs/adr/ADR-005-auth-keycloak.md).

## Scope

- Keycloak dev realm export committed; clients: `web` (public, PKCE), `cli` (device grant).
- API: JWT bearer + API-token auth handler; user provisioned on first login.
- API tokens: create/list/revoke; hashed; `fs_pat_` / `fs_agent_` prefixes; agent name.
- SPA login/logout.

## Acceptance criteria

- [ ] Web login works end-to-end locally.
- [ ] `GET /api/v1/me` with `Authorization: Bearer fs_pat_...` works.
- [ ] Revoked token rejected immediately.
