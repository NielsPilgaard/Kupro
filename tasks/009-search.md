---
title: 'Task 009: Search'
description: >-
  Postgres full-text search (English + Danish) and trigram fuzzy match over issues, exposed in API, CLI, MCP and
  command palette.
status: 'Todo'
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

- [ ] Search on 10k issues < 100 ms p99.
- [ ] Typo-tolerant title match.
