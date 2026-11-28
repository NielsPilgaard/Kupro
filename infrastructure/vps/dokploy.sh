#!/usr/bin/env bash
# Installs Dokploy and creates Kupro's external data volumes. Idempotent. Run after harden.sh.
# scripts/go-live/go-live.mjs vps copies and runs both; by hand:
#
#   scp infrastructure/vps/dokploy.sh ubuntu@<vps>:
#   ssh -t ubuntu@<vps> sudo bash dokploy.sh

set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root: sudo bash $0" >&2; exit 1; }

printf '\n\033[1m== Dokploy\033[0m\n'
if command -v docker >/dev/null && docker service inspect dokploy >/dev/null 2>&1; then
	echo "   already installed"
else
	# Dokploy's documented installer: Docker, Swarm, Traefik on 80/443, the panel on 3000.
	curl -sSL https://dokploy.com/install.sh | sh
fi

printf '\n\033[1m== Data volumes\033[0m\n'
# External in the compose file, so compose refuses to start without them instead of starting an empty database.
for v in pgdata-a pgdata-b pg-control wal-receive backup-agent-state; do
	if docker volume inspect "kupro-$v" >/dev/null 2>&1; then
		echo "   kupro-$v exists"
	else
		docker volume create "kupro-$v" >/dev/null
		echo "   created kupro-$v"
	fi
done
