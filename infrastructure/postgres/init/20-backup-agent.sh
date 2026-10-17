#!/bin/bash
# The backup agent's role (ADR-016), with only what pg_receivewal and pgBackRest need. Runs once, when the data volume
# is empty. Socket only (pg_hba.conf peer map), no password. It reads no table data: row counts come from
# pg_stat_user_tables, and the migrate step grants SELECT on the migration history (DatabaseExtensions).
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<'SQL'
CREATE ROLE backup_agent LOGIN REPLICATION;
GRANT pg_read_all_settings, pg_checkpoint TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_backup_start(text, boolean) TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_backup_stop(boolean) TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_switch_wal() TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_create_restore_point(text) TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_control_checkpoint() TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_control_system() TO backup_agent;
GRANT EXECUTE ON FUNCTION pg_ls_dir(text) TO backup_agent;

-- The agent commits this row every 5 minutes before switching WAL. That gives every WAL segment a commit timestamp,
-- so a point-in-time target is always reachable even on a quiet night, and a restore can read how far it got. It's
-- the only table the agent writes.
CREATE TABLE backup_agent_heartbeat (id int PRIMARY KEY, at timestamptz NOT NULL);
ALTER TABLE backup_agent_heartbeat OWNER TO backup_agent;
SQL
