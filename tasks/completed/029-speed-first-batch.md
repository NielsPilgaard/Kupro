---
title: 'Task 029: Speed, first batch'
description: >-
  Speed changes: a real ttlMs on the MCP tool list, an issue panel that opens from the cached list row and precompressed
  web assets. Npgsql auto-prepare was measured and left off.
status: 'Done'
phase: 1
depends_on: []
purpose: 'Implementation spec for task 029.'
---

# Task 029: Speed, first batch

Budget and rules: [ADR-010](../../docs/adr/ADR-010-performance-budget.md). The parts are independent; ship them as
separate commits.

## TL;DR

| Part | Today | Target |
|---|---|---|
| MCP `tools/list` | `ttlMs: 0`, 22.4 KB | `ttlMs` of one hour, about 1 KB smaller, order pinned by a test |
| Issue panel | Empty until the detail response arrives | Shows the row's fields at once |
| Main JS, compressed | About 346 KB (gzip level 1) | About 289 KB (gzip level 9, precompressed) |
| Npgsql auto-prepare | Off | Off: measured, and worse for large tenants |

## 1. Cacheable tool list

The server sends `ttlMs: 0` and `cacheScope: private`, so clients that honour the
[2026-07-28 caching fields](https://modelcontextprotocol.io/specification/2026-07-28/changelog) treat the list as stale
at once.

- Add a filter in `McpSetup.AddKuproMcp` (`WithRequestFilters` + `AddListToolsFilter`) that sets
  `ListToolsResult.TimeToLive` to one hour. Keep `cacheScope` private: the endpoint requires authentication.
- The workspace help in `KuproTools` (`WorkspaceHelp`) is about 2 KB of the list, repeated in 22 tools. Shorten it and
  move the explanation to the server instructions. Check the 2025-11-25 protocol still works: the field is 2026-07-28 only.

### Acceptance criteria

- [x] A raw `tools/list` on protocol 2026-07-28 returns `ttlMs: 3600000`.
- [x] `McpTests` asserts the tool order is the same across calls and the list stays under 22 KB.
- [x] [ADR-001](../../docs/adr/ADR-001-agent-parity.md) records the TTL under Implementation Notes.

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

- [x] With the detail response delayed, opening a row shows title, state, priority, assignee, effort and labels at once.
- [x] The placeholder never shows false empty states, and the description editor can't save over an unloaded description.
- [x] Opening a key that is in no cached list (a direct link) behaves as today.
- [x] Web test or Playwright test with a delayed `getIssue` covers the above.

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

- [x] `curl -H 'Accept-Encoding: gzip'` against the built image returns the JS at about 289 KB with `Vary: Accept-Encoding`
  and the existing `immutable` header.
- [x] A client without gzip support still gets the uncompressed file.
- [x] [ADR-012](../../docs/adr/ADR-012-aspire.md) IMP-002 mentions the precompression if its description of the image changes.

## 4. Npgsql auto-prepare: measured, not shipped

The first version of this part asked for auto-prepare, from numbers taken on tables without planner stats. On analyzed
tables the gain is small, and with tenants of different sizes single-issue reads and writes get slower. The numbers and
the reason are in [ADR-010](../../docs/adr/ADR-010-performance-budget.md) IMP-001.

### Acceptance criteria

- [x] [ADR-010](../../docs/adr/ADR-010-performance-budget.md) records the measurements and why the setting stays off.
- [x] `PerfBudgetTests` runs `ANALYZE` after seeding, so it measures the plans production gets.

## Out of scope

Compact MCP responses, `get_context`, route loaders and bundle splitting, persisted client cache, realtime debouncing,
ETags and a server data cache. Each needs its own task.
