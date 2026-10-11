---
name: verify
description: "Run all local validation checks for Flowstate before declaring work done. USE THIS SKILL when the user says 'verify', 'check everything', 'validate', 'is this ready', 'does it compile', 'are there errors', or similar, and after every code change before committing. Runs: web lint, web build/typecheck, web *:check guards (i18n), web tests, dotnet format, dotnet build (warnings as errors), .NET integration tests, and ryni (Markdown links + skill metadata)."
---

# Verify Skill

Runs all checks matching CI via `scripts/verify.ps1`.

## Run

```powershell
# Check everything (auto-fixes formatting/lint by default)
pwsh scripts/verify.ps1

# Report violations instead of fixing
pwsh scripts/verify.ps1 -NoFix

# Quick compile check: no .NET integration tests, no web tests
pwsh scripts/verify.ps1 -SkipTests

# Only one side
pwsh scripts/verify.ps1 -SkipWeb
pwsh scripts/verify.ps1 -SkipDotnet -SkipDocs
```

The script runs all steps and **collects all errors before exiting** — you see every failure at once, not just the first one.

## Steps

1. **bun install** — `--frozen-lockfile` in `web/`
2. **Web lint** — `bun run lint:fix` (default) or `bun run lint` (`-NoFix`, or no `lint:fix` script)
3. **Web build** — `bun run build` (typecheck + codegen via `prebuild`); falls back to `typecheck`
4. **Web guards** — every `*:check` script, e.g. `bun run i18n:check` (no hardcoded UI strings, ADR-011). Add new static guards as `<name>:check` scripts and they run here automatically.
5. **Web tests** — `bun run test` (skipped with `-SkipTests`)
6. **dotnet format** — on `Flowstate.slnx`; auto-fixes by default, output suppressed. Use `-NoFix` to see violations.
7. **dotnet build** — Release, `-p:CI=true`. `Directory.Build.props` sets `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, so analyzer violations fail here. Some `.editorconfig` rules (e.g. naming, IDE1006) only surface in `dotnet format`, which is why both run.
8. **.NET tests** — xUnit v3 + Testcontainers (skipped with `-SkipTests`; needs Docker)
9. **ryni** — broken local Markdown links and invalid `SKILL.md` metadata across `docs/`, `tasks/`, `.claude/` (skipped with `-SkipDocs`). Checks a temp mirror of non-gitignored files, so `node_modules` and build output are ignored.

Web steps run only the `package.json` scripts that exist and show as `SKIP` otherwise, so the script works before `web/` is scaffolded.

## Rules

- Run `verify.ps1` after every code change, before committing or declaring done
- Default is auto-fix; pass `-NoFix` only to inspect violations without changing files
- Do not report work as done until the script exits 0
- Read the summary at the end, not the intermediate output
- `SKIP` is not `PASS`: if a step you expected to run was skipped, say so
- If Docker is not running, the .NET tests step says so — report it as an environment problem, not a code bug
- Missing `bun` or `ryni` fails the step with an install hint; report it rather than skipping the step
