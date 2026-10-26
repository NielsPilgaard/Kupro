---
title: 'Task 022: Open-source readiness'
description: >-
  Make the public repo usable and contributable: AGPL-3.0 LICENSE, a CLA enforced by CLA Assistant Lite,
  CONTRIBUTING, code of conduct, security policy, issue templates, and a README with product screenshots taken from a
  scripted demo workspace.
status: 'In progress'
phase: 1
depends_on: []
purpose: 'Implementation spec for task 022.'
---

# Task 022: Open-source readiness

## TL;DR

The repo is public but has no LICENSE, so nobody may legally use or contribute to it. This task carries out
[ADR-007](../docs/adr/ADR-007-licensing.md): the AGPL-3.0 text, a CLA that keeps dual licensing possible, and the files a
contributor looks for first. The README gets screenshots from a demo workspace that a script can recreate.

## Scope

- **License**: `LICENSE` with the unmodified AGPL-3.0 text. Copyright holder: Pilgaard Development.
- **CLA**: `CLA.md`, adapted from the Apache Individual CLA, granting Pilgaard Development and its successors a
  copyright and patent license with the right to relicense. `.github/workflows/cla.yml` runs CLA Assistant Lite: a
  contributor signs by commenting on their first PR, and signatures are stored in this repo's `cla-signatures` branch.
  No new vendor: it runs on GitHub Actions ([ADR-006](../docs/adr/ADR-006-eu-vendor-policy.md) exception).
- **CONTRIBUTING.md**: dev setup (links to the README), the AGENTS.md rules by link, the `/verify` checks, Conventional
  Commits, issue first for anything bigger than a small fix, AI-assisted PRs welcome when checked and understood.
- **CODE_OF_CONDUCT.md**: Contributor Covenant 2.1.
- **SECURITY.md**: report through GitHub private vulnerability reporting.
- **Issue templates**: bug and feature forms; blank issues off; security reports routed to the advisory form.
- **Repo settings** (manual or `gh api`): private vulnerability reporting on, wiki off, discussions stay off.
- **Demo seed**: `scripts/demo-seed.ps1` creates a fictional workspace through the `kupro` CLI (projects, epics,
  features, about 20 issues, a sprint) so screenshots can be retaken when the UI changes.
- **Screenshots**: board and delivery plan, light and dark, in `docs/images/`.
- **README**: logo, pitch, screenshots, features, quickstart, connect an agent, self-hosting (possible, unsupported, a
  hosted version is planned), contributing, license.

## Acceptance criteria

- [ ] GitHub shows the repo's license as AGPL-3.0.
- [ ] A PR from an account that has not signed fails the CLA check; after the signing comment it passes, and the
  signature is committed to `cla-signatures`. The owner and bots are not asked to sign.
- [ ] The README renders on GitHub in light and dark mode with the right logo and screenshots.
- [ ] `scripts/demo-seed.ps1` against a fresh `aspire run` produces the workspace shown in the screenshots.
- [ ] GitHub's community profile (Insights → Community standards) is complete.

## Out of scope

- A supported self-hosting package (still out of v1, [product.md](../docs/product.md)).
- Trademark registration or a trademark policy.
- Moving issue tracking from GitHub Issues to a Kupro instance.
