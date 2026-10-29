---
title: 'Task 025: Import from Azure DevOps (Services and Server)'
description: >-
  Azure DevOps Boards connector for the import framework: Services and Server, per-process default type and state
  mappings, four-level hierarchy folding, iterations as sprints, area paths as labels, comments, attachments and
  work item revisions as changelog.
status: 'Todo'
phase: 1
depends_on: [023]
purpose: 'Implementation spec for task 025.'
---

# Task 025: Import from Azure DevOps (Services and Server)

## TL;DR

Decisions are in [ADR-019](../docs/adr/ADR-019-import.md); the pipeline is [task 023](023-import-framework.md). This
task adds `AzureDevOpsConnector` for Boards on Azure DevOps Services and Server, and default mappings per process
(Basic, Agile, Scrum, CMMI and inherited ones). The hard part is the four-level hierarchy.

## Scope

### Access

- **Services**: `https://dev.azure.com/<org>` (and legacy `https://<org>.visualstudio.com`); **Server**:
  `https://<server>/<collection>`. PAT with **Work Items (Read)**, Basic auth with an empty user.
- API version: 7.1 on Services. On Server, probe and fall back to the highest the server supports (6.0 for Server 2020).
- `kupro import azure-devops --url <collection> --local` for on-prem servers.

### Reading

- Describe: projects, the project's process and its work item types, states per type with **state category**
  (Proposed, InProgress, Resolved, Completed, Removed), fields, area and iteration trees
  (`wit/classificationnodes?$depth=…`), identities seen on the selected items.
- Items: WIQL returns IDs only and at most 20,000 per query, so the connector queries in `System.Id` ranges, then
  `wit/workitemsbatch` (200 per call) with fields and relations. Delta: `[System.ChangedDate] >= '<cursor>'`.
- Hierarchy: `System.LinkTypes.Hierarchy-Reverse` (parent).
- Changelog: `wit/workItems/{id}/updates`; change ID = update ID. Field diffs and relation changes both replay.
- Comments: `wit/workItems/{id}/comments` (HTML). Attachments: `AttachedFile` relations, downloaded with the PAT.
- Iterations: classification nodes with `startDate`/`finishDate` → sprints (per Kupro project). Undated iterations →
  label option. Area paths → label option (leaf name, or full path with `/`).
- Throttling: honour `Retry-After` and `X-RateLimit-*` headers (TSTU-based).

### Markup

- ADO fields are HTML: `System.Description`, `Microsoft.VSTS.Common.AcceptanceCriteria`, `Microsoft.VSTS.TCM.ReproSteps`.
  Converted with task 023's HTML-to-Markdown; acceptance criteria and repro steps become `## Acceptance criteria` /
  `## Repro steps` sections in the description.
- Mentions (`<a data-vss-mention>`) → People map; `#1234` references and work item URLs → Kupro keys.

### Default mappings

- **Types per process**:
  - Basic: Epic → Epic, Issue → Feature, Task → Task (fits exactly).
  - Agile: Epic → Epic, Feature → Feature, User Story → Task, Task → Task, Bug → Task + `bug`, Issue → Task.
  - Scrum: as Agile with Product Backlog Item → Task, Impediment → Task + `impediment`.
  - CMMI: as Agile with Requirement → Task, Change Request/Risk/Review → Task + label of the same name.
  - Inherited processes: start from the parent process's map; custom types default to Task.
  - Test Plan, Test Suite, Test Case, Shared Steps, Shared Parameter: unticked.
- **Fold** (four levels): default keeps Epic and Feature, maps Story-level to Task, and puts Task-level items as Tasks
  under the Feature with a `relates` relation to their Story. The owner can instead fold Epics (Epic → label, Feature
  → Epic, Story → Feature, Task → Task). The plan shows both counts so the choice is informed.
- **States**: Proposed → Todo (state "New" with no iteration → Backlog), InProgress → In Progress (name contains
  "review", "test", "resolved" → In Review), Resolved → In Review, Completed → Done, Removed → Canceled.
- **Priority**: 1 → High, 2 → Medium, 3 and 4 → Low. Bug `Severity` "1 - Critical" → Urgent.
- **Effort**: `Microsoft.VSTS.Scheduling.OriginalEstimate` (hours), else `RemainingWork + CompletedWork`, else
  `StoryPoints` / `Effort` / `Size` × factor.
- **Dates**: `StartDate` → start; `TargetDate`, else `FinishDate`, else `DueDate` → due.
- **Labels**: `System.Tags` (semicolon-separated) → labels.
- **Relations**: Dependency (Predecessor/Successor) → blocks, Related → relates, Duplicate → duplicates. Links to
  commits, PRs and builds go into the details block as links.
- **Keys**: ADO ID kept as the number under the project key the owner picks (`#4567` → `WEB-4567`), with `#4567`
  and the work item URL as aliases (ADR-019 Keys).

### Site

- `kupro.app/switch/azure-devops` (EN/DA): what comes across, how the four levels fold, the three steps.

## Acceptance criteria

- [ ] Recorded fixtures from a Services org with one project per built-in process import with no errors; plan and
      commit agree.
- [ ] A project with 25,000 work items imports completely (WIQL ID-range chunking; fixture or generated).
- [ ] Both fold options produce valid ADR-008 shapes; the plan's counts match the result.
- [ ] HTML descriptions, acceptance criteria and repro steps convert; mentions and `#id` references are rewritten.
- [ ] Revisions replay: state, assignee, iteration and priority history as Kupro updates; other fields as source changes.
- [ ] `WEB-4567`, `#4567` and the ADO URL all open the same issue.
- [ ] Server API-version fallback is covered by a fixture recorded at 6.0.
- [ ] One live run against the Services sandbox before marking Done, noted in the PR.

## Out of scope

- Repos, Pipelines, Test Plans, Wiki.
- Delivery Plans (ADO's) and team capacity: Kupro's capacity is per project ([ADR-008](../docs/adr/ADR-008-work-item-model.md)); owners set it after import.
- Microsoft Entra OAuth "Connect" button.
