---
title: 'Manual steps'
description: >-
  Everything only a person can do: accounts, keys, DNS, signing in to consoles, recordings and legal paperwork. Grouped by
  what each step unblocks. Agents add a row when their work needs one and remove it once you confirm it is done.
status: 'Active'
purpose: 'Single checklist of the human-only work the AI-built parts of Flowstate wait on.'
---

# Manual steps

## TL;DR

- **Now**: get a Scaleway Generative APIs key, set it locally, and record standups for the accuracy check (task 015).
- **Go-live** (task 013): domain, OVHcloud VPS + Dokploy, GitHub repo, Object Storage, Scaleway email, then the
  first-deploy checks. The details (variable names, commands) live in the [deploy runbook](runbooks/deploy.md). This
  page is the checklist and the order.
- **Before the first external user**: backups + restore drill, privacy policy, terms and data processing agreements.
- **Before charging**: Alunta + card acquirer.

Tick a box when it is done. Tell an agent when you have done one, so it can do the follow-up and update this page.

## Now — unblocks open work

| # | Step | Where | Unblocks |
|---|---|---|---|
| N1 | [ ] Create a Scaleway account and project. Create an API key with **Generative APIs** permissions only. | console.scaleway.com | 015 |
| N2 | [ ] Set the key locally: `dotnet user-secrets set Parameters:scaleway-ai-key <key>` in `src/Flowstate.AppHost`, then `aspire run`. The microphone button appears in the meeting panel. | your machine | 015: the 5-second test, Danish/English with real speech, and the extraction model spike ([ADR-015](adr/ADR-015-meeting-agent-vendors.md) IMP-006) |
| N3 | [ ] Record 10 short standups (5 Danish, 5 English) that mention issue keys and people's names. Ask the people in them for permission. Give an agent the files. It runs the Whisper vs Hviske comparison ([ADR-015](adr/ADR-015-meeting-agent-vendors.md) IMP-001). | your meetings | 015 done |
| N4 | [ ] Try the meeting agent in one real in-person meeting and one Teams meeting, and note what it missed. In Teams, use **Add call audio** in Chrome or Edge: share the Teams tab (Teams on the web) or the entire screen with system audio (Teams desktop app), and wear headphones. | your meetings | Feedback for 015; call audio is only unit-tested so far |

## Go-live — task 013

Do these in order. Each step links to the runbook section with the exact values.

| # | Step | Where |
|---|---|---|
| G1 | [ ] Pick and register the domain. The app runs on `<domain>`, with `auth.<domain>` and `grafana.<domain>` beside it. | any EU registrar (e.g. DK Hostmaster for `.dk`) |
| G2 | [ ] Create a GitHub repository and push: `git remote add origin <url>` then `git push -u origin main`. The repo has no remote yet. | github.com |
| G3 | [ ] Order an OVHcloud VPS with at least 8 GB RAM in an EU region near Denmark ([ADR-004](adr/ADR-004-hosting.md)). In the firewall, allow only ports 22, 80 and 443. | OVHcloud control panel |
| G4 | [ ] Install Dokploy on the VPS (its one-line installer over SSH), then create an admin account. | VPS |
| G5 | [ ] Point DNS A records for the three domains at the VPS ([First deploy](runbooks/deploy.md#first-deploy-once-per-environment) step 1). | registrar / DNS |
| G6 | [ ] Create an Object Storage bucket and an S3 user, then run the CORS command (runbook step 2). | OVHcloud control panel + your terminal |
| G7 | [ ] Scaleway Transactional Email: verify the domain (SPF/DKIM records in DNS) and create an API key with TEM permissions only. | console.scaleway.com |
| G8 | [ ] Create the Dokploy compose app and fill in every [environment variable](runbooks/deploy.md#environment-variables), including long random passwords and the keys from N1, G6 and G7. Add the [domains](runbooks/deploy.md#domains). Turn Autodeploy off. | Dokploy |
| G9 | [ ] Set the [GitHub configuration](runbooks/deploy.md#github-configuration): 2 repository variables, plus the `production` environment with 3 secrets. Make the GHCR packages public, or add a GHCR registry in Dokploy. | GitHub settings |
| G10 | [ ] Push to `main`, or run `cd.yml` by hand, and watch the first deploy. | GitHub Actions |
| G11 | [ ] Keycloak: create a permanent admin with 2FA, delete the bootstrap `admin`, and send a test email (runbook steps 4–5). | `https://auth.<domain>/admin` |
| G12 | [ ] Grafana: sign in, change the admin password, test the `flowstate-email` contact point. Optional: create a Viewer service account for agents (runbook step 6). | `https://grafana.<domain>` |
| G13 | [ ] PingPuffin: create an account and an HTTP check on `/api/v1/status`, with alerts by email and SMS (runbook step 7). | pingpuffin |
| G14 | [ ] Smoke test: register, reset password, create an issue, paste an image, run `flowstate login`, connect an MCP client (runbook step 8). | the app |

## Before the first external user

| # | Step | Notes |
|---|---|---|
| E1 | [ ] Backups + a restore drill you have run yourself. | Waiting on Skoleoverblikket's backup agent ([open gaps](runbooks/deploy.md#open-gaps)). Until then, losing the VPS loses all data. |
| E2 | [ ] Fill the placeholders in the draft privacy policy, terms, cookie policy and DPA ([docs/design/public-pages/](design/public-pages/index.html)), have a lawyer review them, and publish them on the domain. | Keycloak's MCP consent page links to them, and so will sign-up. Open items are listed in the drafts' Legal tab. |
| E3 | [ ] Data processing agreements with the sub-processors in the [ADR-006 register](adr/ADR-006-eu-vendor-policy.md#vendor-register): OVHcloud, Scaleway, PingPuffin. Publish the sub-processor list. | Usually accepted in each vendor's console or terms. Check with your advisor whether anything more is needed. |
| E4 | [ ] Scaleway console: set a billing alert on the project that holds the Generative APIs key, at a monthly amount you would notice (for example EUR 50). | Until [task 017](../tasks/017-meeting-agent-budget.md) ships there is no AI budget per workspace, so this is the only brake on meeting agent cost. |

## Before charging

| # | Step | Notes |
|---|---|---|
| C1 | [ ] Alunta account, plus card acquiring through OnPay or QuickPay. Not Alunta's Stripe option ([ADR-006](adr/ADR-006-eu-vendor-policy.md), [ADR-013](adr/ADR-013-seats-billing.md)). | Billing is not built yet. There is no task for it. |

## Recurring

- **Realm changes**: Keycloak reads the realm file only on its first start. When an agent changes
  `src/Flowstate.AppHost/Realms/flowstate-realm.json`, make the same change in the production admin console
  ([open gaps](runbooks/deploy.md#open-gaps)). Theme changes ship with the image.
- **Rollback**: Actions → CD → Run workflow with an older `sha-<commit>` tag.
- **Secret rotation**: Postgres, Keycloak DB and Grafana passwords apply only on first start. Rotate them as the
  [runbook](runbooks/deploy.md#environment-variables) describes, not just by editing the variable.
