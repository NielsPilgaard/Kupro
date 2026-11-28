---
title: 'Task 039: Safe delivery — merge queue, full parallel E2E, automatic rollback'
description: >-
  Build ADR-023: merge queue, a flow list with E2E for every flow (web, CLI, MCP) running sharded and fully parallel in
  under five minutes, migration lint and the N-1 rollback test, and CD that smoke-tests, bakes and rolls back by itself.
status: 'Todo'
phase: 0
depends_on: ['001']
purpose: 'Implementation spec for task 039.'
---

# Task 039: Safe delivery — merge queue, full parallel E2E, automatic rollback

## TL;DR

- Decisions are in [ADR-023](../docs/adr/ADR-023-safe-delivery.md); this file is the build order and the checklist.
- Parts A to C work today, before production exists. Part D needs [task 013](013-deploy.md) live.
- Done when merging to main needs no further human step: the release lands, or it is rolled back and reverted.

## Part A — E2E: isolated, parallel, sharded

- [ ] E2E realm seeds `e2e-1` … `e2e-8` (password from `e2e.env`); `auth.setup.ts` signs each in once and writes
      `.auth/user-<n>.json`; a worker fixture picks the file by `workerIndex`.
- [ ] `signInToProject` creates a fresh project per test (unique name); per-worker workspace. Paging and screenshot
      specs seed data through the API.
- [ ] `fullyParallel: true`, workers from CPU count; the suite passes `--repeat-each=5` locally with no failures.
- [ ] `e2e.yml` split into a build job (`docker save` of api, keycloak, postgres, web as an artifact) and a 4-way shard
      matrix, each with its own compose stack; blob reports merged into one HTML report.
- [ ] Wall clock from E2E start to report ≤ 5 minutes on a cached run (measure and record here).
- [ ] Retry-only passes listed in the job summary; `@quarantine` tag runs but does not block.

## Part B — Every flow covered

- [ ] `tests/e2e/flows.ts` lists the flows; `bun run flows:check` fails when a flow has no `@flow:<id>` test. Runs in CI.
- [ ] Specs for at least these flows (existing ones tagged, the rest new):

| Area | Flows |
|---|---|
| Account | sign in, sign out, onboarding (create workspace), switch language EN/DA |
| Projects | create, rename, archive |
| Issues | create (dialog, `C`), edit fields, change state, board drag, sub-issues, relations, delete |
| Views | list, board, paging, filters, command palette, search |
| Collaboration | comments, attachments, activity feed, realtime (two browser contexts) |
| Planning | sprint plan, sprint close with split carry-over, burndown, delivery plan |
| Members | invite, role change, billing access, guest |
| Time | log time, week timesheet, close-with-time, Team time export |
| Data | workspace export, CSV import, snapshot import |
| Meeting | meeting agent flow (as today) |
| Agents | API token create/revoke; CLI sign-in with token, issue create/list/update; MCP list tools, create and update an issue |

- [ ] `@smoke` on the flows that must work in production: sign in, open project, create and edit an issue, board,
      search, log time, CLI and MCP issue create.

## Part C — Rollback-compatible releases

- [ ] Migration lint (a test in `Kupro.Tests` that reads the migrations): drop/rename table or column, or nullable →
      non-null without default, fails unless the migration has `// contract: <PR>`. Documented in CONTRIBUTING.md.
- [ ] N-1 shard: migrations from this commit, then API and web of `LAST_GOOD_TAG`, then `@smoke`. Skipped while
      `LAST_GOOD_TAG` is unset.
- [ ] Merge queue: branch protection on main, CI and E2E required, both triggered on `merge_group` (a manual step for
      the protection settings).

## Part D — CD with automatic rollback (needs 013)

- [ ] `cd.yml`: read `LAST_GOOD_TAG`, deploy, wait for version, smoke (`KUPRO_SMOKE_*` secrets), bake (10 min, thresholds in
      `infrastructure/observability/deploy-gates.yaml`), promote by setting `LAST_GOOD_TAG`.
- [ ] Rollback step on failure: `deploy.mjs` with `LAST_GOOD_TAG` (Keycloak and Postgres images kept if their version
      changed), wait for version, smoke, fail the run.
- [ ] Revert PR for the commits since `LAST_GOOD_TAG` with auto-merge, and an issue with the failed step and Grafana links.
- [ ] Rollback failure fires a critical Grafana alert.
- [ ] Synthetic smoke user and workspace in the production realm, flagged internal (no seats billed, out of metrics).
- [ ] Drill: merge a deliberately broken commit (a 500 on one endpoint) and watch it roll back and revert by itself;
      record the time to rollback here.
- [ ] [Deploy runbook](../docs/runbooks/deploy.md) updated: new secrets and variables, rollback behaviour, the drill.

## Manual steps

Branch protection and merge queue settings; a Grafana service account token (`GRAFANA_DEPLOY_TOKEN`); a token that can
write the `production` environment variable and open PRs; the smoke user's credentials. Each goes in the deploy runbook.
