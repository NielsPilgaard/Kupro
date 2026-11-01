---
name: ci-failure
description: "Diagnose and fix a failed GitHub Actions run for Kupro: CI (.NET, web, site, Grafana drift, CLI AOT, image publish), PR E2E, Staging (E2E against published images) or the production deploy (CD). USE THIS SKILL when the user says 'staging failed', 'CI failed', 'the build is red', 'deploy failed', 'e2e failed on the PR', 'fix the pipeline', or pastes a GitHub Actions run link."
---

# CI failure

Find the failing step, get the evidence, find the commit that broke it, then fix the cause.
Use `gh` for everything on GitHub. Don't guess from the workflow file; read the run.

## Pipeline map

| Workflow | Trigger | What fails there |
|---|---|---|
| `ci.yml` | PR + push to main | `.NET build, test, drift`, `Web lint, typecheck, test, build`, `Site typecheck and build`, `Grafana dashboards drift`, `CLI AOT (<rid>)`, `E2E` (PRs only), `Publish * image` (main only) |
| `e2e.yml` | called by CI (PR, builds images) and Staging (pulls `sha-<commit>` images) | Playwright in `tests/e2e` against `deploy/docker-compose.yaml` + `tests/e2e/compose.yaml` |
| `staging.yml` | CI succeeded on main | `E2E`, then `Deploy` (skipped until `vars.APP_URL` is set) |
| `cd.yml` | called by Staging, or `workflow_dispatch` for rollbacks | image check, `scripts/deploy.mjs` (Dokploy), wait for `/api/v1/status` to report the new version |
| `cla.yml`, `dependency-review.yml` | PR | unsigned CLA, vulnerable or disallowed dependency |

A Staging run named "skipped" means CI on that commit didn't succeed. Look at the CI run instead.

**Drift steps** fail when generated files weren't committed. The fix is to regenerate and commit, not to edit by hand:

| Step | Regenerate with |
|---|---|
| OpenAPI spec drift | `dotnet build Kupro.slnx -c Release`, commit `openapi/` |
| Compose drift | `dotnet aspire publish -o deploy --non-interactive`, commit `deploy/docker-compose.yaml` |
| Grafana dashboards drift | `python3 infrastructure/observability/build_dashboards.py`, commit `infrastructure/observability/` |

## Step 1: Find the failed run and step

```bash
gh run list --limit 15
gh run view <run-id> --log-failed | tail -120
```

For a run link the user pasted, the run id is the number after `/runs/`.

## Step 2: Get the evidence

**Playwright failure** (E2E job). The log shows the assertion, but download the artifacts too:

```bash
gh run download <run-id> -n playwright-report -D <scratch-dir>/pw   # traces + error-context.md
gh run download <run-id> -n stack-logs -D <scratch-dir>/stack        # postgres/migrations/api/keycloak/web logs, failure only
```

- `test-results/<test>/error-context.md` has the page's accessibility snapshot at the moment of failure. Read it first.
- `trace.zip` can be opened with `bunx playwright show-trace <path>` from `tests/e2e`.
- `stack-logs/api.log` has server-side exceptions; `migrations.log` covers failed EF migrations. The job log also prints the last 200 API lines under "API log".

**Latency budget failure** (`.NET build, test, drift` → Test). The ADR-010 perf report is in the run's step summary: `gh run view <run-id>` or the run page.

**Production deploy or runtime failure.** Prod logs go to the self-hosted Loki (ADR-014), datasource uid `loki`, stream `{service_namespace="kupro", service_name="api"}` (`{service_namespace="kupro"}` includes migrations). Query with the Grafana MCP `query_loki_logs` tool around the deploy time, if it points at Kupro's Grafana. Staging logs are not in Grafana; the stack only lives on the runner.

## Step 3: Find what changed

Compare the failing commit with the last green run of the same workflow:

```bash
gh run view <run-id> --json workflowName,headSha,displayTitle   # the failing run's workflow, commit and title
gh run list --workflow="<workflowName>" --limit 10
git diff --stat <last-green-sha> <failing-sha>
git log --oneline <last-green-sha>..<failing-sha>
```

For Staging, `headSha` is main's head when the run started, not the commit it tested. The tested commit is the SHA at
the end of `displayTitle` (the CI run's `head_sha`); if the title is cut off, take `headSha` from the triggering CI
run instead.

A test added or changed in that range that fails on first run is usually a wrong test, not a regression. Read the code it exercises before changing either.

## Step 4: Reproduce locally

For e2e, start the stack with `aspire run` (SPA on :5180, Keycloak on :8180), then run only the failing spec:

```bash
cd tests/e2e && bun install && bunx playwright install chromium && bunx playwright test tests/<spec>.ts --reporter=line
```

For .NET, web or docs failures, run `/verify` (or a single test class with `dotnet test --filter`).

## Step 5: Fix and report

- Fix the cause, then rerun the reproduction.
- Run `/verify` before committing code changes.
- Don't push to main, rerun workflows or dispatch CD without asking. `gh run rerun <run-id> --failed` is fine to suggest for flakes.
- Report: failing job/step, root cause, the commit that introduced it, the fix, and how you verified it.
