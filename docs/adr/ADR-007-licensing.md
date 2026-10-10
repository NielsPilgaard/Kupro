---
title: 'ADR-007: Licensing — Open-Core under AGPL-3.0'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['licensing', 'business']
supersedes: ''
superseded_by: ''
description: >-
  Core product is AGPL-3.0. Enterprise-only features (later) live in a separate proprietary module. Contributions
  require a CLA so the owner can keep dual-licensing.
---

# ADR-007: Licensing — Open-Core under AGPL-3.0

## TL;DR

- This repo: AGPL-3.0.
- Enterprise features (SAML/SCIM, audit export, advanced admin) later in a separate, proprietary `ee/` module or repo.
- External contributions require a CLA (e.g. CLA Assistant) to preserve dual-licensing rights.

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

- **IMP-001**: Add `LICENSE` (AGPL-3.0 full text) before the repo is made public.
- **IMP-002**: Decide what is "enterprise" only when there are paying customers asking.

## Related Decisions

- [ADR-006](ADR-006-eu-vendor-policy.md) — sovereignty positioning
