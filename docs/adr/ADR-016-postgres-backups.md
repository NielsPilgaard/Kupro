---
title: 'ADR-016: Postgres backups — backup agent, pgBackRest, A/B data volumes'
status: 'Accepted'
date: '2026-10-15'
authors: 'Niels'
tags: ['infrastructure', 'backups', 'postgres', 'operations']
supersedes: ''
superseded_by: ''
description: >-
  A backup-agent container next to Postgres streams WAL over a replication slot into an encrypted pgBackRest repo
  in OVHcloud Object Storage, takes a nightly full backup, drills a restore every week, and serves a break-glass
  restore console over an SSH tunnel. Restores go to a spare data volume and go live by swapping volumes. Ported
  from Skoleoverblikket. Alerts through Grafana and elmah.io heartbeats.
---

# ADR-016: Postgres backups — backup agent, pgBackRest, A/B data volumes

## TL;DR

- `backup-agent` (infrastructure/backup-agent) streams WAL with `pg_receivewal` and pushes it to a pgBackRest repo (S3, AES-256). Postgres has no `archive_command`. RPO 15 minutes; full backup nightly at 02:00; retention 35 days (the DPA draft).
- Restores never touch live: the wizard restores into the spare of two data volumes (`pgdata-a`/`pgdata-b`), checks it, and a person makes it live; Postgres starts on whichever `/pg-control/active` names.
- Weekly drill restores the newest backup into tmpfs, checks it against live and throws it away. Weekly `pgbackrest verify`.
- Alerts: agent metrics → Grafana rules (ADR-014), plus elmah.io heartbeats from outside the VPS. Console: `127.0.0.1` only, reached over SSH.

## Status

**Accepted**

## Context

[ADR-004](ADR-004-hosting.md) puts a self-managed Postgres on one VPS, so losing the VPS, a bad migration or a mass
delete must be recoverable by one person at 3 a.m., and the restore must be proven, not assumed. Skoleoverblikket built
and tested exactly this agent ([its README](../../infrastructure/backup-agent/README.md)); Kupro has the same stack
(.NET, Aspire, Dokploy, OVHcloud), so we port it instead of writing a second one.

## Decision

1. **WAL by streaming, not `archive_command`.** The agent owns a physical replication slot and runs `pg_receivewal`
   over the shared Unix socket, then `pgbackrest archive-push`. If the agent or S3 breaks, Postgres drops the slot at
   `max_slot_wal_keep_size` (4 GB) and keeps running: the failure is a WAL gap and an alert, never a full disk.
2. **pgBackRest repo** in its own OVHcloud Object Storage bucket, encrypted with a cipher pass kept in the password
   manager. Daily full backups, time retention 34 days, so nothing restorable is older than 35 days.
3. **A/B data volumes.** Postgres runs our image (infrastructure/postgres); its entrypoint starts on `/pgdata/a` or
   `/pgdata/b` as `/pg-control/active` says. The agent only ever writes to the volume Postgres is not running on.
   Going live = write the other letter, restart the stack. The old volume stays for forensics until deleted.
4. **Least privilege.** The agent connects as `backup_agent` (replication + backup functions, peer auth on the socket
   only) and reads no table data, except `SELECT` on the EF migration history for "restore to just before migration
   X". It is not on the app network.
5. **Proof.** A weekly drill restores into tmpfs and checks tables, rows, migrations, tenant isolation (no row whose
   `workspace_id` has no workspace, [ADR-009](ADR-009-multi-tenancy.md)), the point reached and Keycloak's realm and
   users. A quarterly manual drill (rebuild from nothing) is logged in the console.
6. **Alerting.** The agent exports its health as OTLP metrics to Alloy; Grafana alerts when it is unhealthy or silent
   ([ADR-014](ADR-014-observability.md)). elmah.io heartbeats alert when the agent goes quiet even if the whole VPS,
   Grafana included, is gone.
7. **Image tags by content.** The Postgres and agent images are tagged with a hash of their sources, so a normal
   deploy doesn't replace the database container.

## Consequences

### Positive

- **POS-001**: 15-minute RPO and point-in-time restore to any minute in 35 days, including "just before migration X".
- **POS-002**: A broken backup setup can't take the database down.
- **POS-003**: Restores are rehearsed weekly with measured restore time (RTO), so the runbook stays true.
- **POS-004**: The console works with the API, Keycloak and the app database all down.

### Negative

- **NEG-001**: Postgres major upgrades need both images bumped together and a test pass (physical restores need the
  same major).
- **NEG-002**: Attachments in object storage are not covered; only the database is. A restore can leave orphaned files
  or rows pointing at deleted files.
- **NEG-003**: One more container with memory headroom for the drill's tmpfs (default 1 GB tmpfs, 1.5 GB limit).
- **NEG-004**: No GDPR erasure ledger yet: Kupro has no workspace or account deletion. When it gets one, a restore
  would bring erased data back; the agent's ledger (see the README) must be added with that feature.

## Alternatives Considered

### Nightly `pg_dump` + `archive_command` to S3 (ADR-004's original sketch)

- **ALT-001**: **Description**: Dump nightly and archive WAL from Postgres itself.
- **ALT-002**: **Rejection Reason**: A failing `archive_command` fills the disk and stops Postgres; restores are manual
  and untested; no point-in-time tooling or console.

### Managed Postgres (OVHcloud or Scaleway)

- **ALT-003**: **Rejection Reason**: Rejected in [ADR-004](ADR-004-hosting.md) on cost; also gives up Keycloak and the
  app in one cluster.

### Grafana alerts only, or heartbeats only

- **ALT-004**: **Rejection Reason**: Grafana dies with the VPS; heartbeats alone would duplicate Grafana's role for
  everything short of that. Both, each for what it's good at.

## Implementation Notes

- **IMP-001**: Agent code, contract and adapting notes: [infrastructure/backup-agent/README.md](../../infrastructure/backup-agent/README.md).
  Incident steps: [restore runbook](../runbooks/restore.md). Variables and first-time setup: [deploy runbook](../runbooks/deploy.md).
- **IMP-002**: Postgres 18 (Skoleoverblikket runs 17). `PG_MAJOR` sits in both Dockerfiles; change them together.
- **IMP-003**: The pinned volumes are external in compose (`kupro-pgdata-a`, `-b`, `kupro-pg-control`,
  `kupro-wal-receive`, `kupro-backup-agent-state`) and are created by hand once per server.
- **IMP-004**: The migrate step grants `backup_agent` `SELECT` on `__EFMigrationsHistory` on every run
  (`DatabaseExtensions`), so a `pg_restore`d database gets it too.

## Related Decisions

- [ADR-004](ADR-004-hosting.md) — hosting; this ADR owns backups
- [ADR-006](ADR-006-eu-vendor-policy.md) — elmah.io added to the vendor register
- [ADR-009](ADR-009-multi-tenancy.md) — tenant isolation check in the drill
- [ADR-012](ADR-012-aspire.md) — the AppHost publishes Postgres and the agent into the compose file
- [ADR-014](ADR-014-observability.md) — agent metrics and the backup alert rules
