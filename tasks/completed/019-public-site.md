---
title: 'Task 019: Public website'
description: >-
  Build the public site in site/ (Astro, static): landing page led by the meeting agent, pricing, and the legal pages,
  in English and Danish, with its own nginx container, an Aspire resource and the app moved to app.<domain>.
status: 'In progress'
phase: 1
depends_on: []
purpose: 'Implementation spec for task 019.'
---

# Task 019: Public website

## TL;DR

Decisions are in [ADR-017](../docs/adr/ADR-017-public-site.md); prices in [ADR-013](../docs/adr/ADR-013-seats-billing.md);
copy rules in [branding.md](../docs/branding.md). The look book in
[docs/design/public-pages/](../docs/design/public-pages/index.html) is the visual starting point.

## Scope

- **Project**: `site/` with Astro (static output), Bun, TypeScript. Imports `web/src/styles/tokens.css` and the
  `@fontsource` fonts the app uses. No client JavaScript except where a page needs it.
- **Layout**: header (logo, Product, Pricing, Docs, language switch, Sign in, Start a free trial), footer (company
  placeholder, product, developers, legal links, "no tracking" cookie line). Light and dark from the system setting.
- **Landing** (`/`, `/da/`), in this order:
  1. Hero: meeting agent ("Say it in standup. It's in the tracker."), live proposal mock, privacy promise band.
  2. Fast and easy: under half a second per action ([ADR-010](../docs/adr/ADR-010-performance-budget.md)),
     keyboard first, nothing to configure before the first issue.
  3. Delivery plan: plan mock with capacity band.
  4. Agents too: one action in the web app, the CLI and MCP.
  5. European by default: vendor table ([ADR-006](../docs/adr/ADR-006-eu-vendor-policy.md)), no tracking, AGPL.
  6. Price strip and closing call to action.
- **Pricing** (`/pricing`, `/da/priser`): Trial, Monthly, Yearly, Custom; what a seat is; FAQ (seats, idle-seat
  alerts, agents and guests, VAT, cancelling, non-profits, self-hosting).
- **Legal**: privacy, terms, data processing (DPA), sub-processors, cookies, from the drafts. English first; the
  Danish pages say the Danish text follows and link to the English one until the lawyer-reviewed text exists
  (manual step E2).
- **Container**: `site/Dockerfile` (Bun build, nginx runtime), long cache headers for hashed assets, 404 page.
- **Aspire**: `site` resource; dev server locally, the Dockerfile in publish mode, on the apex domain.
- **Domain split**: the app and API move to `app.<domain>`. Update the deploy runbook, the AppHost parameters' comments
  and the manual steps (DNS for apex, `www`, `app`).

## Out of scope

- Docs site, blog, changelog, status page (footer links point to placeholders until they exist).
- Screenshots or video of the real app; the mocks are HTML.
- Filling the legal placeholders and Danish legal text (manual step E2).

## Acceptance

- `bun run build` in `site/` produces static pages for every route in both languages; no hardcoded copy in pages.
- No requests to third-party hosts; no cookies set.
- Lighthouse (mobile) performance and accessibility ≥ 95 on `/` and `/pricing`.
- Works at 360 px wide without horizontal scroll; light and dark both readable.
- `aspire run` shows the `site` resource; `aspire publish` emits a `site` service.
