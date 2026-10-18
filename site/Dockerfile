# Public site image (ADR-017). Build context = repo root: the site imports web/src/styles/tokens.css.
# Bun tag pinned to "packageManager" in site/package.json.
FROM oven/bun:1.4.3-alpine AS build
WORKDIR /app/site

COPY site/package.json site/bun.lock ./
RUN bun install --frozen-lockfile

COPY web/src/styles/tokens.css ../web/src/styles/tokens.css
COPY site/ .
# Canonical URLs and the app link; set by the AppHost from the deploy parameters.
ARG SITE_URL=http://localhost:4321
ARG PUBLIC_APP_URL=http://localhost:5180
ENV SITE_URL=$SITE_URL PUBLIC_APP_URL=$PUBLIC_APP_URL ASTRO_TELEMETRY_DISABLED=1
RUN bun run build

FROM nginx:1.29-alpine AS runtime
COPY --from=build /app/site/dist /usr/share/nginx/html
COPY site/headers.conf /etc/nginx/snippets/headers.conf
COPY site/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
