---
title: 'Task 002: Authentication and API tokens'
description: >-
  Keycloak OIDC for web, device flow for CLI, JWT validation in API, Flowstate-issued personal and agent API tokens.
status: 'Done'
phase: 0
depends_on: [001]
purpose: 'Implementation spec for task 002.'
---

# Task 002: Authentication and API tokens

See [ADR-005](../../docs/adr/ADR-005-auth-keycloak.md).

## Scope

- Keycloak dev realm export committed; clients: `web` (public, PKCE), `cli` (device grant).
- API: JWT bearer + API-token auth handler; user provisioned on first login.
- API tokens: create/list/revoke; hashed; `fs_pat_` / `fs_agent_` prefixes; agent name.
- SPA login/logout.

## Acceptance criteria

- [x] Web login works end-to-end locally.
- [x] `GET /api/v1/me` with `Authorization: Bearer fs_pat_...` works.
- [x] Revoked token rejected immediately.

## Implementation notes

- `Authorization: Bearer` carries either credential; a policy scheme routes `fs_...` to the API-token handler, the
  rest to Keycloak JwtBearer. JWT users are upserted on first request (cached 5 min); new users get the browser's
  language when Keycloak has no locale.
- Tokens are user-scoped for now; task 003 adds workspace scoping. Agent tokens can't manage tokens (`human` policy).
- Dev realm: SPA pinned to `http://localhost:5180` (exact redirect URI/web origin), Keycloak on 8180, users
  `dev@flowstate.local`/`dev` and `other@flowstate.local`/`other`. Prod realm URIs are task 013.
- Errors: Problem Details with `type: urn:flowstate:error:<code>` and a `code` extension.
