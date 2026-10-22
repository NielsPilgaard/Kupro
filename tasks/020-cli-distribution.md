---
title: 'Task 020: CLI install script and `kupro install mcp`'
description: >-
  Publish the CLI as checksummed binaries in OVHcloud Object Storage behind a one-line install script, add
  `kupro update`, and add `kupro install mcp`, which signs in, registers Kupro in every agent client it
  finds, and installs the Kupro skill.
status: 'Todo'
phase: 1
depends_on: [005, 006, 013]
purpose: 'Implementation spec for task 020.'
---

# Task 020: CLI install script and `kupro install mcp`

## TL;DR

Decisions are in [ADR-018](../docs/adr/ADR-018-cli-distribution.md). This task builds them: a release pipeline, the
install scripts, `kupro update`, `kupro install mcp` / `uninstall mcp`, and the Kupro skill. Goal: two
commands from nothing to an agent managing issues.

## Scope

- **Release** (`.github/workflows/cli-release.yml`, on `cli-v*` tags):
  - AOT matrix: `win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`; version from the tag.
  - Upload `kupro-<rid>[.exe]` and `SHA256SUMS` to `cli/<version>/` in the public bucket, then write `cli/latest`.
  - The existing `cli` job in `ci.yml` keeps the build and cold-start check on PRs.
- **Install scripts** in the public site (`site/public/install.sh`, `site/public/install.ps1`, task 019):
  - Detect OS, architecture and musl; download; verify the checksum; install to the user directory; add to `PATH`
    on Windows and print a hint on Unix when `~/.local/bin` is not on it.
  - `KUPRO_VERSION` and `KUPRO_INSTALL_DIR` override the defaults. Print `kupro install mcp` as next step.
  - Tested in CI: run each script against the just-uploaded release on Linux, macOS and Windows, then `kupro --version`.
- **CLI**:
  - `--version`; default URL `https://app.<domain>`; `kupro login --url` stores the URL for self-hosters.
  - `kupro update`: download, verify, replace the running binary (rename-then-replace on Windows), refresh the skill.
  - `minCliVersion` in `GET /api/v1/status`; the CLI warns once per command when it is older.
  - `kupro install mcp [--client <name>] [--remote] [--no-skill] [--dry-run]` and `kupro uninstall mcp`, as
    in ADR-018: Claude Code, Claude Desktop, Cursor, VS Code, Codex. One table of client paths and commands.
  - Token refresh under a file lock on the CLI config (ADR-018 IMP-001).
- **Skill** (`src/Kupro.Cli/Skill/SKILL.md`, embedded resource):
  - Frontmatter `name: kupro`, a `description` that triggers on issues, tasks, sprints, the plan, and Kupro.
  - Body: pick the workspace; search before creating; keys like `KUP-12`; epic > feature > task, states, effort in
    hours; one bulk call for an epic and its children (`parent: "#0"`); Markdown; comments; sprints and the plan;
    reviewing meeting proposals; the CLI with `--json` when MCP is not connected.
  - Test: every tool and command it names exists (ADR-018 IMP-002).
  - Align the MCP server `instructions` (`McpSetup.Instructions`) with the skill's short version.
- **Web**: a "Connect an agent" block on the settings page, above API tokens: the install one-liner for the visitor's
  OS, `kupro install mcp`, and the remote MCP URL for manual setups. All strings in the locale files.
- **Site**: the agents section and the docs placeholder show the same two commands (copy in `site/src/i18n`).
- **Ops**: public bucket and a write-only key for CI → a manual step and the
  [deploy runbook](../docs/runbooks/deploy.md).

## Acceptance criteria

- [ ] On clean Windows, macOS and Linux machines, the install script followed by `kupro install mcp` leaves
  Claude Code able to create, update and close an issue, with one browser sign-in in total.
- [ ] `kupro install mcp --remote` works in Claude Code with its own OAuth sign-in against the production realm
  (closes the open criterion in [task 006](completed/006-mcp.md)).
- [ ] Running `install mcp` twice changes nothing the second time; `uninstall mcp` removes exactly what it added.
- [ ] `kupro update` brings an old binary to the version in `cli/latest` and refreshes the skill.
- [ ] A release whose upload fails half-way leaves `cli/latest` on the previous version.
- [ ] The skill test fails when the skill names a tool or command that does not exist.

## Out of scope

- npm, NuGet, PyPI, Homebrew, winget, Scoop ([ADR-018](../docs/adr/ADR-018-cli-distribution.md) ALT-001).
- Code signing and notarization (ADR-018 NEG-002).
- A Claude Code plugin marketplace entry.
