# Backup agent (ADR-016): Postgres binaries (pg_receivewal, pg_ctl for the drill and restore checks), pgBackRest and
# the .NET console, in one image. The same image runs the weekly drill, the console, and a rebuild on a fresh VPS.
# Build context: the repo root (for Directory.Build.props and the central package versions). The image tag is a hash
# of exactly these inputs (scripts/infra-image-tags.sh), so keep the two in step.
#
# PG_MAJOR must match infrastructure/postgres/Dockerfile: a physical restore needs the same major.
ARG PG_MAJOR=18

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY infrastructure/backup-agent/Kupro.BackupAgent.csproj infrastructure/backup-agent/
RUN dotnet restore infrastructure/backup-agent/Kupro.BackupAgent.csproj --runtime linux-x64
COPY infrastructure/backup-agent/ infrastructure/backup-agent/
# No analyzers here: their settings live in the repo's .editorconfig, and CI's solution build enforces them.
RUN dotnet publish infrastructure/backup-agent/Kupro.BackupAgent.csproj \
    --no-restore --configuration Release --runtime linux-x64 --self-contained -p:RunAnalyzers=false \
    --output /app

FROM postgres:${PG_MAJOR} AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends pgbackrest ca-certificates curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /pgdata/a /pgdata/b /pg-control /var/lib/backup-agent /wal-receive /drill \
 && chown -R postgres:postgres /pgdata /pg-control /var/lib/backup-agent /wal-receive /drill \
 && chmod 0700 /pgdata/a /pgdata/b /drill

COPY infrastructure/backup-agent/pgbackrest.conf /etc/pgbackrest/pgbackrest.conf
COPY --from=build /app /app

# Runs as the Postgres uid (999), so it can read the live data volume (0700) and own what it restores.
USER postgres
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:9090 \
    DOTNET_ENVIRONMENT=Production \
    PGBACKREST_CONFIG=/etc/pgbackrest/pgbackrest.conf
EXPOSE 9090
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD curl -fsS http://127.0.0.1:9090/healthz || exit 1
ENTRYPOINT ["/app/Kupro.BackupAgent"]
# Clear the base image's CMD ("postgres"), or it would be passed to the agent as an argument.
CMD []
