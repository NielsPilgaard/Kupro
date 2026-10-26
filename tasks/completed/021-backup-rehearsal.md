---
title: 'Task 021: Backup and restore rehearsal (local)'
description: >-
  Break the backup agent on purpose and run a full restore against the production-style compose stack on a developer
  machine, with a script that repeats the cases and logs restore time. Proves ADR-016 before there is a production
  server; the same script later runs on the VPS.
status: 'Done'
phase: 0
depends_on: []
purpose: 'Implementation spec for task 021.'
---

# Task 021: Backup and restore rehearsal (local)

## TL;DR

The backup agent ([ADR-016](../../docs/adr/ADR-016-postgres-backups.md)) has been tried by hand under `aspire run`, but
the failure cases have not been repeated on Kupro, and nothing records how long a restore takes. There is no
production server yet, so this task rehearses everything locally, on the stack closest to production: the generated
[deploy/docker-compose.yaml](../../deploy/docker-compose.yaml) (`dotnet aspire deploy`). The output is a repeatable
script and a dated log, so the same run can be repeated on the VPS later.

## Scope

- **Stack**: the local production-style compose stack, with the external volumes created as the
  [deploy runbook](../../docs/runbooks/deploy.md#backups-once-per-environment) describes and a local S3 standing in for
  the repo and ops buckets (Silo, as under `aspire run`). Keycloak's database is in the shared cluster here, so its
  checks run too, unlike under `aspire run`. If the generated compose can't point at a local S3, add that to the
  AppHost publish path rather than editing `deploy/` by hand.
- **Seed data**: a script creates a few workspaces, projects, issues, comments and an attachment through the API, so
  the restore checks (workspace count, tenant isolation) have something real to compare.
- **Cases** (the list from the [agent README](../../infrastructure/backup-agent/README.md#trying-it-locally)), each with
  the expected outcome written down before it runs:
  1. Agent stopped past the slot cap: Postgres drops the slot and keeps running; WAL gap reported and alerted;
     the agent recovers with a new full backup.
  2. Wrong S3 key: backups fail loudly (status, metric, heartbeat), Postgres unaffected.
  3. Agent container recreated with WAL not yet pushed: nothing lost once it restarts.
  4. Postgres stopped and started: streaming resumes, no gap.
  5. Console reached from another container on the app network: refused.
  6. Point-in-time restore to just before a mass delete (delete a project's issues, restore to a minute earlier):
     the deleted rows are back; go live; restart the stack; the app works; switch back.
  7. Lost server: new empty volumes and a fresh agent state, same bucket and cipher pass; restore the newest backup
     and go live. This is the "lost VPS" path in the [restore runbook](../../docs/runbooks/restore.md).
  8. Wrong cipher pass: restore refuses, and the console says why.
- **Script**: `scripts/backup-rehearsal.ps1` runs the cases it can drive without a person (1–5, 8, and the restore
  steps of 6–7 through the console's endpoints or the agent CLI), prints pass/fail per case and the measured restore
  time. Steps that need the console UI are listed for a person to click through.
- **Log**: results, date, database size and restore time in a "Rehearsals" section of the agent README. Log the
  restore time on the console's Drills page as a manual drill.
- **Runbook fixes**: anything that was wrong or unclear in the restore runbook during the rehearsal gets fixed there.

## Acceptance criteria

- [x] All 8 cases run on the local production-style stack and behave as expected; any that don't get a fix or a
      written gap in the agent README's "Known limits".
- [x] After the point-in-time restore and the lost-server restore, the app works: sign in, the seeded issues and the
      attachment are there, and the tenant-isolation check passes.
- [x] `scripts/backup-rehearsal.ps1` repeats the automated cases from a clean stack and exits non-zero on a failure.
- [x] Restore time and database size are logged in the agent README and as a manual drill in the console.
- [x] The restore runbook matches what was actually done.

## Out of scope

- Rehearsing on the VPS, done once the server exists (task 013). Not with this script: it deletes and recreates its
  own volumes, so it only drives the local stack. On the VPS a person runs the restore through the console (a manual
  step); see the agent README's [Rehearsals](../../infrastructure/backup-agent/README.md#rehearsals).
- A second, off-site copy of the backup repo ([deploy runbook](../../docs/runbooks/deploy.md#open-gaps)).
- Backing up attachments: only the database is covered ([agent README](../../infrastructure/backup-agent/README.md#known-limits)).
