---
title: 'Kupro — Design Guide'
description: >-
  The UI rulebook for the web app: Kobber design tokens (colour in light and dark, type, spacing, radius, density),
  app-shell layout, and the spec for every shared component — status ring, type tags, labels, avatars, list rows,
  board cards, delivery-plan cards, capacity band, command palette, toasts. Brand, voice and glossary live in branding.md.
status: 'Accepted'
purpose: 'Canonical source for UI tokens and component rules; every web UI change follows it.'
---

# Kupro — Design Guide

## TL;DR

- **Kobber** tokens: verdigris accent, copper for "in progress", tinted dark-green sidebar, light and dark mode as equals.
- **Comfortable density**: 34 px rows, 13 px base text. Hanken Grotesk for UI, Fragment Mono for keys/hours/numbers.
- **Components**: ring status icons, Epic/Feature tags (tasks untagged), outline plan cards, bar-style capacity band.
- **Rules**: tokens only (no hex in components), state never by colour alone, every action keyboard-reachable, every string via i18n.
- Live reference: [design/directions.html](design/directions.html) (direction *Kobber*, variants as below). Brand: [branding.md](branding.md).

## Principles

1. **Instant beats animated.** Optimistic updates land in the same frame ([ADR-003](adr/ADR-003-frontend-stack.md)); no
   transition may delay showing the new state.
2. **Calm density.** Show a lot, shout little. One accent element per view; colour is for state, not decoration.
3. **Keyboard first.** Every action has a palette entry; frequent ones a single-key shortcut, shown as a `kbd` hint.
4. **Agents are visible.** Agent assignees and agent actions look different from humans, and say which surface they used.
5. **Shape + colour + text.** State, type and warnings are always readable without colour (WCAG 1.4.1).

## Tokens

Implemented as CSS custom properties in `web/src/styles/tokens.css`, mapped into the Tailwind theme
(stack: [ADR-003](adr/ADR-003-frontend-stack.md)). Components reference tokens or Tailwind classes built on them —
**never raw hex**. Dark mode: `prefers-color-scheme`, overridable per user via `data-theme="light|dark"` on `<html>`.

### Colour

| Token | Light | Dark | Use |
|---|---|---|---|
| `--bg` | `#F2F4F3` | `#0F1514` | App ground |
| `--panel` | `#FFFFFF` | `#161F1D` | Cards, popovers, inputs, plan row headers |
| `--sunk` | `#E7ECEA` | `#0B100F` | Group headers, segmented-control track, code |
| `--line` | `#D3DBD8` | `#25322E` | Borders, dividers, grid lines |
| `--ink` | `#16201D` | `#E2ECE8` | Primary text, Todo state |
| `--mute` | `#56655F` | `#8FA29B` | Secondary text, keys, hours, Backlog/Canceled |
| `--acc` | `#2C7A6A` | `#5BB8A3` | Primary button fill, selection tint base |
| `--acc-ink` | `#246859` | `#6CC4AF` | Accent as text/line: links, focus, milestones, Feature tag |
| `--on-acc` | `#FFFFFF` | `#08130F` | Text on `--acc` |
| `--side` | `#1C2A26` | `#0B100F` | Sidebar ground (tinted in both modes) |
| `--side-ink` | `#CFE0DA` | `#B7CAC3` | Sidebar text and icons |
| `--prog` | `#B5651F` | `#E08E52` | In Progress (copper) |
| `--rev` | `#6553A8` | `#A194E6` | In Review, Epic tag |
| `--ok` | `#3F8A3C` | `#78C36F` | Done, capacity surplus, agent "working" dot |
| `--warn` | `#A9801A` | `#E3BC55` | Plan-health warnings |
| `--bad` | `#B3393B` | `#EE6E6E` | Errors, capacity deficit, violated dependency |

Derived (never hand-pick these): `--sel = color-mix(in srgb, var(--acc) 13%, transparent)` (selected row / active
palette item); `--side-sel = color-mix(in srgb, var(--side-ink) 11%, transparent)` (active nav item); chip/tag fill =
the chip's colour at 14% over transparent.

Contrast (measured): body text ≥ 15:1, `--mute` ≥ 5.6:1, `--acc-ink` on panel 6.6:1 (light) / 8.1:1 (dark),
button text 5.1:1 / 8.0:1. State colours are **icon colours** (≥ 3.6:1, non-text AA); text stays `--ink`/`--mute`,
except `--bad` which passes for text (≥ 5.7:1).

### Type

| Token | Face | Fallback |
|---|---|---|
| `--font-display` | Bricolage Grotesque | `system-ui, sans-serif` |
| `--font-ui` | Hanken Grotesk | `system-ui, sans-serif` |
| `--font-mono` | Fragment Mono | `ui-monospace, Consolas, monospace` |

Self-hosted WOFF2, `font-display: swap`, latin + latin-ext subsets only.

| Step | Size / line | Use |
|---|---|---|
| `xs` | 11 / 16 | `kbd`, counts, plan month labels, chip text |
| `sm` | 12 / 16 | Labels, metadata, toasts, tags |
| `base` | 13 / 18 | **Default UI text**: list rows, nav, buttons, inputs |
| `md` | 14 / 22 | Issue description body, comments, palette items |
| `lg` | 16 / 22 | Section titles, dialog titles |
| `xl` | 20 / 26 | Page title (display face) |
| `2xl` | 28 / 32 | Empty states, onboarding (display face) |

Weights: 400 text, 600 emphasis and group headers, 700 page titles. Display face only at `xl`+ and in the wordmark.
Mono for issue keys, hours, dates and numbers in columns, with `tabular-nums`.

### Space, radius, density, elevation

- **Spacing**: 4 px base — `1`=4, `2`=8, `3`=12, `4`=16, `6`=24, `8`=32. Lay out with `gap`, not margins.
- **Radius**: `--r1` 5 px (buttons, inputs, chips, plan cards, nav items), `--r2` 7 px (cards, popovers, palette,
  toasts), `full` for labels and avatars.
- **Density**: comfortable — `--row` 34 px (list rows, group headers), nav items 28 px, topbar 48 px, buttons 30 px.
  Compact (28 px rows, 12.5 px text) is a later per-user setting; build with `--row`, never hard-coded heights.
- **Elevation**: borders, not shadows, for resting surfaces. Shadow only on things that float: popovers and the palette
  (`0 24px 60px -12px rgb(0 0 0 / .45)`), toasts, a card being dragged.

### Motion

120 ms ease-out for hover/focus/open, 160 ms for popovers and the palette. No motion on state changes from
optimistic updates or realtime sync — the new state just appears. Under `prefers-reduced-motion: reduce` all
transitions drop to 0 ms.

## Layout — app shell

```
┌──────────┬───────────────────────────────────────────────┐
│ sidebar  │ topbar 48: crumb · view tabs · search ⌘K · New │
│ 212 px   ├───────────────────────────────────────────────┤
│ --side   │ content (scrolls)                             │
│          │                                               │
│ agents ● │                                               │
└──────────┴───────────────────────────────────────────────┘
```

- **Sidebar** (tinted `--side` in both modes): workspace + mark at top; Inbox, My issues, Delivery plan, Sprints;
  Projects list with a 10 px project colour square; "N agents working" status at the bottom. Collapses to a 52 px icon
  rail (`[`), and to an off-canvas drawer under 640 px.
- **Topbar**: breadcrumb (`Project / Issues`), segmented view tabs (List · Board · Plan), search field showing the
  `Ctrl K` hint, one primary button ("New issue", `C`). One primary button per view, max.
- **Content** owns scrolling; the shell never scrolls. Sticky group headers in lists, sticky month header in the plan.
- **Issue detail** opens as a right-hand panel (min 480 px) over list/board, full page on narrow screens.

## Components

### Status ring

14 px SVG, stroke 1.6, in the state's colour. The shape encodes the state; the name is the `aria-label`.

| State | Shape | Colour |
|---|---|---|
| Backlog | Dashed ring | `--mute` |
| Todo | Empty ring | `--ink` |
| In Progress | Ring, half filled | `--prog` |
| In Review | Ring, three-quarters filled | `--rev` |
| Done | Filled disc with check | `--ok` |
| Canceled | Ring with × | `--mute`, title struck through |

Same icon in list rows, board cards, column headers, issue detail and the palette's "Change state" item.

### Issue type tags

- **Epic**: `Epic` tag, `--rev` text on 14% `--rev` fill, before the title.
- **Feature**: `Feature` tag, `--acc-ink` on 14% fill.
- **Task**: no tag (the common case stays quiet). Bug is a label, not a type ([ADR-008](adr/ADR-008-work-item-model.md)).

Tag: `sm` text, 600 weight, 16 px high, `--r1`, 6 px gap to the title. Tag words come from the glossary in
[branding.md](branding.md#glossary).

### Labels

Pill (radius full), 19 px high, 1 px `--line` border, `xs` `--mute` text, 6 px colour dot first. `bug` always uses
`--bad`. Max 2 visible in a row, then `+N`.

### Avatars

- **Human**: 22 px circle, initials on a colour derived from the user id.
- **Agent**: 22 px **rounded square** (radius 6), `--ink` fill, `>_` in mono. Tooltip shows the agent name.
- **Unassigned**: dashed `--line` circle.

### List row

Grid: status · key (mono, `--mute`, 66 px) · title (truncates) · labels · hours (mono, right-aligned) · avatar.
Height `--row`. Hover `--sel` at half strength; selected row `--sel` plus a 2 px `--acc-ink` inset bar on the left.
Group header: `--sunk`, status icon, state name (600), count, total hours right-aligned. Done rows use `--mute` titles.

### Board card

`--panel`, 1 px `--line`, `--r2`, 10 px padding. Header: key + status ring; title; footer: labels, hours, avatar.
While dragging: −1.5° tilt, float shadow, 1.5 px `--acc-ink` outline; the drop slot is a dashed placeholder.

### Delivery plan

Structure and behaviour: [delivery-plan.md](delivery-plan.md). Visual rules:

- **Rows**: 132 px project header on `--panel` (name, colour square, "3 people from Jan"); track with a faint month
  grid (`--line` at 70%).
- **Cards (outline)**: `--panel` fill, 1.5 px border in the project colour, `--r1`, 20 px high, `sm` text.
  Progress = 2.5 px bar along the bottom edge, width = done children / total. Epics: bold title.
- **Violated dependency**: card gets a `--bad` border, a 1.5 px dashed `--bad` outline (offset 2 px) and a `!` badge;
  the connector to its blocker is a dashed `--bad` curve.
- **Milestones**: 1.5 px dashed `--acc-ink` vertical line, label at the top in `--acc-ink` bold `xs`.
- **Plan health** (no dated work, no effort, no dates): italic `--warn` text with a `!` badge, inside the empty row.
- **Capacity band (bars)**: per month two 9 px bars from a shared baseline — demand (filled; `--ok` if covered,
  `--bad` if over supply) and supply (1.5 px `--mute` outline). Tooltip and screen-reader text: "needs 4.5, has 3".

### Command palette

`Ctrl/Cmd K`. Centered, 540 px max, 44 px from the top, `--panel`, `--r2`, float shadow, dimmed backdrop. Query row
(`md`) with search icon; results with icon, label, context (e.g. sprint dates) and the shortcut `kbd`; active item
`--sel`. Footer on `--sunk` shows the equivalent CLI command (`kupro issue move FLOW-12 --sprint next`) — parity
made visible ([ADR-001](adr/ADR-001-agent-parity.md)).

### Buttons and inputs

- **Primary**: `--acc` fill, `--on-acc` text, 600, 30 px, `--r1`, optional trailing `kbd`. One per view.
- **Secondary**: `--panel` fill, 1 px `--line` border, `--ink` text. **Ghost**: no border, `--mute` text.
- **Destructive**: `--bad` text on secondary; fill only inside the confirm dialog.
- **Inputs**: 30 px, `--panel`, 1 px `--line`, focus = 2 px `--acc-ink` ring (offset 1 px). Focus ring is the same on
  every focusable element.
- **kbd**: mono `xs`, 1 px `--line` border with 2 px bottom, `--r1` 4 px.

### Toasts

Bottom right, `--ink` ground with `--bg` text (inverted), `--r2`, `sm`. Format: `[avatar] actor action · time`.
Agent actions name the surface: "triage-agent moved FLOW-139 to In Review via MCP · 2s ago". Undo link for
destructive actions. Auto-dismiss 5 s; errors stay until dismissed.

### Icons

16 px, 1.5 px stroke, round caps and joins, `currentColor`. One set (Lucide, MIT, bundled — no CDN). No emoji in the UI.

## Writing in the UI

Voice, tone and the EN/DA glossary: [branding.md](branding.md#voice-and-tone). Mechanics:
[ADR-011](adr/ADR-011-i18n.md). Design for Danish being ~30% longer: truncate titles with ellipsis, never
fixed-width buttons. Numbers, dates and hours via `Intl` in the user's locale (`4,5` in Danish; hours `8h` / `8 t`).

## Checklist for UI pull requests

- [ ] No hex, rgb or font names outside `tokens.css`.
- [ ] Checked in light **and** dark.
- [ ] Works at 400 px wide without horizontal page scroll.
- [ ] Every action reachable by keyboard and listed in the command palette.
- [ ] State/type/warnings readable without colour.
- [ ] No hardcoded strings; Danish keys added.
- [ ] No transition delays an optimistic update.
