#!/usr/bin/env bash
# Tags for the Postgres and backup-agent images (ADR-016), derived from their sources: they only change when those
# files change, so a normal deploy leaves the database container alone. Prints KEY=value lines for $GITHUB_ENV.
#
#   scripts/infra-image-tags.sh [commit]    (default: HEAD)
#
# The backup-agent inputs must match what its Dockerfile copies.
set -euo pipefail

rev="${1:-HEAD}"

tag() {
  local path objects=()
  for path in "$@"; do
    objects+=("$rev:$path")
  done
  echo "src-$(git rev-parse "${objects[@]}" | sha1sum | cut -c1-12)"
}

echo "POSTGRES_TAG=$(tag infrastructure/postgres)"
echo "BACKUP_AGENT_TAG=$(tag infrastructure/backup-agent global.json Directory.Build.props Directory.Packages.props)"
