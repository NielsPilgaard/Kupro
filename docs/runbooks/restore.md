---
title: 'Runbook: restore'
description: >-
  What to do when data is lost or the database is broken, written for 3 a.m. Step 1 is always the backup console over
  an SSH tunnel. Covers the restore wizard, going live by swapping the A/B data volumes, the post-restore checklist, WAL
  gaps, S3 outages, a lost VPS and the 35-day retention limit.
status: 'Living'
purpose: 'The incident runbook for the database and its backups (ADR-016).'
---

# Runbook: restore

## TL;DR

1. Open the backup console: `pwsh scripts/backup-console.ps1 <user>@<vps>`.
2. **Restore** → pick a point → type the spare volume's name → wait for the checks.
3. Type the name again and click **Go live**, then restart the stack (§4).
4. Work through the checklist the console shows, including the GDPR assessment within 72 hours.

Never delete a `kupro-pgdata-*`, `kupro-pg-control` or `kupro-wal-receive` volume, and never run
`docker compose down -v`. They are the database.

How it works: [ADR-016](../adr/ADR-016-postgres-backups.md) and the
[agent README](../../infrastructure/backup-agent/README.md). Variables: [deploy runbook](deploy.md).

## 1. Open the console (step 1 of every incident)

The console runs in the `backup-agent` container. It doesn't need Keycloak, the API or the app database, so it works
when they're down.

```bash
pwsh scripts/backup-console.ps1 <user>@<vps>
```

It reads the access link from the agent over SSH, opens the tunnel and opens the link in your browser. Keep the window
open; Ctrl+C closes the tunnel. Set `BACKUP_CONSOLE_SERVER=<user>@<vps>` once and run it with no arguments. If 9090 is
taken locally, add `-LocalPort 9091`.

Without the script:

```bash
ssh -L 9090:127.0.0.1:9090 <user>@<vps>
# on the VPS, in that session:
docker exec $(docker ps -qf label=com.docker.compose.service=backup-agent) cat /var/lib/backup-agent/console-link
```

The link (`http://localhost:9090/access?key=…`) sets a cookie for 12 hours. The key lives in the agent's volume, so
only someone with a shell on the VPS can read it.

| Page | Use it for |
|---|---|
| Overview | "Data secured X min ago", newest backup, oldest restore point, slot, disk, heartbeats |
| Backups | What can be restored (per timeline), every backup, WAL gaps, "Back up now", "Verify now" |
| Drills | Drill history and RTO trend, "Drill now", log the quarterly manual drill |
| Restore | The restore wizard, going live, the post-restore checklist, deleting the old volume |
| History | Every run, action and event (also in the ops bucket under `history/`) |

**The console doesn't load:** the agent is down. `docker ps -a | grep backup-agent`, `docker logs <container>`, then
redeploy the compose app in Dokploy. Postgres keeps running without the agent; WAL waits in the replication slot for
up to 4 GB.

## 2. Decide the scope

- **The whole database is wrong** (bad migration, broken disk, mass deletion): §3–§5. App and Keycloak come back to
  the same point in time, since they share the cluster.
- **One workspace deleted something:** §7. Never roll every workspace back for one workspace's mistake.
- **Attachments:** not covered by the agent, only the database (ADR-016 NEG-002).

## 3. Restore with the wizard

**Restore** in the console:

1. Pick the point: a time (Copenhagen time), "Just before a migration" (stops just before the transaction that applied
   it), or "Full backup as-is". Times outside the restorable ranges are refused; a WAL gap splits the ranges.
2. Type the spare volume's name and start. The wizard wipes the spare volume, runs `pgbackrest restore` into it, starts
   a temporary Postgres on it inside the agent, runs the drill checks and stops it again. The live volume is never
   touched: the agent asks Postgres which directory it runs on and only writes to the other one.
3. Read the summary: the checks, rows per table against live, latest migration, Keycloak users, and the time recovery
   actually reached.

If live Postgres is down, the comparisons with live are skipped and marked "—". That's expected in a real incident.

**Restoring to before a bad migration?** Roll the app back first (Actions → CD → Run workflow with the last good
`sha-<commit>`), or the `migrations` service applies the same migration again when the stack restarts.

## 4. Go live (swap the A/B volumes)

1. Under the summary, type the spare volume's name and click **Go live**. If a check failed, read it first: tick the
   box to go live anyway (in a real incident live may be worse). The agent writes the spare's letter to
   `/pg-control/active`, and the header shows "kupro-pgdata-b live on restart".
2. Restart the stack so Postgres starts again and reads the marker: in Dokploy, **Stop** then **Start** the compose
   app, or on the VPS:

   ```bash
   for s in postgres keycloak api; do docker restart $(docker ps -qf label=com.docker.compose.service=$s); done
   ```

   Postgres starts on the restored volume (a new timeline); Keycloak and the API restart behind it, so no stale
   connections or caches.
3. The agent notices the new live volume, creates a new replication slot, takes a full backup at once and shows the
   checklist (§5).

Changed your mind before restarting: **Cancel** on the banner. To undo after going live: **Switch back** under "Old
volume" on the Restore page, then restart again. The old volume was never written to.

If Postgres won't start and its log says `select-volume: /pgdata/b is empty`, the marker names a volume without a
database. Switch back (or Cancel) and restart.

## 5. After go-live

The console's **Restore** page shows the checklist; each tick goes to history:

- **Full backup** on the new timeline: the agent takes it by itself.
- **Smoke test:** sign in, open a board, create an issue, open an attachment. Use your own workspace, never a
  customer's.
- **Grafana:** no new errors on the Errors & logs dashboard.
- **Tell the workspaces:** everything after the restore point is gone: issues, comments, sign-ups, API tokens and agent
  tokens created since then. CLI and MCP clients with newer tokens get 401 and must sign in again. Attachments uploaded
  after the point stay in object storage without a row (harmless); files deleted after it may be missing.
- **GDPR:** §6.
- **Old volume:** it keeps the pre-restore database for forensics. Delete it with "Delete old volume" once you're done,
  at the latest after 35 days (the DPA's backup retention). The console warns after 7.

## 6. GDPR

Lost data is a personal data breach (availability). Assess within 72 hours whether Datatilsynet must be notified. As
data processor we notify the affected customers without undue delay (DPA). Write down what was lost (the window
between the restore point and the incident) and which workspaces wrote data in it.

## 7. One workspace's mistake

Restore to the spare volume with the wizard (§3) but **don't go live**. Then read that workspace's rows from the spare
(the console names it; `kupro-pgdata-b` here) with a throwaway Postgres that has no network, on the VPS:

```bash
docker run --rm -d --name restore-read --network none -e PGDATA=/pgdata/b -v kupro-pgdata-b:/pgdata/b \
  postgres:18 bash -c "echo 'local all all trust' > /tmp/hba && exec docker-entrypoint.sh postgres -c hba_file=/tmp/hba"
docker exec -it restore-read psql -U postgres -d kupro
# \copy (SELECT * FROM issues WHERE workspace_id = '<workspace id>') TO '/tmp/issues.csv' CSV HEADER
docker stop restore-read
```

Insert the rows into live by hand. The files stay on the VPS; never copy them to a laptop or GitHub. A workspace export
from a point in time is not built.

## 8. WAL gap (lost slot)

If the agent falls more than 4 GB behind (agent down for long, S3 down past the agent's own 4 GB buffer), Postgres
drops the slot and keeps running. The console goes red: "WAL gap". A point-in-time restore can't cross the gap.

The agent recovers by itself: new slot, streaming again, full backup requested. If that backup can't run, fix the
cause and click **Back up now** (full). The gap closes with that backup. Restores into the gap can only go to the last
backup before it.

## 9. The repo or S3 is down

`archive-push` fails and "Not pushed yet" grows on the overview. Up to 4 GB waits on the agent's `wal-receive` volume,
then the agent pauses and Postgres holds up to 4 GB more in the slot, then §8. Fix the endpoint or keys in Dokploy
(`BACKUP_S3_*`) and redeploy. Everything waiting is pushed, oldest first, with no gap.

## 10. The VPS is gone

The agent image restores without anything from the old box. On a new VPS with Docker:

```bash
for v in pgdata-a pgdata-b pg-control wal-receive backup-agent-state; do docker volume create kupro-$v; done
docker run -d --name backup-agent -p 127.0.0.1:9090:9090 \
  --label com.docker.compose.service=backup-agent \
  -v kupro-pgdata-a:/pgdata/a -v kupro-pgdata-b:/pgdata/b \
  -v kupro-pg-control:/pg-control \
  --env-file agent.env \
  ghcr.io/<owner>/kupro-backup-agent:<tag>
```

`agent.env` holds the values from the password manager: `PGBACKREST_REPO1_S3_ENDPOINT`, `_S3_REGION`, `_S3_BUCKET`,
`_S3_KEY`, `_S3_KEY_SECRET`, `_CIPHER_PASS`, and `OpsBucket__ServiceUrl`, `__BucketName`, `__AccessKey`,
`__SecretKey`. Open the console (§1); it shows Postgres as down, which is right, and treats `kupro-pgdata-a` as
live, so the wizard restores into `kupro-pgdata-b`. Restore, click Go live (it writes `b` to the marker), stop and
remove this container, then install Dokploy and deploy the compose app as in the [deploy runbook](deploy.md). Postgres
starts on `kupro-pgdata-b`. Without the cipher pass the backups are unreadable.

## 11. A backup is older than 35 days

The console flags it red (DPA). Normally `expire` after each daily full keeps the oldest at 34–35 days. If fulls keep
failing, the oldest data just gets older. Fix the backup first. If no new full can be taken, the DPA wins: expire the
stale backups by hand and log it in the console's history, knowing that leaves nothing to restore until the next full
succeeds.

```bash
docker exec $(docker ps -qf label=com.docker.compose.service=backup-agent) pgbackrest --stanza=main expire --set=<label>
```

## 12. The agent says the live database has another system-id

The live cluster isn't the one the repo knows, e.g. it was re-initialized empty. Stop and don't delete anything: check
which volume `/pg-control/active` names (`docker exec <postgres container> cat /pg-control/active`) and that it's the
one you meant. If a new cluster is intended, point the agent at a new repo path (`PGBACKREST_REPO1_PATH`), remove
`/var/lib/backup-agent/state.json` in the agent and redeploy it. The old path keeps the old backups until you delete
them (within 35 days, DPA).

## 13. Where things live

| Thing | Where |
|---|---|
| Live and spare database | Docker volumes `kupro-pgdata-a` / `-b`; `/pg-control/active` (volume `kupro-pg-control`) says which one Postgres starts on |
| WAL not yet in the repo | Docker volume `kupro-wal-receive` |
| Backups (encrypted) | pgBackRest repo bucket in OVHcloud Object Storage (`BACKUP_S3_BUCKET`) |
| Status, history, migration list | Ops bucket (`BACKUP_OPS_BUCKET`: `status.json`, `history/`, `migrations.json`) |
| Console access key, agent state | Docker volume `kupro-backup-agent-state` |
| Secrets (S3 keys, cipher pass, role passwords) | Dokploy environment and the password manager |
| Measured restore times | Console → Drills (weekly drills and the quarterly manual drill) |
