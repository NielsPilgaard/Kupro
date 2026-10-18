---
title: 'ADR-017: Public Website'
status: 'Accepted'
date: '2026-10-18'
authors: 'Niels'
tags: ['frontend', 'marketing', 'hosting', 'i18n']
supersedes: ''
superseded_by: ''
description: >-
  The public website (landing, pricing, legal pages) is a static Astro site in site/, in English and Danish, sharing
  the app's design tokens and self-hosted fonts. It is served by its own nginx container on the apex domain; the app
  moves to the app. subdomain. The landing page leads with the meeting agent, then speed, the delivery plan and agents.
---

# ADR-017: Public Website

## TL;DR

- Astro, built to static HTML with no client JavaScript by default, in `site/`. Bun runs it, like `web/` ([ADR-003](ADR-003-frontend-stack.md)).
- English at `/`, Danish at `/da/`; every string comes from a locale file ([ADR-011](ADR-011-i18n.md)).
- Same tokens (`web/src/styles/tokens.css`) and fonts as the app ([design-guide.md](../design-guide.md)); no third-party requests.
- Own container (`site`, nginx) on the apex domain; the app moves to `app.<domain>`. Aspire runs it locally ([ADR-012](ADR-012-aspire.md)).
- Story order: meeting agent first, then fast and easy to use, then the delivery plan, then agents (CLI/MCP), then EU.

## Status

**Accepted**

## Context

Pricing ([ADR-013](ADR-013-seats-billing.md)), cookies and the legal drafts are settled in the look book
(`docs/design/public-pages/`), and the app is close to its first deploy (task 013). Visitors need a landing page, a
pricing page and the legal pages before the first external user.

The app is a React SPA behind login ([ADR-003](ADR-003-frontend-stack.md)): fine for a tool, poor for pages that
search engines must read and that should load instantly on a phone. The public pages change rarely and hold no state.

## Decision

- **Astro static site** in `site/`, output `static`. Pages are `.astro` components; no React islands unless a page
  needs interaction, which none does today.
- **Languages**: Astro i18n routing, `en` default without prefix, `da` under `/da/`. Copy lives in
  `site/src/i18n/{en,da}.ts`; pages never contain literal copy. Legal text is long-form, so each legal page is one
  HTML partial per language in `site/src/legal/{en,da}/`, carried over from the drafts; Danish legal text follows
  lawyer review (manual step E2).
- **Design**: imports the app's `tokens.css` and the same `@fontsource` packages, so there is one palette and the
  [design guide](../design-guide.md) rules apply unchanged (no hex or font names outside `tokens.css`). Light and dark
  follow the visitor's system setting.
- **No tracking, no cookies, no banner.** The site sets nothing; the app sets only strictly necessary sign-in storage.
  A footer line and a cookie page say so.
- **Hosting**: its own `site` service (nginx with the built files), behind Dokploy's Traefik on the apex domain, with
  `www.` redirecting to it. The app and API move to `app.<domain>`, so the SPA's routes and the site's never collide.
- **Landing story**: lead with the meeting agent (the newest, most demo-able feature, privacy first), then speed and
  ease of use ("every action under half a second", keyboard first), then the delivery plan, then agent parity, then
  the EU vendor list and pricing.

## Consequences

### Positive

- **POS-001**: Pages are plain HTML and CSS: fast on any connection, readable by search engines, nothing to hydrate.
- **POS-002**: One set of tokens and fonts for app and site; a palette change lands in both.
- **POS-003**: The site deploys and scales separately from the app and has no access to it.

### Negative

- **NEG-001**: A second JavaScript project to keep up to date (Astro, fonts), though small and build-time only.
- **NEG-002**: The app's URL changes to `app.<domain>` before the first deploy: the Keycloak redirect URIs, the
  `api-public-url` parameter and DNS follow (deploy runbook).

## Alternatives Considered

### Public routes in the web SPA

- **ALT-001**: **Description**: Landing and pricing as TanStack Router routes in `web/`.
- **ALT-002**: **Rejection Reason**: The visitor downloads the app bundle to read a landing page, and search engines
  see an empty shell unless we add pre-rendering, which the app doesn't otherwise need.

### Hand-written HTML

- **ALT-003**: **Description**: Static HTML and CSS files like the look book, no build step.
- **ALT-004**: **Rejection Reason**: The header, footer and every string repeat across about 14 pages in two
  languages; i18n by copy-paste breaks ADR-011.

### Hosted site builder (Webflow, Framer)

- **ALT-005**: **Description**: Build the site in a hosted no-code tool.
- **ALT-006**: **Rejection Reason**: US vendors ([ADR-006](ADR-006-eu-vendor-policy.md)), and the site would drift
  from the design tokens.

## Implementation Notes

- **IMP-001**: [Task 019](../../tasks/019-public-site.md) builds the site, its container and the Aspire resource.
- **IMP-002**: `site/Dockerfile` builds with Bun and copies `dist/` into `nginx:alpine`; build context is the repo
  root so the site can import `web/src/styles/tokens.css`.
- **IMP-003**: The look book stays in `docs/design/public-pages/` as the design record; the site is the source of
  truth for the live copy.

## Related Decisions

- [ADR-003](ADR-003-frontend-stack.md) — the app's frontend; the site shares its toolchain and tokens
- [ADR-004](ADR-004-hosting.md) — the `site` service and the domain split
- [ADR-006](ADR-006-eu-vendor-policy.md) — no third-party fonts, analytics or embeds
- [ADR-011](ADR-011-i18n.md) — English and Danish copy
- [ADR-012](ADR-012-aspire.md) — the `site` resource in the AppHost
- [ADR-013](ADR-013-seats-billing.md) — the prices the pricing page shows
