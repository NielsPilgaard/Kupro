---
title: 'Kupro — Branding'
description: >-
  Who Kupro sounds and looks like: name and wordmark, the Stream logo mark, the Kobber brand palette, voice and
  tone in English and Danish, and the product glossary. UI tokens and component rules live in design-guide.md.
status: 'Accepted'
purpose: 'Canonical source for name, logo, brand colours, voice and terminology.'
---

# Kupro — Branding

## TL;DR

- **Direction: Kobber** (copper). Copenhagen's copper roofs weather into verdigris green: local, durable, calm.
  Chosen in [task 000](../tasks/completed/000-frontend-ideas.md) from [the directions look book](design/directions.html); runner-up Fjord.
- **Mark: Stream** — three staggered bars, a delivery plan in miniature. Files in [design/brand/](design/brand/).
- **Wordmark**: lowercase `kupro` in Bricolage Grotesque Bold. In running text: "Kupro".
- **Voice**: plain, direct, short. Says what happened and what to do next. Same tone in English and Danish.
- UI tokens, components and layout rules: [design-guide.md](design-guide.md).

## Story

Kupro is a Danish, EU-sovereign issue tracker and planner ([product.md](product.md)). The identity borrows from
one thing every Dane recognises: the green copper spires of Christiansborg, Frederik's Church and the Stock Exchange.
Copper starts warm and weathers into verdigris (Danish: *irgrøn*) — a material that gets better the longer it is used.
That is the brand promise in one image: a tool you settle into, not one you fight.

What the brand is not: no flags, no Vikings, no hygge clichés, no gradients, no mascots. Danishness shows in material
and restraint, not symbols — the product is for EU teams too.

## Name and wordmark

| Use | Form |
|---|---|
| Running text (EN and DA) | Kupro |
| Wordmark (logo lockup, app sidebar) | `kupro` — lowercase, Bricolage Grotesque 700, tracking −1% |
| CLI binary, package names, domains | `kupro` |
| MCP server name | `kupro` |

Never: "KuPro", "KUPRO", "Kupro.app" in prose.

The name comes from Latin *cuprum*, copper, spelled with a Nordic K: the material the whole identity is built on.
Say it "KOO-pro". Chosen in Oct 2026 to replace the working name ([the name](naming.md)). Should it change again before go-live,
`node scripts/rename-product.mjs <NewName>` renames it everywhere (paths, namespaces, realm, database, CLI, copy).
Keep the name in one of the three forms above so the script catches it.

## Logo mark — Stream

Three rounded bars on a 32-unit grid, staggered like cards on a timeline. Top and bottom bars in ink (bottom at 50%),
middle bar in verdigris: the piece of work currently in flow.

| File | Use |
|---|---|
| [kupro-mark.svg](design/brand/kupro-mark.svg) | Light backgrounds |
| [kupro-mark-dark.svg](design/brand/kupro-mark-dark.svg) | Dark backgrounds, tinted sidebar |
| [kupro-mark-mono.svg](design/brand/kupro-mark-mono.svg) | Single colour (`currentColor`): docs, CLI banners, print |
| [favicon.svg](design/brand/favicon.svg) | Browser tab; follows OS light/dark |
| [kupro-lockup.svg](design/brand/kupro-lockup.svg) · [dark](design/brand/kupro-lockup-dark.svg) · [mono](design/brand/kupro-lockup-mono.svg) | Mark + wordmark, text outlined (no font needed): slides, press, partner pages |

Raster files are rendered from the mark by `bun scripts/brand-images.ts`, never drawn by hand: `favicon.ico` and the
app icons (listed in `manifest.json`) in `web/public/` and `site/public/`, the site's social cards (`og-image.png`,
`og-image-da.png`, headline from the hero copy), Keycloak's login favicon, and the GitHub
[social preview](images/social-preview.png). Rerun it after changing the mark, palette or hero headline.

Geometry (viewBox `0 0 32 32`, bar height 5, radius 2.5): `(3, 6.5, w17)`, `(9, 13.5, w20)`, `(5, 20.5, w12)`.

Rules:

- **Minimum size** 16 px (favicon). Below 24 px drop the wordmark and show the mark alone.
- **Clear space** = one bar height (5/32 of the mark) on every side.
- **Lockup**: mark left, wordmark right, gap = 0.4 × mark height, wordmark cap height ≈ 55% of mark height.
- Don't rotate, recolour the bars individually, add effects, or animate the bars apart. A subtle left-to-right slide of
  the middle bar is the only allowed motion (loading states), and it respects `prefers-reduced-motion`.

## Brand palette

Brand names for the core colours. Exact UI tokens (and their dark-mode values) are owned by
[design-guide.md](design-guide.md#colour); this table is for marketing, slides and docs.

| Name | Danish | Hex | Role |
|---|---|---|---|
| Verdigris | Irgrøn | `#2C7A6A` | Primary accent, the mark's middle bar |
| Copper | Kobber | `#B5651F` | Work in motion; secondary accent, sparingly |
| Ink | Blæk | `#16201D` | Text, mark, dark surfaces |
| Lime wash | Kalk | `#F2F4F3` | Light ground |
| Night | Nat | `#0F1514` | Dark ground |
| Spire | Spir | `#1C2A26` | App sidebar, dark brand panels |

Ratio on any brand surface: mostly Kalk or Nat, Ink for type, one Verdigris element, Copper only where something moves.

## Typography

| Role | Face | Notes |
|---|---|---|
| Display, wordmark | Bricolage Grotesque | Page titles and marketing headlines only, never body text |
| Text and UI | Hanken Grotesk | Everything people read and click |
| Data | Fragment Mono | Issue keys, hours, dates in tables, CLI and code |

All three are open-licence (OFL) Google Fonts; self-host them, never load from Google at runtime
([ADR-006](adr/ADR-006-eu-vendor-policy.md)). All cover æ, ø, å.

## Voice and tone

Write the way a calm, senior colleague talks: plain words, short sentences, active voice, no hype.

1. **Say what happened, then what to do.** "Couldn't move KUP-12: Sprint 24 is closed. Pick another sprint."
2. **Name things the way users do**: "sprint", "delivery plan", "capacity" — not "iteration entity" or "allocation config".
3. **Buttons say the action**: "Move issue", "Create feature". Never "OK", "Submit", "Yes".
4. **No exclamation marks, no emoji, no apologies, no "Oops".**
5. **Agents are colleagues, not magic.** Attribute their actions plainly: "triage-agent moved KUP-139 to In Review via MCP".
   Never "AI-powered", "magically", "smart".
6. **Danish is written Danish, not translated English.** Short, informal *du*-form, Danish punctuation and number
   formats (4,5 personer). Keep English loanwords devs actually use (backlog, sprint, review).

| Don't | Do |
|---|---|
| Oops! Something went wrong 😕 | Couldn't save the description. Check your connection and try again. |
| Successfully updated the issue! | Saved |
| Are you sure you want to proceed? | Delete KUP-12 and its 3 tasks? This can't be undone. |
| Hov! Der skete en fejl | Kunne ikke gemme beskrivelsen. Tjek din forbindelse, og prøv igen. |

## Glossary

Single source for product terms in both languages. Translators and agents use these exactly.
Mechanics of i18n: [ADR-011](adr/ADR-011-i18n.md). Domain definitions: [ADR-008](adr/ADR-008-work-item-model.md).

| English | Danish | Note |
|---|---|---|
| Workspace | Arbejdsområde | |
| Project | Projekt | |
| Issue | Opgave | Generic word for any work item |
| Epic / Feature / Task | Epic / Feature / Task | Type names stay English (also in CLI/MCP) |
| Backlog · Todo · In Progress · In Review · Done · Canceled | Backlog · Klar · I gang · Til review · Færdig · Annulleret | Fixed workflow |
| Sprint | Sprint | |
| Delivery plan | Leveranceplan | |
| Milestone | Milepæl | |
| Capacity | Kapacitet | "needs 4.5, has 3" → "behov 4,5, har 3" |
| Effort (hours) | Estimat (timer) | Unit: `h` / `t` |
| Assignee | Ansvarlig | |
| Agent | Agent | |
| Owner · Member · Guest | Ejer · Medlem · Gæst | Workspace roles. Guest is described as "Can view and comment. Free." |
| Seat | Plads | "5 per seat" → "5 pr. plads" |
| Billing access | Adgang til fakturering | A flag on any person, not a role |
| Label | Label | |
| Command palette | Kommandopalet | |

## Taglines

Primary: **Plan the year. Ship the week.** (DA: *Planlæg året. Lever ugen.*)

Alternates: "The issue tracker your agents can use too." · "European. Fast. Built to plan."
