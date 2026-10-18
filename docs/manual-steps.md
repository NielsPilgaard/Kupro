---
title: 'Manual steps'
description: >-
  Everything only a person can do: accounts, keys, DNS, signing in to consoles, recordings and legal paperwork. Grouped by
  what each step unblocks. Agents add a row when their work needs one and remove it once you confirm it is done.
status: 'Active'
purpose: 'Single checklist of the human-only work the AI-built parts of Kupro wait on.'
---

# Manual steps

## TL;DR

- **Now**: record standups for the accuracy check and try the meeting agent in real meetings (task 015).
- **Go-live** (task 013): domain, OVHcloud VPS + Dokploy, GitHub repo, Object Storage, Scaleway email, then the
  first-deploy checks. The details (variable names, commands) live in the [deploy runbook](runbooks/deploy.md). This
  page is the checklist and the order.
- **Before the first external user**: a restore rehearsed on the VPS, privacy policy, terms and data processing agreements.
- **Before charging**: Alunta + card acquirer.

Tick a box when it is done. Tell an agent when you have done one, so it can do the follow-up and update this page.

## Now — unblocks open work

| # | Step | Where | Unblocks |
|---|---|---|---|
| N3 | [ ] Record 10 short standups (5 Danish, 5 English) that mention issue keys and people's names. Ask the people in them for permission. Give an agent the files. It runs the Whisper vs Hviske comparison ([ADR-015](adr/ADR-015-meeting-agent-vendors.md) IMP-001). | your meetings | 015 done |
| N4 | [ ] Try the meeting agent in one real in-person meeting and one Teams meeting, and note what it missed. In Teams, use **Add call audio** in Chrome or Edge: share the Teams tab (Teams on the web) or the entire screen with system audio (Teams desktop app), and wear headphones. | your meetings | Feedback for 015; call audio is only unit-tested so far |

## Go-live — task 013

Do these in order. Each step links to the runbook section with the exact values.

| # | Step | Where |
|---|---|---|
| G0 | [ ] Secure the Kupro name before anyone else does ([naming.md](naming.md)): register `kupro.app`, `kupro.dk`, `kupro.dev` and `usekupro.com` (or `getkupro.com`); claim `linkedin.com/company/kupro`; run a trademark search for "Kupro" in classes 9 and 42 (EUIPO eSearch, DKPTO). Tell an agent which domain is primary so it can fill in G1. | registrar, LinkedIn, euipo.europa.eu |
| G1 | [ ] Pick and register the domain. The public site runs on `<domain>` (and `www.`), the app on `app.<domain>`, with `auth.<domain>` and `grafana.<domain>` beside it ([ADR-017](adr/ADR-017-public-site.md)). | any EU registrar (e.g. DK Hostmaster for `.dk`) |
| G2 | [ ] Create a GitHub repository and push: `git remote add origin <url>` then `git push -u origin main`. The repo has no remote yet. | github.com |
| G3 | [ ] Order an OVHcloud VPS with at least 8 GB RAM in an EU region near Denmark ([ADR-004](adr/ADR-004-hosting.md)). In the firewall, allow only ports 22, 80 and 443. | OVHcloud control panel |
| G4 | [ ] Install Dokploy on the VPS (its one-line installer over SSH), then create an admin account. | VPS |
| G5 | [ ] Point DNS A records for the apex, `www`, `app`, `auth` and `grafana` at the VPS ([First deploy](runbooks/deploy.md#first-deploy-once-per-environment) step 1). | registrar / DNS |
| G6 | [ ] Create an Object Storage bucket and an S3 user, then run the CORS command (runbook step 2). | OVHcloud control panel + your terminal |
| G7 | [ ] Scaleway Transactional Email: verify the domain (SPF/DKIM records in DNS) and create an API key with TEM permissions only. | console.scaleway.com |
| G7b | [ ] Backups ([runbook](runbooks/deploy.md#backups-once-per-environment)): two more buckets with their own S3 users (backup repo, ops), a long random cipher pass, all three stored in your password manager; an elmah.io log, API key and four heartbeats; then create the five `kupro-*` data volumes on the VPS over SSH. The `BACKUP_*` and `ELMAHIO_*` values go into G8. | OVHcloud control panel, elmah.io, password manager, VPS |
| G8 | [ ] Create the Dokploy compose app and fill in every [environment variable](runbooks/deploy.md#environment-variables), including long random passwords, the Scaleway Generative APIs key (`SCALEWAY_AI_KEY`) and the keys from G6 and G7. Add the [domains](runbooks/deploy.md#domains). Turn Autodeploy off. | Dokploy |
| G9 | [ ] Set the [GitHub configuration](runbooks/deploy.md#github-configuration): 3 repository variables (`KEYCLOAK_PUBLIC_URL`, `APP_URL`, `SITE_URL`), plus the `production` environment with 3 secrets. Make the GHCR packages public, or add a GHCR registry in Dokploy. | GitHub settings |
| G10 | [ ] Push to `main`, or run `cd.yml` by hand, and watch the first deploy. | GitHub Actions |
| G11 | [ ] Keycloak: create a permanent admin with 2FA, delete the bootstrap `admin`, and send a test email (runbook steps 4–5). | `https://auth.<domain>/admin` |
| G12 | [ ] Grafana: sign in, change the admin password, test the `kupro-email` contact point. Optional: create a Viewer service account for agents (runbook step 6). | `https://grafana.<domain>` |
| G13 | [ ] PingPuffin: create an account and an HTTP check on `/api/v1/status`, with alerts by email and SMS (runbook step 7). | pingpuffin |
| G14 | [ ] Smoke test: register, reset password, create an issue, paste an image, run `kupro login`, connect an MCP client (runbook step 8). | the app |

## Before the first external user

| # | Step | Notes |
|---|---|---|
| E1 | [ ] Rehearse a restore on the VPS through the backup console: restore to a few minutes ago, go live, restart, check the app, switch back ([restore runbook](runbooks/restore.md)). Log the measured time on the console's Drills page as the first manual drill. | Backups run from the first deploy (G7b, runbook step 10); so far the restore path is only tested under `aspire run`. The console asks for a manual drill every quarter. |
| E2 | [ ] Fill the placeholders in the privacy policy, terms, cookie policy and DPA (now in `site/src/legal/en/`, plus the company, CVR and contact placeholders in `site/src/i18n/`), have a lawyer review them, and add the reviewed Danish text in `site/src/legal/da/`. | Keycloak's MCP consent page links to them, and so will sign-up. Open items are listed in the drafts' Legal tab. |
| E3 | [ ] Data processing agreements with the sub-processors in the [ADR-006 register](adr/ADR-006-eu-vendor-policy.md#vendor-register): OVHcloud, Scaleway, PingPuffin. Publish the sub-processor list. | Usually accepted in each vendor's console or terms. Check with your advisor whether anything more is needed. |
| E4 | [ ] Scaleway console: set a billing alert on the project that holds the Generative APIs key, at a monthly amount you would notice (for example EUR 50). | Until [task 017](../tasks/017-meeting-agent-budget.md) ships there is no AI budget per workspace, so this is the only brake on meeting agent cost. |

## Before charging

| # | Step | Notes |
|---|---|---|
| C1 | [ ] Alunta account, plus card acquiring through OnPay or QuickPay. Not Alunta's Stripe option ([ADR-006](adr/ADR-006-eu-vendor-policy.md), [ADR-013](adr/ADR-013-seats-billing.md)). | Billing is not built yet. There is no task for it. |

## Recurring

- **Realm changes**: Keycloak reads the realm file only on its first start. When an agent changes
  `src/Kupro.AppHost/Realms/kupro-realm.json`, make the same change in the production admin console
  ([open gaps](runbooks/deploy.md#open-gaps)). Theme changes ship with the image.
- **Rollback**: Actions → CD → Run workflow with an older `sha-<commit>` tag.
- **Secret rotation**: Postgres, Keycloak DB and Grafana passwords apply only on first start. Rotate them as the
  [runbook](runbooks/deploy.md#environment-variables) describes, not just by editing the variable.
