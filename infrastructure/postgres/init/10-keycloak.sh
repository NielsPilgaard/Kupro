#!/bin/bash
# Keycloak's own role and database in the shared cluster (task 013), so one backup covers both.
# The postgres image runs this once, when the data volume is empty; later password changes are ALTER ROLE by hand.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  --set=password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
CREATE ROLE keycloak LOGIN PASSWORD :'password';
CREATE DATABASE keycloak OWNER keycloak;
SQL
