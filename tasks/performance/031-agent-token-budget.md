---
title: 'Task 031: Agent token budget'
description: >-
  Cut what Kupro costs agents in tokens: a summary plan by default, compact issue rows, one get_context call instead of
  five, multi-key get_issue, unescaped Danish letters, and size limits in tests so it stays that way.
status: 'Todo'
phase: 1
depends_on: []
purpose: 'Implementation spec for task 031.'
---

# Task 031: Agent token budget

Tool design: [ADR-001](../../docs/adr/ADR-001-agent-parity.md).

## TL;DR

Measured on 2026-11-03 with 10,000 issues, 1,000 of them dated epics. Tokens are bytes ÷ 4, a rough estimate.

| Call | Today | Target |
|---|---|---|
| `get_plan` | 227 KB, about 57,000 tokens | Under 10 KB by default |
| `search_issues`, default | 50 rows, 17.1 KB, about 4,300 tokens | 25 rows, under 4 KB |
| Getting oriented | Up to 5 calls | 1 call |
| Reading 5 issues with history | 10 calls | 1 call |
| `tools/list` | 22.4 KB, about 5,600 tokens | Under 19 KB |

Kupro isn't live, so tools can change or go without a deprecation period.

## 1. Summary plan by default

The tool description's main use, "do we need to hire for Q2 2027?", needs monthly demand, supply and gap, not every card.

- Add a `view` to `PlanService.GetPlanAsync` and `GET plan`: `full` (default on REST, so the web client is unchanged) and
  `summary` (no cards; health flags as counts).
- `get_plan` defaults to `summary`, with `view=full`. Its description says when to ask for it and to narrow by `project`.
- `kupro` gets the same option.
- As built: the summary has per-project months only when `project` is set, so its size doesn't grow with the number of
  projects, and `violatedDependencies` replaces the dependency rows.

## 2. Compact issue rows

Today's rows repeat two UUIDs, `rank` and two timestamps. Rows with key, title, state, priority and assignee name cost a
third of today's size (5.6 KB against 17.1 KB for 50).

- MCP rows default to `key`, `type`, `title`, `state`, `priority`, assignee name, `effort`, `parentKey`, `labels`.
- Add `fields` to `search_issues` for the rest. Default `limit` becomes 25.
- `create_issues` and `update_issues` return the same compact rows. A bulk update of 100 must not echo 100 full rows.
- Assigning still needs a member id, which `get_context` provides.
- Shaping is view logic: it lives in one mapping shared by the MCP tools, and the feature services don't change.
- Add `--fields` to `kupro issue list`, since agents using the CLI pay for tokens too.

## 3. `get_context`

- Replaces `get_me`, `list_workspaces`, `list_projects`, `list_members` and `list_labels`: 1,626 bytes of definitions and
  up to 5 model turns.
- Returns who you are, workspaces (with role), and for the resolved workspace: projects, members with ids, labels by
  name and active sprints.
- `include` limits it. Cap members and labels, and say so in the response when capped, so a large workspace stays small.
- Move the `[CoversOperations]` entries to it, so the parity test still passes.
- Rewrite `McpSetup.Instructions` to start with `get_context`.
- As built: it also returns `myIssues`, your open issues as compact rows, capped at 25. Without them "what's on my plate?"
  takes a third call. An agent token gets none: agents can't be assignees ([ADR-008](../../docs/adr/ADR-008-work-item-model.md)).

## 4. Issues in one call

- `get_issue` takes `keys` (up to 20) and `include: activity`, and replaces `get_issue_activity`.
- Fetch keys in batches. A loop over `GetAsync` is 5 queries per key plus 8–10 for activity.
- A key that doesn't exist gets its own error entry and doesn't fail the call.

## 5. Encoding and definitions

- MCP output uses an escaping encoder: timestamps carry `+` for `+`. Check whether æ, ø and å are also escaped, which
  would make Danish text cost more than English. If so, set `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` on the MCP
  JSON options: the output is JSON in a text block, not HTML.
- `get_sprint_review` and `get_sprint_burndown` (1,535 bytes), `relate_issues` and `unrelate_issues` (1,146 bytes): merge
  only if the schema stays clear.
- Don't merge write tools further: `manage_sprint` and `meeting` are already the largest, and an `invalid_arguments`
  retry costs a whole model turn.
- As built: no merges were needed. Stripping `"default": null` and `null` types from the schemas brought the list under
  the limit.

## Acceptance criteria

- [x] `McpTests` asserts byte limits on the reference dataset for `tools/list`, default `get_plan`, default
  `search_issues`, `update_issues` with 100 rows, and `get_context`.
- [x] Tool count stays within the ADR-001 limit, and the limit is lowered to the new count.
- [x] `get_context` plus a multi-key `get_issue` answers "what's on my plate?" with 2 tool calls in an MCP test.
- [x] Parity test passes. REST and the web client are unchanged except for the new `view` option.
- [x] Danish titles come through unescaped in a test, if the check in part 5 finds escaping.
- [x] [ADR-001](../../docs/adr/ADR-001-agent-parity.md) IMP-003 records the byte limits and the new tool list. The agent
  skill and docs from [ADR-018](../../docs/adr/ADR-018-cli-distribution.md) are updated wherever they name a removed tool.

## Out of scope

The meeting agent's own LLM spend ([ADR-015](../../docs/adr/ADR-015-meeting-agent-vendors.md)), which is a separate cost.
