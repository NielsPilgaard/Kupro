---
title: 'ADR-023: Safe delivery — merge queue, parallel E2E, rollback-compatible releases, automatic rollback'
status: 'Proposed'
date: '2026-11-28'
authors: 'Niels'
tags: ['delivery', 'ci', 'cd', 'testing', 'operations']
supersedes: ''
superseded_by: ''
description: >-
  A merge to main either lands in production and stays healthy, or is rolled back to the last good release with no
  person involved. The merge queue tests the exact merged commit, E2E covers every listed flow in parallel in under five
  minutes, CI proves the last good release still runs on the new schema, and CD deploys, smoke-tests, watches
  production for ten minutes and rolls back on any failure.
---

# ADR-023: Safe delivery — merge queue, parallel E2E, rollback-compatible releases, automatic rollback

## TL;DR

- **Gate**: GitHub merge queue; CI and E2E run on the merged commit, so main only ever holds tested commits.
- **E2E**: every flow in `tests/e2e/flows.ts` has a spec (CI fails otherwise), across web, CLI and MCP. Tests isolate by user and workspace, so they run fully parallel, sharded over 4 runners: under 5 minutes.
- **Rollback is tested before merge**: CI runs the last good release against the new schema. Migrations follow expand/contract; a lint blocks destructive ones.
- **CD**: deploy, check the version, smoke-test production, then watch error rate and latency for 10 minutes. Any failure redeploys `LAST_GOOD_TAG`, checks it, and opens a revert PR that merges by itself once its checks pass.

## Status

**Proposed**

## Context

Today a push to main runs CI, then E2E against the published images (Staging), then CD: Dokploy redeploys and CD waits
until `/api/v1/status` reports the new commit. Nothing happens if the release then fails, apart from Grafana alerts,
and the runbook's rollback is a manual CD run with an older tag. That rollback is only safe if the old code runs on the
new schema, which nothing checks.

E2E runs on one runner with one worker, because every test signs in as the same dev user and shares one workspace. The
suite has seven specs, and many flows (search, comments, import, export, delivery plan, CLI, MCP) have none. The owner wants to merge and walk away: the feature lands safely or is rolled back, and the
whole E2E run may take 3 to 5 minutes with everything in parallel.

Constraints: a single VPS with Dokploy and Docker Compose ([ADR-004](ADR-004-hosting.md)), forward-only EF migrations
in a separate `migrations` service ([ADR-002](ADR-002-backend-stack.md)), the self-hosted Grafana stack
([ADR-014](ADR-014-observability.md)), no new vendors ([ADR-006](ADR-006-eu-vendor-policy.md)).

## Decision

### 1. Merge gate

Main is protected: no direct pushes, merges only through the GitHub merge queue. CI and E2E are required checks and also
run on `merge_group`, so the commit that reaches main is the one that was tested, not a PR branch that was green
against an older main. Staging still runs E2E against the published images, which catches image and publish problems.

### 2. E2E that covers every flow, in parallel

- **Flow list**: `tests/e2e/flows.ts` names every user-facing flow (web) and agent flow (CLI, MCP). Each spec tags the
  flows it covers (`test(..., { tag: ['@flow:issue-create'] })`). A check in CI fails when a listed flow has no test,
  so adding a feature means adding its flow and its test. Flows that matter most are also tagged `@smoke`.
- **Isolation**: the E2E realm seeds one user per Playwright worker (`e2e-1` … `e2e-N`); each worker signs in once and
  each test creates its own workspace or project with a unique name. No test reads data another test wrote, so
  `fullyParallel: true` is safe. Tests that need a fixed state (paging, screenshots) seed it through the API, not the UI.
- **Sharding**: one job builds the images once (`docker save`, uploaded as an artifact, so fork PRs work without
  registry access); 4 shard jobs each start their own compose stack and run `--shard=i/4` with several workers.
  The reports are merged into one HTML report. Budget: about 2 minutes to build (cached) and 3 to run.
- **Agent surfaces**: the CLI (the AOT binary CI already builds) and MCP (an MCP client in the test runner) run their
  flows against the same stack, so parity ([ADR-001](ADR-001-agent-parity.md)) is tested end to end, not only in
  integration tests.
- **Flakes**: one retry in CI. A test that passes only on retry is reported in the job summary; flaky tests are fixed
  or quarantined (`@quarantine`, still run, not blocking) within a week, never silently retried forever.

### 3. Every release can be rolled back

A rollback swaps images, not the database, so the previous release must run on the new schema.

- **Expand/contract**: a release only adds (tables, nullable columns, columns with defaults, indexes). Removing or
  renaming happens in a later release, once no deployed code uses the old shape. Data backfills run in the
  `migrations` service and must be safe to leave in place.
- **Migration lint**: CI fails when a migration drops or renames a table or column, or makes a column non-nullable
  without a default, unless the migration carries `// contract: <PR that stopped using it>`. The reviewer checks that
  the referenced release is already in production.
- **N-1 test**: in E2E, after the new migrations ran, one shard starts the API and web images of `LAST_GOOD_TAG` on that
  schema and runs the `@smoke` suite. If the old release breaks on the new schema, the PR cannot merge. This is the
  check that makes automatic rollback trustworthy.
- **Not image-reversible**: Keycloak major upgrades (Keycloak migrates its own tables) and Postgres image changes. A
  rollback keeps the new Keycloak and Postgres images and rolls back only `api`, `migrations`, `web` and `site`.
  A Postgres major upgrade is never an automatic deploy.

### 4. CD with automatic rollback

`LAST_GOOD_TAG` is a variable on the `production` environment: the last tag that passed every step below. CD:

1. **Deploy** the new tag through Dokploy (as today) and wait until `/api/v1/status` reports it.
2. **Smoke** production: the `@smoke` suite against `APP_URL`, signed in as a synthetic user in a synthetic workspace
   (`KUPRO_SMOKE_*` secrets). The workspace is flagged internal: no seats billed, left out of product metrics. Smoke
   tests clean up what they create.
3. **Bake** for 10 minutes: CD queries Prometheus through the Grafana API every 30 seconds and fails on any of: API 5xx
   rate above 1 % (at least 20 requests), p95 above the [ADR-010](ADR-010-performance-budget.md) budget, an API
   container restart, or a firing critical alert ([ADR-014](ADR-014-observability.md)). The thresholds live in one file
   next to the alert rules.
4. **Promote**: set `LAST_GOOD_TAG` to the new tag.

Any failure in steps 1 to 3 triggers the rollback in the same job: redeploy `LAST_GOOD_TAG` (with the exceptions in 3),
wait for its version, run the smoke suite again, and fail the run. CD then opens a revert PR for the commits between
`LAST_GOOD_TAG` and the failed tag, with auto-merge on, so it goes through the merge queue like any change. It also
opens an issue with the failed step, the job summary and links to the Grafana panels. If the rollback itself fails,
that is the one case that pages a person (a critical Grafana alert).

Deploys stay serial (`concurrency: production`, no cancel). A commit merged while a rollback is in progress deploys after
it; if it still contains the bad change it fails and rolls back too, which is noisy but safe, and the revert PR ends it.

## Consequences

### Positive

- **POS-001**: Merging is the last human step. A bad release costs at most the bake window plus one redeploy.
- **POS-002**: Rollback is tested on every PR (N-1), not discovered during an incident.
- **POS-003**: The flow list makes coverage visible and enforced; agent surfaces get real E2E coverage.
- **POS-004**: Parallel, isolated tests are faster and stop flaking from shared state.

### Negative

- **NEG-001**: Expand/contract makes renames and removals take two releases.
- **NEG-002**: Smoke tests write to production, in a synthetic workspace that must be kept out of billing and metrics.
- **NEG-003**: More runner minutes: 5 jobs per E2E run, on PRs, merge queue and Staging.
- **NEG-004**: Compose still recreates containers, so each deploy and rollback has a few seconds of downtime.
- **NEG-005**: CD needs a Grafana service account token, a secret that can read production metrics.

## Alternatives Considered

### Blue/green or canary on the VPS

- **ALT-001**: **Description**: run two API/web stacks behind Traefik and shift traffic after checks.
- **ALT-002**: **Rejection Reason**: on one VPS with Dokploy it doubles memory and needs custom Traefik routing; at
  our traffic a canary gives too few requests to judge. Revisit when downtime per deploy matters (it would remove
  NEG-004).

### Database rollback (restore or down migrations)

- **ALT-003**: **Description**: roll the schema back with EF down migrations or a point-in-time restore.
- **ALT-004**: **Rejection Reason**: both lose data written since the deploy, down migrations are rarely tested, and a
  restore is a person's decision ([ADR-016](ADR-016-postgres-backups.md)). Expand/contract makes it unnecessary.

### Rely on Grafana alerts only

- **ALT-005**: **Description**: deploy, and let alerts tell a person to roll back.
- **ALT-006**: **Rejection Reason**: needs a person, which is what this ADR removes.

### Deploy freeze after a rollback instead of a revert PR

- **ALT-007**: **Description**: stop deploying until a person clears a flag.
- **ALT-008**: **Rejection Reason**: needs a person, and blocks unrelated fixes. The revert PR is reviewable and
  goes through the same gate.

## Implementation Notes

- **IMP-001**: Built in [task 039](../../tasks/039-safe-delivery.md). The merge gate, E2E and migration parts work before
  production exists; smoke, bake and rollback need [task 013](../../tasks/013-deploy.md) live.
- **IMP-002**: `scripts/deploy.mjs` stays the one way to deploy a tag; the rollback calls it with `LAST_GOOD_TAG`.
  Setting the environment variable needs a token with `environments: write` (a GitHub App or fine-grained token).
- **IMP-003**: Until `LAST_GOOD_TAG` exists (first deploy), there is nothing to roll back to: CD fails without
  rollback, and the N-1 shard is skipped.
- **IMP-004**: The bake thresholds start loose and tighten once production traffic gives a baseline.

## Related Decisions

- [ADR-004](ADR-004-hosting.md) — the host and Dokploy deploy this builds on
- [ADR-002](ADR-002-backend-stack.md) — forward-only EF migrations in a separate step
- [ADR-010](ADR-010-performance-budget.md) — the latency budget the bake checks
- [ADR-014](ADR-014-observability.md) — the metrics and alerts the bake reads
- [ADR-016](ADR-016-postgres-backups.md) — restores stay a person's decision
- [ADR-001](ADR-001-agent-parity.md) — CLI and MCP flows in E2E

## References

- **REF-001**: GitHub merge queue — docs.github.com, "Managing a merge queue"
- **REF-002**: Playwright sharding and `merge-reports` — playwright.dev/docs/test-sharding
