---
title: 'ADR-007: Licensing — Open-Core under AGPL-3.0'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['licensing', 'business']
supersedes: ''
superseded_by: ''
description: >-
  Core product is AGPL-3.0, copyright Pilgaard Development. Enterprise-only features (later) live in a separate
  proprietary module. Contributions require a CLA (CLA.md, signed through CLA Assistant Lite) so the owner can keep
  dual-licensing.
---

# ADR-007: Licensing — Open-Core under AGPL-3.0

## TL;DR

- This repo: AGPL-3.0.
- Enterprise features (SAML/SCIM, audit export, advanced admin) later in a separate, proprietary `ee/` module or repo.
- Copyright holder: Pilgaard Development (and its successors, e.g. a future ApS).
- External contributions require the [CLA](../../CLA.md) to preserve dual-licensing rights.

## Status

**Accepted**

## Context

Sovereignty buyers value source visibility and the ability to self-host. AGPL prevents closed SaaS forks.

## Consequences

### Positive

- **POS-001**: Trust and self-host story; community contributions possible.

### Negative

- **NEG-001**: CLA adds friction for contributors.
- **NEG-002**: Must keep a clean boundary between core and `ee/` code.

## Implementation Notes

- **IMP-001**: `LICENSE` holds the unmodified AGPL-3.0 text ([task 022](../../tasks/022-open-source-readiness.md)).
- **IMP-002**: Decide what is "enterprise" only when there are paying customers asking.
- **IMP-003**: The CLA is adapted from the Apache Individual CLA: a non-exclusive copyright and patent license to
  Pilgaard Development and its successors, including the right to relicense, so contributors keep their copyright.
- **IMP-004**: CLA Assistant Lite (`.github/workflows/cla.yml`) collects signatures as PR comments and commits them to
  the `cla-signatures` branch. It runs on GitHub Actions, so no new vendor ([ADR-006](ADR-006-eu-vendor-policy.md)).
  The upstream action is archived; it is pinned by SHA and can be replaced by a fork or a small script if it breaks.

## Related Decisions

- [ADR-006](ADR-006-eu-vendor-policy.md) — sovereignty positioning
- [ADR-013](ADR-013-seats-billing.md) — whether billing code is core or `ee/`
