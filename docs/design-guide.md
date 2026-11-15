---
title: 'Kupro — Design Guide'
description: >-
  The UI rulebook for the web app: Kobber design tokens (colour in light and dark, type, spacing, radius, density),
  app-shell layout, and the spec for every shared component — status ring, type tags, labels, avatars, list rows,
  board cards, delivery-plan cards, capacity band, command palette, toasts, and flow patterns (stepper, step footer,
  live rail, option cards, data table, chips, progress, trees, drawer). Brand, voice and glossary live in branding.md.
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
4. **Agents are visible.** Agent actions look different from humans, and say which surface they used.
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
| `--bad` | `#B3393B` | `#EE6E6E` | Errors, capacity deficit, card late against its blocker |

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
- **Large screens**: from `3xl` (1920 px) the sidebar widens (`--sidebar` 252 px, `--nav-item` 34 px, `md` text);
  centred form pages (onboarding, new project) step up from `2xl`. Lists and boards keep their density.
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
- **Issue detail** opens as a right-hand panel (min 480 px) over list/board, full page on narrow screens. At 560 px:
  title, a row of property chips (status, priority, assignee, labels, type), a planning strip (sprint, dates,
  estimate) and tabs (description, subtasks, relations, activity). From `3xl`, or widened from its header, it grows to
  ~920 px: content on the left, every property in a 288 px rail on the right, no tabs.

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
- **Placeholder** (an imported person without a seat, [ADR-019](adr/ADR-019-import.md)): 22 px circle, 1 px dashed
  `--mute` border, initials in `--mute`, no fill. Tooltip and `aria-label`: "Name, placeholder". It always has
  initials; Unassigned never does.

### List row

Grid: status · key (mono, `--mute`, 66 px) · title (truncates) · labels · hours (mono, right-aligned) · avatar.
Height `--row`. Hover `--sel` at half strength; selected row `--sel` plus a 2 px `--acc-ink` inset bar on the left.
Group header: `--sunk`, status icon, state name (600), count, total hours right-aligned. Done rows use `--mute` titles.

### Board card

`--panel`, 1 px `--line`, `--r2`, 10 px padding. Header: key + status ring; title; footer: labels, hours, avatar.
While dragging: −1.5° tilt, float shadow, 1.5 px `--acc-ink` outline; the drop slot is a dashed placeholder.

### Delivery plan

Structure and behaviour: [delivery-plan.md](delivery-plan.md). Visual rules:

- **Rows**: 300 px label column. Project row on `--sunk` (name, colour square, "3 people from Jan", health) with the
  capacity band in its track; epic rows (chevron, key, title, feature count, focus button on hover) and feature rows
  (indented, state ring, key, title, effort) 30 px high on `--panel`. Tracks have a faint month grid (`--line` at 70%).
- **Cards (outline)**: `--panel` fill, 1.5 px border in the project colour, `--r1`, 20 px high, `sm` text.
  Progress = 2.5 px bar along the bottom edge, width = done children / total. Epics: bold title.
- **Late against its blocker**: card gets a `--bad` border, a 1.5 px dashed `--bad` outline (offset 2 px) and a `!`
  badge. No connector lines.
- **Milestones**: 1.5 px dashed `--acc-ink` vertical line, label at the top in `--acc-ink` bold `xs`.
- **Plan health** (no dated work, no effort, no dates): italic `--warn` text with a `!` badge, inside the empty row.
- **Capacity band (bars)**: per month two 9 px bars from a shared baseline — demand (filled; `--ok` if covered,
  `--bad` if over supply) and supply (1.5 px `--mute` outline). Tooltip and screen-reader text: "needs 4.5, has 3".

### Burndown

Close sprint page and sprint report ([ADR-020](adr/ADR-020-sprint-close-burndown.md)). Hand-rolled SVG like the delivery
plan, no chart dependency. Each chart has a screen-reader table behind it.

- **Remaining**: 2 px `--acc-ink` step line; a point is the value at the end of its Europe/Copenhagen day.
- **Scope**: 1.5 px dashed `--mute` step line. **Ideal**: 1 px `--mute` at 60% straight line from baseline scope to 0 over
  the sprint's weekdays.
- **Weekends**: `--sunk` bands. **End date** on a sprint that ran late: 1 px dashed `--mute` vertical.
- **Per assignee**: small multiples, 240 px minimum, all on one y scale so heights compare, followed by a table (at start,
  added, completed, remaining, carried) sorted by name with Unassigned last. No ranking.
- **Outcome control**: segmented, as the view tabs. Split is disabled with a tooltip for issues without effort or with
  children. Its hours-left input starts empty and takes focus.

### Command palette

`Ctrl/Cmd K`. Centered, 540 px max, 44 px from the top, `--panel`, `--r2`, float shadow, dimmed backdrop. Query row
(`md`) with search icon; results with icon, label, context (e.g. sprint dates) and the shortcut `kbd`; active item
`--sel`. Footer on `--sunk` shows the equivalent CLI command (`kupro issue move KUP-12 --sprint next`) — parity
made visible ([ADR-001](adr/ADR-001-agent-parity.md)).

### Buttons and inputs

- **Primary**: `--acc` fill, `--on-acc` text, 600, 30 px, `--r1`, optional trailing `kbd`. One per view. In a view
  with a side rail (the import wizard's plan), the primary sits in the main column's footer; the rail's actions are
  secondary or ghost.
- **Secondary**: `--panel` fill, 1 px `--line` border, `--ink` text. **Ghost**: no border, `--mute` text.
- **Destructive**: `--bad` text on secondary; fill only inside the confirm dialog.
- **Inputs**: 30 px, `--panel`, 1 px `--line`, focus = 2 px `--acc-ink` ring (offset 1 px). Focus ring is the same on
  every focusable element.
- **kbd**: mono `xs`, 1 px `--line` border with 2 px bottom, `--r1` 4 px.

### Toasts

Bottom right, `--ink` ground with `--bg` text (inverted), `--r2`, `sm`. Format: `[avatar] actor action · time`.
Agent actions name the surface: "triage-agent moved KUP-139 to In Review via MCP · 2s ago". Undo link for
destructive actions. Auto-dismiss 5 s; errors stay until dismissed.

### Icons

16 px, 1.5 px stroke, round caps and joins, `currentColor`. One set (Lucide, MIT, bundled — no CDN). No emoji in the UI.

**Product marks** (import sources) are the exception: each vendor's official logo file, bundled as an SVG asset,
used as their brand rules allow, never redrawn or recoloured. Their own colours are the one place hex lives outside
`tokens.css`, and only inside the asset file. Shown in a 30 px tile (`--panel`, 1 px `--line`, `--r1`, mark 18 px),
40 px (`--r2`, mark 24 px) as a heading mark. Formats without a vendor (CSV, snapshot, Kupro export) use a Lucide
icon in the same tile.

## Flows and settings

Patterns for multi-step setup flows and the screens that manage them afterwards. First used by import
([task 023](../tasks/023-import-framework.md)); reuse them before inventing new ones.

### Split pane with live rail

Main column `minmax(0, 1fr)` plus a 340 px rail on `--panel` with a 1 px `--line` left border. The rail shows the
result of the choices being made (the import's dry-run plan) and updates in place, with `aria-live="polite"`. Below
900 px the rail stacks under the main column and the step footer becomes sticky at the bottom.

**Key–value with change**: three columns (label, value in mono right-aligned, change since the last edit in mono `xs`
`--acc-ink`, `+12` / `−3`, empty when unchanged). Counts that wait on a decision show `—`, not 0.

**Open decisions box**: `--r2`, 1 px border of `--warn` mixed 45% into `--line`, fill `--warn` 8% on `--panel`.
Title "Needs you before import" (600), then one underlined link per open decision that jumps to its step. When none
are open: a `✓` line in `--ok`.

### Stepper

A row of numbered steps above the main column, on `--bg` with a `--line` bottom border; scrolls sideways, never
wraps. Each step is a pill button: a 20 px numbered circle (mono `xs` 600, 1.5 px border) and the label. Steps join
with a 14 px `--line` rule, `--ok` after a done step.

| Step | Circle | Label |
|---|---|---|
| Current | `--acc` fill, `--on-acc` number | `--ink` 600 on `--sel`, `aria-current="step"` |
| Done | `✓` in `--ok`, `--ok` border | `--ink` |
| Needs a decision | `!`, `--warn` border, `--warn` 18% fill | `--ink` |
| Not yet seen | number, `--line` border | `--mute` |
| Locked (e.g. before connect) | as not seen, 55% opacity | disabled |

The accessible name says the status: "Step 3, Hierarchy, needs a decision". Any step that isn't locked can be opened.

### Step footer

Bottom of the main column, `--panel`, `--line` top border, 12 × 20 px padding. Left to right: **Back** (secondary),
the message block, a `Ctrl Enter` `kbd` hint (hidden below 900 px), then the primary named after where it goes
("Continue to States →"; on the last step the action itself: "Import 4,812 issues").

- **Message block**: `xs` `--mute` "Step 3 of 9 · Hierarchy", then either "**Next: States** · its hint" or, when the
  step blocks, why: a `!` line ("Pick one of the two options to continue. There is no default."). On the last step,
  the open decisions as links, or "Ready. This is exactly what the import writes."
- **Blocked**: the primary is disabled and the reason is always visible next to it, never only in a tooltip.
- **Progress**: a 2 px `--acc` line along the footer's top edge, width = done steps / all steps.
- **Unblocked**: when a change clears the block, the primary pulses twice (a fading `--acc` ring, 900 ms); none with
  reduced motion. Focus does not move.
- `Ctrl/Cmd Enter` presses the primary when it is enabled.

### Option cards

For a choice with no default where each option needs a short explanation (import's hierarchy fold). A `fieldset`
with a 14 px 600 `legend`; options in equal columns (two up, one column below 720 px), never ranked or pre-selected.
Each card is a `label` around a native radio: `--panel`, 1 px `--line`, `--r2`, 12 px padding. Hover: `--acc-ink`
border. Checked: `--acc-ink` border plus a 1 px inset `--acc-ink` ring. Keyboard focus shows the focus ring on the
card.

### Source cards

A grid (`auto-fill`, 220 px minimum, 8 px gap) of toggle buttons (`aria-pressed`): product mark, name (600), one `xs`
`--mute` line. Selected: as a checked option card plus `--sel` fill. **Not available yet**: disabled, `--sunk` fill,
dashed border, greyscale mark at 50%, name in `--mute`, the line "Not available yet" and a `WIP` chip at the right.
They stay visible so people see what's coming; they can't be picked.

### Data table

Inside a `--panel` card (1 px `--line`, `--r2`, clipped). Header row 30 px on `--sunk`, `sm` 600 `--mute`, no
wrapping. Body rows `--row` high, `--line` between rows, none after the last. Numbers in mono, right-aligned, header
too. Hover: `--acc` at 6%. Inline controls (selects, checkboxes, key inputs) sit in the cell at full control height.

### Status chips

20 px, `--r1`, `xs` 600, a symbol first so they read without colour. Fills are the colour at 14%.

| Chip | Symbol | Text / fill |
|---|---|---|
| OK | `✓` in `--ok` | `--ink` on `--ok` |
| Warning | `!` in `--warn` | `--ink` on `--warn` |
| Error | `×` | `--bad` on `--bad` |
| Neutral | none | `--mute` on `--mute` |
| Accent | none | `--acc-ink` on `--sel` |
| WIP | none | `--mute`, no fill, 1 px `--line` border, wide tracking |

**Inline lines**: a warning line starts with a 14 px `--warn` square holding `!` in `--panel`; an OK line starts with
`✓` in `--ok`. Text stays `--ink`.

### Progress

- **Bar**: 6 px, `--sunk` track, `--acc` fill, radius 3 px. Count beside it in mono ("1,204 / 4,812").
- **Phases**: a list under the bar, 8 px dots: done = `--ok` filled, current = `--acc` filled and the text 600, next =
  `--line` ring and `--mute` text.
- **Distribution**: one 10 px stacked bar, 1 px gaps, segments in each state's colour, `role="img"` with a label, and
  a legend under it with the status ring, name and count, so it reads without colour.

### Hierarchy trees

Two panes side by side (from source / in Kupro) with a 112 px mapping column between them; one column below a
740 px container width. Pane: `--panel` card, 46 px `--sunk` header with the product mark, name and level count.
Nodes are `--row` high: indent guides (1 px `--line`, 14 px per level), status ring, key (mono `--mute`, 66 px), the
source's level as an outlined tag (1 px `--line`, `xs` 600 `--mute`) or Kupro's type tag, title, and an optional `xs`
note. A note that marks a changed parent or type gets a `--warn` 14% fill. Hover or focus on a node highlights its
twin in the other pane (`--sel` plus a 2 px `--acc-ink` inset bar). The mapping column lists `Level → Type`; rows
that fold are `--warn` 600.

### Drop zone

1.5 px dashed `--line`, `--r2`, `--sunk` fill, centred `--mute` text with a small secondary "choose a file" button.

### Connection list

The list of imports after the first run. One row per import: product mark, name and source, last run, a sparkline,
actions. Row hover `--acc` at 5%; the open row as a selected list row. Below 960 px only mark and name stay.

**Sparkline**: 30 px high, one 9 px bar per run, 2 px apart; each bar stacks created (`--acc`), updated (`--mute`
55%) and failed (`--bad`) items. Tooltip and screen-reader text give the numbers ("Run 4: 12 created, 380 updated, 0
failed").

### Drawer

For a focused task on top of a page (a new import, resolving a conflict). Fixed to the right, `min(480px, 100%)`
wide, `--panel`, 1 px `--line` left border, float shadow, over a `--scrim` backdrop. Header `--topbar` high (title,
close button), scrolling body with 16 px padding, footer with the actions (primary first). `Esc` and the backdrop
close it; focus is trapped inside and returns to the opener.

### Section tabs

Tabs inside a page or panel (issue detail, an import's runs and conflicts): `role="tablist"`, 36 px buttons, a 2 px
`--acc-ink` bottom border and 600 on the selected one, `--mute` on the rest. A count follows the label in `xs`
`--mute`. View switching in the topbar uses the segmented control instead.

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
