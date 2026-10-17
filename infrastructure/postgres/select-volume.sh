#!/usr/bin/env bash
# Picks the data volume, then hands over to the official entrypoint. Both volumes are
# mounted, at /pgdata/a and /pgdata/b. /pg-control/active says which one is live. The backup console
# writes it when a restore goes live, and the next redeploy starts Postgres on that volume. No env
# vars to change in Dokploy.
set -euo pipefail

control=/pg-control/active

# The volume starts out root-owned. The agent (same uid as postgres) writes the file.
if [[ "$(id -u)" == 0 ]]; then
	chown postgres:postgres /pg-control
fi

active=a
if [[ -f "$control" ]]; then
	active=$(tr -d '[:space:]' < "$control")
fi

if [[ "$active" != a && "$active" != b ]]; then
	echo "select-volume: $control must say a or b, not '$active'" >&2
	exit 1
fi

export PGDATA="/pgdata/$active"

# Only the very first start may create a cluster: the agent writes the file once it has seen this
# one run. After that an empty volume means a wrong switch, and a new empty database would look
# healthy to the app.
if [[ -f "$control" && ! -s "$PGDATA/PG_VERSION" ]]; then
	echo "select-volume: $PGDATA is empty. Refusing to create a new database; check $control." >&2
	exit 1
fi

echo "select-volume: starting on pgdata-$active"
exec docker-entrypoint.sh "$@"
