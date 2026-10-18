# Kupro Keycloak (ADR-005): stock Keycloak plus the kupro login and email theme and the realm.
# Build context = repo root. Dev users (kupro-users-0.json) stay out; dev and E2E mount them.
FROM quay.io/keycloak/keycloak:26.6

# Theme files are served from /resources/<Keycloak version>/, a path that stays the same when only the theme changes.
# Keycloak's default 30-day max-age would keep old styles in browsers after a deploy.
ENV KC_SPI_THEME__STATIC_MAX_AGE=3600
# Keycloak keeps gzipped theme files in data/tmp/kc-gzip-cache/<Keycloak version>/, inside the data volume, so a
# changed theme would be served stale until the next Keycloak upgrade. Start every container with an empty cache.
ENTRYPOINT ["/bin/bash", "-c", "rm -rf /opt/keycloak/data/tmp/kc-gzip-cache && exec /opt/keycloak/bin/kc.sh \"$@\"", "kc.sh"]

COPY infrastructure/keycloak/themes/ /opt/keycloak/themes/
# Colours, fonts and the mark come from the web app's single sources (docs/design-guide.md, docs/branding.md).
COPY web/src/styles/tokens.css /opt/keycloak/themes/kupro/login/resources/css/
COPY docs/design/brand/kupro-mark.svg docs/design/brand/kupro-mark-dark.svg /opt/keycloak/themes/kupro/login/resources/img/
# Imported once, when the realm doesn't exist yet; ${REALM_*} placeholders come from the environment (AppHost).
COPY src/Kupro.AppHost/Realms/kupro-realm.json /opt/keycloak/data/import/
