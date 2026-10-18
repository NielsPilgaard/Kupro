---
title: 'Product name search'
description: >-
  Running list for replacing the provisional name Flowstate: names we like, the shortlist, what was rejected and why,
  and domain/LinkedIn availability as last checked. Rename mechanics: scripts/rename-product.mjs.
status: 'Draft'
purpose: 'Keeps the name search in one place so candidates are not re-checked or re-proposed.'
---

# Product name search

## TL;DR

- **Liked so far: Cupro, Aetlan.** Neither has the bare .com; both have .app, .dev, .dk and a get/use .com.
- Brief: catchy, easy for English speakers, works in Danish, domain + LinkedIn available. One-liner idea:
  "Get ahead of yourself" / "Få et forspring" (plan years ahead).
- Plain English words and well-known myth names are taken on .com/.app/.dev/.io. Invented or ancient-root words are
  where the free names are.
- Renaming is one command, safe until go-live: `node scripts/rename-product.mjs <NewName>` (see [branding.md](branding.md#name-and-wordmark)).
- Before buying: trademark search (EUIPO eSearch, DKPTO) in classes 9 and 42. Not done for any name yet.

## Liked

| Name | Meaning | .com | .app | .dev | .io | .dk | LinkedIn | Fallback .com | Watch out |
|---|---|---|---|---|---|---|---|---|---|
| **Cupro** | Latin root of copper (*cuprum*); ties to the Kobber brand | taken | free | free | taken | free | free | getcupro, usecupro | Cupro is also a known fabric; web search for "cupro software" returns Copper CRM |
| **Aetlan** | Old Norse *ætlan*: "thought; design, plan". English kept the root as *ettle* (to intend, plan) | taken | free | free | free | free | taken | getaetlan, useaetlan | Hard for English speakers to say; wordmark could use the ligature `ætlan` |

## Shortlist (not yet liked or rejected)

| Name | Idea | Free | Taken |
|---|---|---|---|
| Foresprint | fore + sprint; sounds like Danish *forspring* | .com, .app, .dev, .io, .dk, LinkedIn | — |
| Etlan | Aetlan, easy spelling | .app, .dev, .io, .dk, LinkedIn; get/use/try-etlan.com, etlanhq.com | .com. Reads like "ETL"; close to Atlan (data catalog) |
| Aetlun | Old Norse *ætlun*, same as ætlan; Icelandic *áætlun* = plan, schedule | .com, .app, .dev, .io, .dk, LinkedIn | — |
| Aetlo | Aetlan, Trello-style ending | .app, .dev, .io, .dk, LinkedIn | .com |
| Kupro / Cupri | Cupro variants | .app, .dev, .io, .dk, LinkedIn; get/use .com | .com |
| Eirr | Old Norse *eir* = copper, brass | .app, .dev, .io, .dk, LinkedIn | .com |
| Snemma | Old Norse "early" | .app, .dev, .io, .dk, LinkedIn; get/use .com | .com |
| Forsja | Old Norse *forsjá* = foresight | .app, .dev, .io, .dk, LinkedIn | .com |
| Antevorta | Roman goddess of the future | .app, .dev, .io, .dk, LinkedIn | .com; long |

## Rejected

| Name(s) | Why |
|---|---|
| Flowstate | Intrinsic (Alphabet) sells a Flowstate developer platform; all domains taken |
| Planr, Planly | B2B SaaS owns each .com (revenue planning; social scheduling) |
| Kedgo | Hard to pronounce, not catchy |
| Holdkurs, Nordspor, Fremtakt, Fremwork and other Danish compounds | Need something English |
| Steadyship, Paceline, Underway, Forereach | Not catchy enough |
| Plain English words (brisk, tiller, keel, cadence, lodestar, headway, …) | Taken on .com, .app, .dev and .io |
| Linear-style words (planar, vertex, tangent, helix, meridian, …) | Taken everywhere |
| Myth names (Frigg, Kvasir, Sindri, Hermod, Ratatosk, Huginn, Norn, Mimir, …) | Taken everywhere; Mimir is also Grafana's |

## How availability was checked

Registry lookups, Oct 2026: RDAP for .com (Verisign) and .app/.dev (Google); whois for .dk (Punktum), .io and .eu.
LinkedIn: whether `linkedin.com/company/<name>` exists (another company could still use the name under a different
slug). "Free" means unregistered; premium pricing not checked. Re-check before buying.
