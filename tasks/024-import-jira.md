---
title: 'Task 024: Import from Jira (Cloud and Data Center)'
description: >-
  Jira connector for the import framework: Cloud (REST v3, ADF) and Data Center/Server (REST v2, wiki markup), with
  default type, state, priority and field mappings, sprints, versions, links, comments, attachments and changelog.
status: 'In progress'
phase: 1
depends_on: [023]
purpose: 'Implementation spec for task 024.'
---

# Task 024: Import from Jira (Cloud and Data Center)

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md); the pipeline, wizard and re-runs are
[task 023](023-import-framework.md). This task adds `JiraConnector` (Cloud and Data Center share one connector with
two API dialects), two markup converters, and Jira's default mappings. Jira is the biggest switcher pool, and Data
Center ends on 28 March 2029.

## Progress

Markup is in: `AdfConverter` and `JiraWikiConverter` (`src/Kupro.Api/Features/Imports/`), registered in
`MarkupConverters`, with golden files in `tests/Kupro.Tests/Features/Golden/Jira/`.

The connector is in: `Kupro.Import/Jira` (`JiraConnector`, client, reading), registered in `ImportSources`, tested in
`JiraConnectorTests` against `FakeJira` (Cloud v3 and Data Center v2 response shapes) and end to end in
`ImportCommitTests.Jira`. Notes on what it does:

- The token field holds `email:api-token` on Cloud (Basic) and a PAT on Data Center (Bearer); Cloud with a bare token
  is refused as a rejected token.
- Delta JQL is relative (`updated >= "-Nm"`), so the API user's time zone doesn't matter, and the search runs in key
  order (not `updated`), so `startAt` paging neither skips nor repeats an issue that changes mid-read.
- The connector points images and files that name an attachment at its content URL in descriptions and comments.
- Changelogs and comments embedded in a search are paged in when cut short. An Epic Link key resolves through issues
  already read, else one lookup. The sprint field is read as objects or the old `Sprint@…[…]` string; a sprint without
  dates gets them from the Agile API.
- Worklogs come across as the issue's `timespent` (hours) for the details block; components and resolution are fields.
- Default mappings are in `ImportMappingDefaults.Jira`: fold `top`, New Feature → Task, Service Management request
  types skipped, backlog/QA/test state hints, components as labels, dated versions as milestones. App accounts carry
  `bot: true` (a new optional format property) and default to Skip.
- Resolution → Canceled per item: the mapping's `canceledValues` lists field values that turn a Done item Canceled.
  Describe reads the resolution names, and the Jira default lists those named for dropped work ("Won't Do",
  "Duplicate").
- Remote links (Confluence and web pages) come from `issue/{id}/remotelink`, one call per issue, into the item's links
  (the details block). Links that aren't http(s) are dropped.

- `kupro import jira --url <site> --local [--project KEY] [--import <id>] [--out file.zip]` runs the connector on the
  user's machine and uploads a snapshot with the attachment bytes in it, so the token never reaches Kupro. Without
  `--local` it is `import create jira`. The same command exists for azure-devops, linear and trello. Each `--local`
  run reads everything in scope (no delta cursor), and the upload replaces the import's file.

Not done: the People step's account type and the report's per-project summary (wizard), and the site page (waits on
the wizard). Recorded fixtures and the live
run need a Cloud sandbox and a Data Center instance (a manual step); until then the fake stands in for them.

## Scope

### Access

- **Cloud**: `https://<site>.atlassian.net`, Basic auth with email + API token. Wizard links to
  `id.atlassian.com/manage-profile/security/api-tokens`.
- **Data Center / Server**: base URL + personal access token (Bearer). Server (EOL since 2024) uses the same API.
- Dialect detected from `/rest/api/2/serverInfo` (`deploymentType`).
- `kupro import jira --url <base> --local` for instances behind a VPN (ADR-019 Source access).

### Reading

- Describe: projects (`project/search`), issue types per project (team-managed projects have their own), statuses
  with `statusCategory`, priorities, fields (`/field`, custom fields by schema type), users seen on the selected
  projects (assignees, reporters, commenters). Emails are often hidden by Cloud privacy settings; match by name then.
- Items: Cloud `/rest/api/3/search/jql` (token pagination, eventually consistent, so the 10-minute delta overlap);
  DC `/rest/api/2/search` (`startAt`). Delta: `updated >= "-Nm"` (relative to the cursor), in key order.
- Hierarchy: `parent` (Cloud, and sub-tasks everywhere); `Epic Link` custom field on DC and old Cloud data.
- Changelog: Cloud `/rest/api/3/issue/{id}/changelog` (paged); DC `expand=changelog`. Change ID = history ID + item index.
- Comments (paged), attachments (content URL with the same auth), issue links, worklogs (summed into the details block
  only; time tracking is out of v1 scope, [product.md](../docs/product.md)).
- Sprints: the sprint field (by schema `com.pyxis.greenhopper.jira:gh-sprint`) plus Agile API
  `/rest/agile/1.0/sprint/{id}` for dates and state.
- Versions: `/project/{id}/versions`; a version with `releaseDate` → milestone option.
- Rate limits: 429 with `Retry-After` on Cloud; back off on 503 on DC.

### Markup

- `AdfToMarkdown`: paragraph, heading, lists, task list, code block, blockquote, table, rule, hard break, marks
  (bold, italic, code, strike, link), mention (→ People map), inline/block card (→ Kupro key when it's a source
  issue), media (→ re-hosted attachment), panel (→ blockquote with label), expand (→ `<details>`), status, date,
  emoji. Anything else → fenced `json` block with the raw node.
- `JiraWikiToMarkdown`: `h1.`–`h6.`, `*bold*`, `_italic_`, `-strike-`, `{{mono}}`, `{code}`, `{noformat}`, `{quote}`,
  `bq.`, lists `*`/`#`, tables `||`/`|`, `[text|url]`, `[~user]` and `[~accountid:…]`, `!image.png!` and
  `!image.png|thumbnail!`, `{panel}`, `{color}` (dropped, text kept). Unknown macros → fenced block.
- Golden-file tests for both, from real-shaped samples incl. Danish text.

### Default mappings

- **Types**: Epic → Epic; Story, Task, Improvement, New Feature → Task; Bug → Task + `bug`; Sub-task → Task (fit
  promotes its parent to Feature). Initiative (Premium, a level above Epic) makes four levels, so the team picks
  `fold` ([ADR-019](../docs/adr/ADR-019-import.md) Shape): `top` (preselected) keeps Epics as Epics, and each
  Initiative comes across as a childless Epic related to its epics, which get a label with its name; `bottom` makes
  Initiatives Epics and Epics Features. Jira Service Management request types are listed but unticked.
- **States**: category `new` → Todo (name contains "backlog" → Backlog), `indeterminate` → In Progress (name contains
  "review", "QA", "test" → In Review), `done` → Done (resolution or name like "won't", "cancel", "duplicate",
  "rejected" → Canceled).
- **Priority**: Highest → Urgent, High → High, Medium → Medium, Low and Lowest → Low.
- **Effort**: `timeoriginalestimate` (seconds → hours), else story points (field found by name: "Story Points",
  "Story point estimate") × factor.
- **Dates**: `duedate` → due; a "Start date" / "Target start" field → start. Epics with no dates get them from
  children only in the plan view (no write).
- **Labels**: Jira labels → labels. Components → label option (ticked by default).
- **Relations**: Blocks → blocks, Duplicate → duplicates, Relates and Cloners → relates. Unknown link types → relates
  plus a details-block line.
- **Keys**: kept (`PROJ-123`). Jira keys can exceed Kupro's 10-character project key: the owner picks a shorter one
  and the old key becomes an alias.

### Wizard extras

- A "Jira" source card with Cloud / Data Center toggle and the token steps.
- People step shows Jira avatars' names and account type (app users and bots default to "skip").
- After the first run, the report offers a JQL-style summary: per project, items imported, re-parented, conflicts.

### Site

- `kupro.app/switch/jira` (EN/DA, [task 019](completed/019-public-site.md) structure): what comes across, what changes (the
  fixed workflow, no custom fields), the Data Center deadline, a three-step how-to.

## Acceptance criteria

- [ ] Recorded fixtures from a Cloud sandbox (company-managed and team-managed project) and a Data Center instance
      import with no errors; plan and commit agree.
- [x] Epic > Story > Sub-task arrives as Epic > Feature > Task; Initiative handling follows the owner's choice.
- [x] ADF and wiki golden files convert; unknown nodes appear as fenced blocks, never vanish.
- [x] Mentions, issue links in text and inline images are rewritten; images open from Kupro storage.
- [ ] Changelog: status, assignee, sprint and priority history shows as Kupro updates with original actor and time;
      custom-field history as source changes.
- [x] Delta re-run picks up an edit made in Jira after the first run, including one inside the overlap window, without
      duplicating anything.
- [x] A 429 with `Retry-After` pauses and resumes the run.
- [ ] One live run against the Cloud sandbox before marking Done, noted in the PR.

## Out of scope

- Jira Service Management queues, SLAs and customer portals.
- Jira Align, Confluence pages linked from issues (kept as links).
- Worklogs as entries (time tracking is not in v1).
- Atlassian OAuth 2.0 (3LO) "Connect" button.
