---
title: 'Task 001: Repo scaffold, dev environment, CI'
description: >-
  Create solution layout, React app, Aspire AppHost dev stack (Postgres, Keycloak), OpenAPI → TS codegen, editorconfig and GitHub Actions CI.
status: 'Done'
phase: 0
depends_on: []
purpose: 'Implementation spec for task 001.'
---

# Task 001: Repo scaffold, dev environment, CI

## Scope

- .NET 10 solution: `src/Kupro.AppHost`, `src/Kupro.ServiceDefaults`, `src/Kupro.Api`, `src/Kupro.Cli`, `tests/Kupro.Tests` (xUnit + Testcontainers).
- `web/`: Vite + React + TS + TanStack Query/Router, Bun as toolchain ([ADR-003](../../docs/adr/ADR-003-frontend-stack.md)).
- Aspire AppHost ([ADR-012](../../docs/adr/ADR-012-aspire.md)): postgres 18, keycloak (dev realm import), api, web (Vite).
- `aspire publish` → `deploy/docker-compose.yaml` committed.
- OpenAPI emitted to `openapi/kupro.json` at build; `@hey-api/openapi-ts` generates `web/src/api/gen/` in `predev`/`prebuild` ([ADR-003](../../docs/adr/ADR-003-frontend-stack.md) IMP-003).
- `web/Dockerfile` + `web/nginx.conf` ([ADR-012](../../docs/adr/ADR-012-aspire.md) IMP-002), roughly:

  ```dockerfile
  # pin tag to the version in package.json "packageManager"
  FROM oven/bun:1-alpine AS build
  WORKDIR /app/web

  COPY web/package.json web/bun.lock ./
  RUN bun install --frozen-lockfile

  # hey-api codegen reads from ../openapi/ relative to web/
  COPY openapi/ ../openapi/
  COPY web/ .
  ARG VITE_KEYCLOAK_URL=http://localhost:8080
  ENV VITE_KEYCLOAK_URL=$VITE_KEYCLOAK_URL
  RUN bun run build

  FROM nginx:alpine AS runtime
  COPY --from=build /app/web/dist /usr/share/nginx/html
  COPY web/nginx.conf /etc/nginx/conf.d/default.conf
  EXPOSE 80
  ```
- `.editorconfig`, `Directory.Build.props` (nullable, warnings as errors, analyzers).
- CI: build + test (.NET), lint + typecheck + test (web), drift checks for `openapi/` spec and `deploy/` compose.

## Acceptance criteria

- [x] `aspire run` gives a running API (`/health`), SPA and Aspire dashboard on localhost.
- [x] `docker compose -f deploy/docker-compose.yaml up` (generated) also starts the full stack.
- [x] SPA calls `/health` (or a sample endpoint) through the generated client.
- [x] CI green on an empty feature set.
- [x] README has a 3-step quickstart.

## Implementation notes

- Compose file uses `${API_IMAGE}` / `${WEB_IMAGE}` / `${MIGRATIONS_IMAGE}`: `aspire deploy` builds the images, fills
  the env file and runs `docker compose up` (verified locally). Image registry/build in prod is task 013.
- Keycloak realm is bind-mounted from the AppHost dir (`KEYCLOAK_BINDMOUNT_0`); task 013 should bake it into an image.
- Migrations run as a separate `migrations` resource (API image with `migrate` arg) before `api` starts.
- Keycloak dev port is 8180 (8080 often taken by other local stacks).
