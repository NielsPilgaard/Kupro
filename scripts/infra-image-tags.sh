#!/usr/bin/env bash
# Tags for the Postgres and backup-agent images (ADR-016), derived from their sources: they only change when those
# files change, so a normal deploy leaves the database container alone. Prints KEY=value lines for $GITHUB_ENV.
#
#   scripts/infra-image-tags.sh [commit]    (default: HEAD)
#
# The backup-agent inputs must match what its Dockerfile copies.
set -euo pipefail
shopt -s inherit_errexit

rev="${1:-HEAD}"

tag() {
  local path objects=() hashes
  for path in "$@"; do
    objects+=("$rev:$path")
  done
  hashes=$(git rev-parse "${objects[@]}")
  echo "src-$(sha1sum <<< "$hashes" | cut -c1-12)"
}

postgres=$(tag infrastructure/postgres)
agent=$(tag infrastructure/backup-agent global.json Directory.Build.props Directory.Packages.props)
echo "POSTGRES_TAG=$postgres"
echo "BACKUP_AGENT_TAG=$agent"
