---
title: 'ADR-018: CLI Distribution and MCP Setup'
status: 'Accepted'
date: '2026-10-18'
authors: 'Niels'
tags: ['cli', 'mcp', 'agents', 'distribution']
supersedes: ''
superseded_by: ''
description: >-
  The CLI ships only through a one-line install script that downloads a checksummed native binary from OVHcloud
  Object Storage; no package registries until someone asks. `kupro install mcp` signs in, registers Kupro as an
  MCP server in every agent client it finds, and installs a Kupro skill that teaches the agent how to use it.
---

# ADR-018: CLI Distribution and MCP Setup

## TL;DR

- Install: `curl -fsSL https://<domain>/install.sh | sh` or `irm https://<domain>/install.ps1 | iex`. No prerequisites.
- Binaries live in a public OVHcloud Object Storage bucket, published by CI on a `cli-v*` tag, with `SHA256SUMS`.
- `kupro install mcp`: signs in if needed, writes the MCP config for each agent client found, installs the skill.
- Default MCP transport is the local `kupro mcp` bridge (one sign-in for every client); `--remote` writes the URL.
- No npm, NuGet, PyPI, Homebrew or winget for now; each is added on demand and needs an [ADR-006](ADR-006-eu-vendor-policy.md) row.

## Status

**Accepted**

## Context

Agent parity ([ADR-001](ADR-001-agent-parity.md)) is worth little if the CLI and MCP server are hard to set up. Today
the CLI is built for three platforms in CI but published nowhere, its default URL is `localhost`, and connecting an
agent means hand-editing each client's MCP config. Setup has to be one command to install and one command to connect.

Package registries (npmjs, NuGet.org, PyPI) are US companies. They would never see customer data, but every one of
them is another channel to release, test and explain in the vendor list, for users who have not asked yet.

## Decision

- **One channel: the install script.** `install.sh` (Linux, macOS) and `install.ps1` (Windows) are served by the
  public site ([ADR-017](ADR-017-public-site.md)) at the apex domain. They detect OS and architecture (glibc or musl
  on Linux), download the matching binary, verify it against `SHA256SUMS`, and install it without admin rights to
  `~/.local/bin` or `%LOCALAPPDATA%\Programs\kupro` (added to the user `PATH`). They end by printing the next step:
  `kupro install mcp`.
- **Binaries in OVHcloud Object Storage** (already in the vendor register), in a public bucket:
  `cli/<version>/kupro-<rid>[.exe]`, `cli/<version>/SHA256SUMS`, and `cli/latest` holding the current version.
  Targets: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64` (agents in Alpine containers), `osx-arm64`.
- **Release**: a `cli-v<semver>` tag runs the AOT matrix, stamps the version, uploads binaries and checksums, and moves
  `cli/latest` last, so a half-finished release is never "latest".
- **`kupro update`** replaces the binary in place from the same bucket (checksum verified) and refreshes the
  installed skill. The CLI makes no background update checks; it warns only when `GET /api/v1/status` reports a
  `minCliVersion` above its own.
- **Default URL** is the hosted app (`https://app.<domain>`). Self-hosters run `kupro login --url <their app>` once.
- **`kupro install mcp`** (and `kupro uninstall mcp`):
  - Runs `kupro login` first when there is no session or token.
  - Detects Claude Code, Claude Desktop, Cursor, VS Code and Codex; `--client <name>` limits it to one.
  - Uses the client's own CLI when it has one (`claude mcp add-json --scope user`, `code --add-mcp`, `codex mcp add`),
    otherwise merges a `kupro` entry into the client's user-level JSON config, keeping every other entry and
    writing a `.bak` first. Re-running it is safe; `--dry-run` prints what it would change.
  - **Default transport: stdio**, `{"command": "<absolute path to kupro>", "args": ["mcp"]}`. It reuses the CLI's
    sign-in, so the user signs in once for all clients, and it works in clients without remote OAuth (Claude Desktop's
    config file, ChatGPT's Keycloak gap in [ADR-005](ADR-005-auth-keycloak.md) IMP-001).
  - `--remote` writes `https://app.<domain>/mcp` instead, and each client runs its own OAuth sign-in.
- **The Kupro skill is installed by default** (`--no-skill` opts out) into each client that reads Agent Skills
  (`SKILL.md`), at user level. It is embedded in the CLI binary, so it always matches the CLI's version. It tells the
  agent how to work with Kupro: pick the workspace, search before creating, address issues by key, create an epic
  with its children in one call, write Markdown, and fall back to the CLI with `--json` when MCP is not connected.
  Clients without skill support get the same guidance, shorter, from the MCP server's `instructions`.

## Consequences

### Positive

- **POS-001**: Two commands from nothing to an agent managing issues: the install script, then `kupro install mcp`.
- **POS-002**: Every download comes from the EU, from infrastructure we already run; the vendor list does not grow.
- **POS-003**: One release pipeline and one artifact per platform to test.
- **POS-004**: The skill ships with the binary, so agents learn the workflow without the user writing any prompt.

### Negative

- **NEG-001**: `curl | sh` puts some users off; the script is short, readable and checksums the binary, and the
  binaries can be downloaded by hand.
- **NEG-002**: Binaries are unsigned. Files fetched by `curl` or `Invoke-WebRequest` carry no quarantine flag or
  Mark of the Web, so the script path works; downloading a binary in a browser triggers Gatekeeper or SmartScreen.
- **NEG-003**: Client config locations and CLIs change; `install mcp` needs a test per client and upkeep.
- **NEG-004**: The stdio bridge is a local process per client session, and several bridges share one refresh token.

## Alternatives Considered

### npm (`npx @kupro/cli`), dotnet tool (`dnx`), PyPI (`uvx`)

- **ALT-001**: **Description**: Publish the same binaries as per-platform packages to the registries agent users know.
- **ALT-002**: **Rejection Reason**: Not rejected, deferred. Three more release targets and three US vendors for
  demand nobody has shown yet. Added one at a time when asked for, each with an ADR-006 row.

### GitHub Releases as the download host

- **ALT-003**: **Description**: Attach binaries to GitHub Releases and let the script download from there.
- **ALT-004**: **Rejection Reason**: GitHub's exception in ADR-006 covers source and CI, not delivery to customers;
  downloads from our own EU storage keep the sovereignty story whole.

### Remote MCP as the default

- **ALT-005**: **Description**: Write the remote URL into every client and let each run OAuth.
- **ALT-006**: **Rejection Reason**: One sign-in per client instead of one in total, and Claude Desktop's config file
  and ChatGPT cannot use it. Kept as `--remote`.

## Implementation Notes

- **IMP-001**: The bridges refresh the shared Keycloak session under a file lock on the CLI config, and the realm does
  not rotate refresh tokens (Keycloak's default), so concurrent bridges never invalidate each other.
- **IMP-002**: A test fails when the skill names an MCP tool or CLI command that does not exist, so it cannot drift
  from the parity set (ADR-001 IMP-001).
- **IMP-003**: Client config paths and Agent Skills locations are checked against each client's docs at
  implementation and kept in one table in the CLI source, with a test per client against a temp home directory.
- **IMP-004**: The CI job that uploads binaries holds the bucket's write key as a GitHub secret
  ([deploy runbook](../runbooks/deploy.md)).

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md) — the CLI and MCP surfaces this ADR distributes
- [ADR-004](ADR-004-hosting.md) — OVHcloud Object Storage
- [ADR-005](ADR-005-auth-keycloak.md) — device flow, remote MCP OAuth
- [ADR-006](ADR-006-eu-vendor-policy.md) — why registries wait
- [ADR-017](ADR-017-public-site.md) — the site that serves the install scripts
