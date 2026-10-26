#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run all CI checks locally. Collect errors from all steps before exiting.
.PARAMETER NoFix
    Report formatting/lint violations instead of auto-fixing them (default is to fix).
.PARAMETER SkipTests
    Skip .NET integration tests and web unit tests (quick compile check).
.PARAMETER SkipWeb
    Skip all web/ and site/ steps (lint, typecheck/build, tests).
.PARAMETER SkipDotnet
    Skip all dotnet steps (format, build, tests).
.PARAMETER SkipDocs
    Skip the ryni check (broken Markdown links, SKILL.md metadata).
.PARAMETER FormatAll
    Run dotnet format on the whole solution before the build, not only on files the build reports.
#>
param(
    [switch]$NoFix,
    [switch]$SkipTests,
    [switch]$SkipWeb,
    [switch]$SkipDotnet,
    [switch]$SkipDocs,
    [switch]$FormatAll
)

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Solution = Join-Path $RepoRoot 'Kupro.slnx'
$WebDir = Join-Path $RepoRoot 'web'
$Errors = @()
$StepResults = @()

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n==> $name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $output = (& $body 2>&1 | Out-String).TrimEnd()
    $exit = $LASTEXITCODE
    $took = Took $timer
    if ($exit -ne 0) {
        $script:Errors += "FAIL [$name]`n$output"
        $script:StepResults += @{ Name = $name; Pass = $false; Took = $took }
        Write-Host $output
        Write-Host "FAIL: $name ($took)" -ForegroundColor Red
    } else {
        $script:StepResults += @{ Name = $name; Pass = $true; Took = $took }
        Write-Host "PASS: $name ($took)" -ForegroundColor Green
    }
}

function Took([Diagnostics.Stopwatch]$timer) { '{0:N0}s' -f $timer.Elapsed.TotalSeconds }

function Skip([string]$name, [string]$reason) {
    Write-Host "`n==> $name" -ForegroundColor Cyan
    Write-Host "SKIP: $reason" -ForegroundColor Yellow
    $script:StepResults += @{ Name = $name; Pass = $null; Reason = $reason }
}

function Fail([string]$message) {
    $message
    $global:LASTEXITCODE = 1
}

Push-Location $RepoRoot

# ── Web (Bun, ADR-003) ──────────────────────────────────────────────────────
# Runs whichever package.json scripts exist, so this keeps working while web/ is scaffolded.

if (-not $SkipWeb) {
    $pkgPath = Join-Path $WebDir 'package.json'
    if (-not (Test-Path $pkgPath)) {
        Skip "Web" "web/package.json not found"
    } elseif (-not (Get-Command bun -ErrorAction SilentlyContinue)) {
        Step "Web" { Fail "bun is not installed. See https://bun.sh/docs/installation" }
    } else {
        $scripts = (Get-Content $pkgPath -Raw | ConvertFrom-Json).scripts
        $has = { param($n) $null -ne $scripts -and $scripts.PSObject.Properties.Name -contains $n }

        Step "bun install" {
            Set-Location $WebDir
            bun install --frozen-lockfile
        }

        if (-not $NoFix -and (& $has 'lint:fix')) {
            Step "Web lint (auto-fix)" { Set-Location $WebDir; bun run lint:fix }
        } elseif (& $has 'lint') {
            Step "Web lint" { Set-Location $WebDir; bun run lint }
        } else {
            Skip "Web lint" "no 'lint' script in web/package.json"
        }

        # `build` typechecks (tsc -b) and also runs codegen via `prebuild`.
        if (& $has 'build') {
            Step "Web build" { Set-Location $WebDir; bun run build }
        } elseif (& $has 'typecheck') {
            Step "Web typecheck" { Set-Location $WebDir; bun run typecheck }
        } else {
            Skip "Web build" "no 'build' or 'typecheck' script in web/package.json"
        }

        # Repo guards such as i18n:check (ADR-011): every "<name>:check" script runs.
        foreach ($check in @($scripts.PSObject.Properties.Name | Where-Object { $_ -like '*:check' })) {
            Step "Web $check" { Set-Location $WebDir; bun run $check }.GetNewClosure()
        }

        if ($SkipTests) {
            Skip "Web tests" "-SkipTests"
        } elseif (& $has 'test') {
            Step "Web tests" { Set-Location $WebDir; bun run test }
        } else {
            Skip "Web tests" "no 'test' script in web/package.json"
        }
    }

    # Public site (ADR-017): `build` is astro check + astro build.
    $SiteDir = Join-Path $RepoRoot 'site'
    if ((Test-Path (Join-Path $SiteDir 'package.json')) -and (Get-Command bun -ErrorAction SilentlyContinue)) {
        Step "Site install" { Set-Location $SiteDir; bun install --frozen-lockfile }
        Step "Site build" { Set-Location $SiteDir; bun run build }
    }
}

# ── .NET ────────────────────────────────────────────────────────────────────

if (-not $SkipDotnet) {
    # dotnet format needs 15–30 s to load the solution, even for one file. The build already enforces everything it
    # checks (analyzers, code style, IDE0055 formatting, IDE1006 naming), so format runs only to fix what the build
    # reports, on just those files, followed by one rebuild. -FormatAll runs it on the whole solution first.
    if ($FormatAll) {
        if ($NoFix) {
            Step "dotnet format" { Set-Location $RepoRoot; dotnet format $Solution --verify-no-changes }
        } else {
            Step "dotnet format (auto-fix)" { Set-Location $RepoRoot; dotnet format $Solution }
        }
    }

    # TreatWarningsAsErrors + analyzers + EnforceCodeStyleInBuild come from Directory.Build.props.
    Step "dotnet build" {
        Set-Location $RepoRoot
        $out = dotnet build $Solution --configuration Release -p:CI=true 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -and -not $NoFix) {
            $files = @([regex]::Matches($out, '(?m)^\s*(\S.*?\.cs)\(\d+,\d+\): error (?:IDE|CA)\d+') |
                ForEach-Object { [IO.Path]::GetRelativePath($RepoRoot, $_.Groups[1].Value) } | Sort-Object -Unique)
            if ($files.Count -gt 0) {
                Write-Host "dotnet format: fixing $($files -join ', '), then rebuilding" -ForegroundColor Yellow
                dotnet format $Solution --include @files > $null 2>&1
                $out = dotnet build $Solution --configuration Release -p:CI=true 2>&1 | Out-String
            }
        }
        $out
    }

    if ($SkipTests) {
        Skip ".NET tests" "-SkipTests"
    } else {
        Step ".NET tests" {
            Set-Location $RepoRoot
            docker info > $null 2>&1
            if ($LASTEXITCODE -ne 0) {
                Fail "Docker is not running. Testcontainers needs it — start Docker Desktop, or pass -SkipTests."
                return
            }
            # global.json may opt into Microsoft.Testing.Platform, which changes the dotnet test CLI.
            $globalJson = Get-Content (Join-Path $RepoRoot 'global.json') -Raw | ConvertFrom-Json
            if ($globalJson.test.runner -eq 'Microsoft.Testing.Platform') {
                dotnet test --solution $Solution --configuration Release --no-build
            } else {
                dotnet test $Solution --configuration Release --no-build
            }
        }
    }
}

# ── Docs ────────────────────────────────────────────────────────────────────

if (-not $SkipDocs) {
    Step "ryni (Markdown links + skills)" {
        if (-not (Get-Command ryni -ErrorAction SilentlyContinue)) {
            Fail ("ryni is not installed. Install: " +
                "irm https://github.com/computerlovetech/ryni/releases/download/v0.2.2/ryni-installer.ps1 | iex")
            return
        }
        # ryni has no ignore support and would walk node_modules, bin/ and obj/.
        # Check a mirror of the non-ignored files instead: Markdown copied, everything
        # else as an empty placeholder so links to code files still resolve.
        $mirror = Join-Path ([IO.Path]::GetTempPath()) "kupro-ryni"
        if (Test-Path $mirror) { Remove-Item -Recurse -Force $mirror }
        git -C $RepoRoot -c core.quotepath=false ls-files -co --exclude-standard | ForEach-Object {
            $src = Join-Path $RepoRoot $_
            if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { return }
            $dest = Join-Path $mirror $_
            [void][IO.Directory]::CreateDirectory((Split-Path $dest))
            if ($_ -match '\.(md|markdown)$') {
                [IO.File]::Copy($src, $dest)
            } else {
                [IO.File]::WriteAllBytes($dest, @())
            }
        }
        Set-Location $mirror
        $env:NO_COLOR = '1'
        ryni check .
    }
}

Pop-Location

Write-Host "`n==> Summary" -ForegroundColor Cyan
foreach ($r in $StepResults) {
    if ($null -eq $r.Pass) {
        Write-Host "  SKIP  $($r.Name) ($($r.Reason))" -ForegroundColor Yellow
    } elseif ($r.Pass) {
        Write-Host "  PASS  $($r.Name) ($($r.Took))" -ForegroundColor Green
    } else {
        Write-Host "  FAIL  $($r.Name) ($($r.Took))" -ForegroundColor Red
    }
}

if ($Errors.Count -gt 0) {
    Write-Host "`n$($Errors.Count) step(s) failed:" -ForegroundColor Red
    $Errors | ForEach-Object { Write-Host "`n$_" -ForegroundColor Red }
    exit 1
}

Write-Host "`nAll checks passed." -ForegroundColor Green
exit 0
