# SPA image (ADR-012 IMP-002). Build context = repo root.
# Bun tag pinned to "packageManager" in package.json (ADR-003 IMP-005).
FROM oven/bun:1.4.3-alpine AS build
WORKDIR /app/web

COPY web/package.json web/bun.lock ./
RUN bun install --frozen-lockfile

# hey-api codegen reads ../openapi/ relative to web/
COPY openapi/ ../openapi/
COPY web/ .
ARG VITE_KEYCLOAK_URL=http://localhost:8080
ENV VITE_KEYCLOAK_URL=$VITE_KEYCLOAK_URL
RUN bun run build

FROM nginx:1.29-alpine AS runtime
COPY --from=build /app/web/dist /usr/share/nginx/html
# Rendered by the nginx entrypoint (envsubst) into /etc/nginx/conf.d/default.conf at start.
COPY web/nginx.conf /etc/nginx/templates/default.conf.template
ENV API_UPSTREAM=http://api:8080
EXPOSE 80
