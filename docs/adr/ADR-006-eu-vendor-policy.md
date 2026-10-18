---
title: 'ADR-006: EU Vendor Policy'
status: 'Accepted'
date: '2026-10-03'
authors: 'Niels'
tags: ['sovereignty', 'vendors', 'compliance']
supersedes: ''
superseded_by: ''
description: >-
  All vendors, infrastructure and data processors must be EU-headquartered and under EU jurisdiction. Open-source
  libraries and frameworks of any origin are allowed. GitHub is allowed for source hosting and CI only.
---

# ADR-006: EU Vendor Policy

## TL;DR

- Anything that processes customer data or runs production = EU company, EU jurisdiction.
- OSS libraries/frameworks: any origin (React, .NET, Keycloak, …).
- Exception: GitHub for source code + Actions (no customer data).
- Vendor register below is the source of truth; add a row before adopting a vendor.

## Status

**Accepted**

## Context

Sovereignty is a core selling point. Customers will ask for a sub-processor list; it must be short and European.

## Vendor register

| Purpose | Vendor | Country | Status |
|---|---|---|---|
| Hosting, object storage | OVHcloud | FR | Chosen |
| Identity | Keycloak (self-hosted OSS) | — | Chosen |
| Subscription billing | Alunta | DK | Chosen — card acquiring via OnPay or QuickPay (DK), **not** Alunta's Stripe option |
| Transactional email | Scaleway Transactional Email | FR | Chosen |
| Speech-to-text, LLM (meeting agent) | Scaleway Generative APIs | FR | Chosen, see [ADR-015](ADR-015-meeting-agent-vendors.md) |
| Speech-to-text, Danish (alternative) | syv.ai (Hviske) | DK | Candidate — no public streaming API; per-language routing in [ADR-015](ADR-015-meeting-agent-vendors.md) |
| Source + CI | GitHub | US | Exception (no customer data) |
| Logs, traces, metrics | Grafana, Loki, Tempo, Prometheus (self-hosted OSS) | — | Chosen, see [ADR-014](ADR-014-observability.md) |
| External uptime monitoring | PingPuffin | DK | Chosen — probes a public health endpoint only, see [ADR-014](ADR-014-observability.md) |
| Backup heartbeats (dead-man's switch) | elmah.io | DK | Chosen — receives the backup agent's health and a short status text, no customer data, see [ADR-016](ADR-016-postgres-backups.md) |

## Consequences

### Positive

- **POS-001**: Clean sovereignty story; short sub-processor list for DPAs.

### Negative

- **NEG-001**: Fewer turnkey integrations (e.g. Stripe, Sentry SaaS off-limits) — self-host or pick EU alternatives.

## Related Decisions

- [ADR-004](ADR-004-hosting.md) — hosting choice
- [ADR-013](ADR-013-seats-billing.md) — billing model using Alunta
- [ADR-014](ADR-014-observability.md) — self-hosted telemetry, PingPuffin uptime check
- [ADR-016](ADR-016-postgres-backups.md) — Postgres backups, elmah.io heartbeats
- [ADR-017](ADR-017-public-site.md) — the public site: no third-party fonts, analytics or embeds
