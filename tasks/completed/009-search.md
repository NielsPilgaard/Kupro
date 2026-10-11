---
title: 'Task 009: Search'
description: >-
  Postgres full-text search (English + Danish) and trigram fuzzy match over issues, exposed in API, CLI, MCP and
  command palette.
status: 'Done'
phase: 1
depends_on: [004]
purpose: 'Implementation spec for task 009.'
---

# Task 009: Search

## Scope

- `tsvector` generated column over key/title/description; `pg_trgm` on title.
- Query syntax: free text + filters (`type:epic state:todo assignee:me sprint:current`).
- Same syntax in UI, CLI and MCP `search_issues`.

## Acceptance criteria

- [x] Search on 10k issues < 100 ms p99.
- [x] Typo-tolerant title match.

## Implementation notes

- `GET /issues?q=` (operation `listIssues`), CLI `flowstate issue search <words...>` and `issue list -q`, MCP
  `search_issues` (`query`; it also gained the missing `sprint` filter), and the Ctrl+K palette. One parser
  (`IssueSearch`) serves all of them; `q` filters AND with the explicit parameters. Unknown keys stay free text;
  bad values return `invalid_query`.
- Filters: `project type state assignee label sprint parent`; lists by comma or repetition; `sprint:current` =
  `active`; states accept `in-progress` / `in_progress`.
- Stored generated `tsvector` (key `simple`, title A + description B, each stemmed English and Danish) with a GIN
  index; query is `websearch_to_tsquery` English OR Danish. Typo tolerance: pg_trgm `word_similarity(q, title) >= 0.5`
  (pg_trgm's 0.6 default misses "dependancy"). An exact key match ranks first.
- New `relevance` sort, the default when there is free text; it pages by offset, other sorts keep keyset cursors.
- The GIN index is on `search_vector` alone (not led by `workspace_id`); the tenant filter still applies.
- 10k issues (CI budget test): p99 53 ms for a typo, 60 ms for a word in every title (ranks all 10k), 18 ms for text
  plus filters. If this grows, add a trigram GIN index and switch to the `<%` operator with a session threshold.
