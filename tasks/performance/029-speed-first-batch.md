---
title: 'Task 029: Speed, first batch'
description: >-
  Four small speed changes: a real ttlMs on the MCP tool list, an issue panel that opens from the cached list row,
  precompressed web assets, and Npgsql auto-prepare without slowing fuzzy search.
status: 'Todo'
phase: 1
depends_on: []
purpose: 'Implementation spec for task 029.'
---

# Task 029: Speed, first batch

Budget and rules: [ADR-010](../../docs/adr/ADR-010-performance-budget.md). The four parts are independent; ship them as
separate commits, and split the auto-prepare part into its own PR if the search fix takes long.

## TL;DR

| Part | Today | Target |
|---|---|---|
| MCP `tools/list` | `ttlMs: 0`, 22.4 KB | `ttlMs` of one hour, about 1 KB smaller, order pinned by a test |
| Issue panel | Empty until the detail response arrives | Shows the row's fields at once |
| Main JS, compressed | About 346 KB (gzip level 1) | About 289 KB (gzip level 9, precompressed) |
| `GET issues?limit=200` | 12–16 ms p50 | About 4 ms p50, fuzzy search no slower |

Numbers are from 2026-11-03: Release build, API in-process, Postgres in Docker on a laptop, 10,000 issues, one client.
They show relative cost, not production latency.

## 1. Cacheable tool list

The server sends `ttlMs: 0` and `cacheScope: private`, so clients that honour the
[2026-07-28 caching fields](https://modelcontextprotocol.io/specification/2026-07-28/changelog) treat the list as stale
at once.

- Add a filter in `McpSetup.AddKuproMcp` (`WithRequestFilters` + `AddListToolsFilter`) that sets
  `ListToolsResult.TimeToLive` to one hour. Keep `cacheScope` private: the endpoint requires authentication.
- The workspace help in `KuproTools` (`WorkspaceHelp`) is about 2 KB of the list, repeated in 22 tools. Shorten it and
  move the explanation to the server instructions. Check the 2025-11-25 protocol still works: the field is 2026-07-28 only.

### Acceptance criteria

- [ ] A raw `tools/list` on protocol 2026-07-28 returns `ttlMs: 3600000`.
- [ ] `McpTests` asserts the tool order is the same across calls and the list stays under 22 KB.
- [ ] [ADR-001](../../docs/adr/ADR-001-agent-parity.md) records the TTL under Implementation Notes.

## 2. Issue panel opens from the list row

`useIssue` ([web/src/api/issues.ts](../../web/src/api/issues.ts)) has no placeholder, so
[IssuePanel](../../web/src/features/issues/IssuePanel.tsx) has nothing to show until `getIssue` returns.

- Add `placeholderData` to `useIssue`: find the row in the cached issue lists (`isIssueList`, `listItems`) and build an
  `IssueDetail` with empty `children` and `relations`.
- `IssueSummary` has no `description`, `projectKey`, `createdBy`, `parent` details or children, and the panel reads them.
  Use `isPlaceholderData` to show a loading state for those parts. An empty placeholder must never read as real data:
  no "no description" or "0 subtasks", and no editing of description or relations until the real response arrives.
- Field edits made while the placeholder shows must still work or be disabled; check the optimistic path in `issues.ts`
  reads the detail cache, which is empty at that point.

### Acceptance criteria

- [ ] With the detail response delayed, opening a row shows title, state, priority, assignee, effort and labels at once.
- [ ] The placeholder never shows false empty states, and the description editor can't save over an unloaded description.
- [ ] Opening a key that is in no cached list (a direct link) behaves as today.
- [ ] Web test or Playwright test with a delayed `getIssue` covers the above.

## 3. Precompressed web assets

[web/nginx.conf](../../web/nginx.conf) compresses on the fly at nginx's default level 1.

| Main JS (983 KB raw) | Size |
|---|---|
| gzip 1, today | 346 KB |
| gzip 9 | 289 KB |
| Brotli 11 | 246 KB |

- In the build stage of [web/Dockerfile](../../web/Dockerfile), write a `.gz` at level 9 next to each JS, CSS and SVG file
  in `dist/`. Turn on `gzip_static` and `gzip_vary` in the `/assets/` location.
- `nginx:1.31-alpine` includes `gzip_static`. Its modules directory has no Brotli module, so Brotli means another base
  image or building the module. Leave it out unless that turns out to be a small change: a less-maintained image is a
  poor trade for 43 KB.
- Leave `gzip_comp_level` alone for proxied API responses: those are compressed per request.
- [site/nginx.conf](../../site/nginx.conf) is out of scope.

### Acceptance criteria

- [ ] `curl -H 'Accept-Encoding: gzip'` against the built image returns the JS at about 289 KB with `Vary: Accept-Encoding`
  and the existing `immutable` header.
- [ ] A client without gzip support still gets the uncompressed file.
- [ ] [ADR-012](../../docs/adr/ADR-012-aspire.md) IMP-002 mentions the precompression if its description of the image changes.

## 4. Npgsql auto-prepare

`Max Auto Prepare=64;Auto Prepare Min Usages=2` on the connection string, p50 ms, two runs each:

| Call | Off | On |
|---|---|---:|
| REST `issues?limit=200` | 12.1 / 15.8 | 3.9 / 4.1 |
| MCP `search_issues`, limit 50 | 11.7 / 14.2 | 4.6 / 4.5 |
| MCP `search_issues`, limit 200 | 15.8 / 17.7 | 8.5 / 8.4 |
| MCP `get_plan` | 33.1 / 36.7 | 21.1 / 19.1 |
| REST `issues/CORE-1` | 10.4 / 12.8 | 8.5 / 8.3 |
| MCP fuzzy search, all rows match | 53.1 / 57.3 | **93.0 / 92.0 (worse)** |

- Set both options in `DatabaseExtensions.AddKuproDatabase` with `NpgsqlConnectionStringBuilder`, so they apply however
  the connection string is provided. Keep any value an operator sets explicitly.
- Find out why fuzzy search slows down before choosing a fix. The likely cause is Postgres moving to a generic plan after
  five executions, but this is not confirmed; check with `EXPLAIN (ANALYZE)` on the prepared statement, parameters
  included. If that is the cause, candidates are `SET LOCAL plan_cache_mode = force_custom_plan` in a transaction around
  the `search.Text` path in `IssueService.ListAsync`, or restructuring the query so a generic plan works.
- Check deploys. Prepared statements on a running API can fail with `cached plan must not change result type` when a
  migration changes a table it reads. Test a migration that alters `issues` while the API serves requests, and document
  the result in the implementation notes.

### Acceptance criteria

- [ ] `PerfBudgetTests` passes, with `issues?limit=200` at least twice as fast as before on the same machine.
- [ ] `q=dependancy` and `q=issue` p50 are no slower than without auto-prepare, over at least two runs each.
- [ ] Migration behaviour is tested and written down; any error during a deploy has a handled path.
- [ ] [ADR-010](../../docs/adr/ADR-010-performance-budget.md) records the setting and the search exception.

## Out of scope

Compact MCP responses, `get_context`, route loaders and bundle splitting, persisted client cache, realtime debouncing,
ETags and a server data cache. Each needs its own task.
