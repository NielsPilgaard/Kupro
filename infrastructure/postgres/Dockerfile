# Postgres for Flowstate (ADR-016). No backup tooling in here: WAL streaming and pgBackRest live in the backup-agent
# image (infrastructure/backup-agent), so a broken backup setup can't take Postgres down.
# PG_MAJOR must match infrastructure/backup-agent/Dockerfile, because physical restores need the same major.
ARG PG_MAJOR=18
FROM postgres:${PG_MAJOR}

COPY postgresql.conf pg_hba.conf pg_ident.conf /etc/postgresql/
COPY init/ /docker-entrypoint-initdb.d/
COPY select-volume.sh /usr/local/bin/
RUN chmod 0755 /docker-entrypoint-initdb.d/*.sh /usr/local/bin/select-volume.sh \
 && mkdir -p /pgdata/a /pgdata/b /pg-control \
 && chown postgres:postgres /pgdata/a /pgdata/b /pg-control

# Starts on /pgdata/a or /pgdata/b, whichever /pg-control/active names.
ENTRYPOINT ["select-volume.sh"]
CMD ["postgres", "-c", "config_file=/etc/postgresql/postgresql.conf"]
