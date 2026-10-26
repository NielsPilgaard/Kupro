# Contributing to Kupro

Thanks for helping. Kupro is a small project with one maintainer, so this page explains how to make a change that
can be merged quickly.

## Before you start

- **Small fixes** (bugs, typos, docs, a missing translation): open a pull request directly.
- **Anything bigger** (a new feature, a new endpoint, a change in behavior): [open an issue](https://github.com/NielsPilgaard/Kupro/issues/new/choose)
  first and wait for a yes. Kupro cuts features before they cause confusion ([product principles](docs/product.md#principles-in-priority-order)),
  and the roadmap lives in [tasks/INDEX.md](tasks/INDEX.md). An issue first saves you from building something that
  won't be merged.
- **Security issues**: never in public. See [SECURITY.md](SECURITY.md).

## Set up

Follow the [Quickstart](README.md#quickstart): .NET 10 SDK, Bun, Docker, then `dotnet tool restore` and
`dotnet aspire run`. The dev login is `dev@kupro.local` / `dev`.

To run the checks you also need PowerShell 7 (`pwsh`) and [ryni](https://github.com/computerlovetech/ryni) for the
Markdown link check (`scripts/verify.ps1` prints the install command when it's missing).

## The rules

[AGENTS.md](AGENTS.md) holds the project rules, for humans and coding agents alike. The ones reviews check most:

- **Parity**: every API operation also has a CLI command and an MCP tool ([ADR-001](docs/adr/ADR-001-agent-parity.md)).
  A CI test fails when one is missing.
- **Thin adapters**: controllers, MCP tools and CLI commands contain no business rules. Feature services do.
- **Fast**: every action stays under 500 ms p99 ([ADR-010](docs/adr/ADR-010-performance-budget.md)). Paginate, index,
  no N+1.
- **Tenancy**: every tenant-owned entity has a `WorkspaceId` ([ADR-009](docs/adr/ADR-009-multi-tenancy.md)).
- **UI**: no hardcoded strings, English and Danish both ([ADR-011](docs/adr/ADR-011-i18n.md)); design tokens only,
  never raw colors or fonts ([design guide](docs/design-guide.md)).
- **EU only**: a new vendor or SaaS needs a row in the [ADR-006](docs/adr/ADR-006-eu-vendor-policy.md) register.
  Libraries of any origin are fine.
- **Decisions live in ADRs** ([index](docs/adr/INDEX.md)). If your change needs a new decision, propose it in the issue.

## Check your change

```sh
pwsh scripts/verify.ps1
```

This runs what CI runs: web lint, typecheck and tests, the i18n guard, `dotnet format`, the .NET build with warnings as
errors, the integration tests (need Docker), and the Markdown link check. It fixes formatting by default. A PR is ready
when it exits 0.

End-to-end tests need the stack running: `cd tests/e2e && bun install && bunx playwright test`.

If you change an API endpoint, build once and commit the regenerated `openapi/kupro.json`. If you change the AppHost,
run `dotnet aspire publish -o deploy` and commit `deploy/`. CI fails on drift in either.

## Pull requests

- One logical change per PR. Don't reformat code you didn't change.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org): `feat(issues): bulk move`,
  `fix(cli): ...`, `docs: ...`.
- Describe what changed and why, and how you tested it. Screenshots for UI changes, both light and dark.
- Add or update tests: integration tests in `tests/Kupro.Tests`, web tests (`*.test.ts(x)`) next to the code they test.

## AI-assisted contributions

Welcome. The repo is built to be worked on by agents: [AGENTS.md](AGENTS.md) and the skills in `.claude/skills` give
them the rules. Two conditions: you've run the checks above, and you understand every line you submit and can answer
review questions about it. You are responsible for the code as if you had typed it.

## Contributor License Agreement

Before your first PR can be merged, you sign the [CLA](CLA.md). The CLA bot comments on your PR with a sentence to post
as a comment, and that's it: one signature covers all your future contributions.

The CLA lets Pilgaard Development license Kupro under other terms besides the AGPL-3.0, for example for a hosted
version. You keep the copyright in your work, and your contribution stays available under the AGPL-3.0.

## Conduct

Everyone in this project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
