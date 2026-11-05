---
name: kupro
description: >-
  Work with Kupro, the issue tracker and planner: find, create and update issues and tasks, run sprints, read the
  delivery plan, review meeting proposals. Use when the user mentions Kupro, an issue key like KUP-12, their backlog,
  a sprint, or the plan.
---

# Kupro

Use the Kupro MCP tools when they are connected. When they are not, use the `kupro` CLI with `--json`; if it says you
are not signed in, ask the user to run `kupro login`.

## Start

Call `get_context` first. It returns who you are, your workspaces and, for the workspace, its projects, members, labels,
active sprints and your open issues. If you are in several workspaces, ask which one and pass its slug as `workspace`.
CLI: `kupro workspace list`, `kupro workspace use <slug>`.

## Issues

- Issues live in projects and are addressed by key, like `KUP-12`. Use keys when you talk to the user.
- Types: epic > feature > task. A task may sit directly under an epic.
- States: backlog, todo, inProgress, inReview, done, canceled.
- Effort is in hours.
- Descriptions and comments are Markdown.

Search before you create, so you don't add a duplicate: `search_issues` (CLI: `kupro issue search`). Read one with
`get_issue` (CLI: `kupro issue get`).

Create an epic and its children in one `create_issues` call, all-or-nothing. A child points at an earlier item in the
same call with `parent: "#0"`, where the number is that item's position. CLI: `kupro issue create-many --file`.

Change issues with `update_issues` (CLI: `kupro issue update`, `kupro issue update-many`, `kupro issue close`). Link them
with `relate_issues` (CLI: `kupro issue relate`). Comment with `comment` (CLI: `kupro issue comment`).

## Sprints and the plan

- `list_sprints` shows a project's sprints. `manage_sprint` creates, starts, closes and changes them. CLI: `kupro sprint list`,
  `kupro sprint create`, `kupro sprint start`, `kupro sprint close`.
- Before closing a sprint, read `get_sprint_review` (CLI: `kupro sprint review`) and agree with the user what happens to
  the unfinished work. `get_sprint_burndown` (CLI: `kupro sprint burndown`) shows progress.
- `get_plan` (CLI: `kupro plan`) compares demand with supply per month. `set_capacity` (CLI: `kupro capacity set`) and
  `manage_milestone` (CLI: `kupro milestone create`) change what it shows.

## Meeting proposals

The meeting agent turns what is said in a meeting into proposed issue changes, and people decide which ones apply. These
commands are CLI only. List them with `kupro meeting proposals`, apply with `kupro meeting approve`, and revert with
`kupro meeting undo`. Show the user what a proposal changes before you approve it.
