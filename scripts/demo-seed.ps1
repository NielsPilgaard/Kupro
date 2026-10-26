#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates the demo workspace shown in the README screenshots (task 022): a fictional logistics company with three
    projects, epics and features spread over the coming year, an active sprint with work in every column, capacity,
    milestones and a cross-project dependency.
.DESCRIPTION
    Everything goes through the kupro CLI, as an agent would do it. Sign in first (`kupro login`, or KUPRO_TOKEN). The
    script sets KUPRO_WORKSPACE for its own calls only, so your default workspace stays as it is. Dates are relative to
    today, so the plan and the sprint look current whenever it runs. Fails if the workspace slug already exists: pass
    another -Slug, or delete the old workspace in the app.

    For the README screenshots, sign the CLI in as other@kupro.local (password: other), which has no other workspaces,
    so the sidebar shows only this one. Its UI language must be English (`kupro me update --language en`). Then, in
    tests/e2e: KUPRO_SCREENSHOTS=1 KUPRO_USER=other@kupro.local KUPRO_PASSWORD=other bunx playwright test readme-screenshots
.PARAMETER Slug
    Workspace slug. Default: lindholm.
.PARAMETER Kupro
    The CLI to call. Default: kupro on PATH, else the Debug build of src/Kupro.Cli (built if missing).
.EXAMPLE
    pwsh scripts/demo-seed.ps1
#>
param(
    [string]$Slug = 'lindholm',
    [string]$Kupro
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $Kupro) {
    $Kupro = (Get-Command kupro -ErrorAction SilentlyContinue)?.Source
}
if (-not $Kupro) {
    $Kupro = Join-Path $root 'src/Kupro.Cli/bin/Debug/net10.0/kupro.dll'
    if (-not (Test-Path $Kupro)) { dotnet build (Join-Path $root 'src/Kupro.Cli') -v q | Out-Null }
}

# Runs the CLI with --json and returns the parsed result; stops on the first failing call.
function K {
    $result = if ($Kupro.EndsWith('.dll')) { dotnet $Kupro @args --json } else { & $Kupro @args --json }
    if ($LASTEXITCODE -ne 0) { throw "kupro $($args -join ' ') failed" }
    if ($result) { $result | ConvertFrom-Json }
}

function Day([int]$offset) { (Get-Date).Date.AddDays($offset).ToString('yyyy-MM-dd') }
function Month([int]$offset) { (Get-Date).AddMonths($offset).ToString('yyyy-MM') }

$today = (Get-Date).Date
$monday = $today.AddDays(-(([int]$today.DayOfWeek + 6) % 7))
function Week([int]$weeks, [int]$days = 0) { $monday.AddDays(7 * $weeks + $days).ToString('yyyy-MM-dd') }

Write-Host "Creating workspace $Slug"
K workspace create $Slug --name 'Lindholm Logistics' | Out-Null
$env:KUPRO_WORKSPACE = $Slug
K workspace update --hours-per-week 30 | Out-Null

$projects = @(
    @{ Key = 'APP'; Name = 'Driver app'; Color = 'verdigris'; Description = 'The mobile app drivers use on the road: routes, scans and proof of delivery.' }
    @{ Key = 'WEB'; Name = 'Customer portal'; Color = 'copper'; Description = 'Where customers book, track and pay for shipments.' }
    @{ Key = 'PLAT'; Name = 'Platform'; Color = 'plum'; Description = 'Shared services: routing engine, auth, integrations.' }
)
foreach ($p in $projects) {
    K project create $p.Key --name $p.Name --color $p.Color --description $p.Description | Out-Null
}

K capacity set APP "3@$(Month 0)" "4@$(Month 3)" | Out-Null
K capacity set WEB "2@$(Month 0)" "3@$(Month 5)" | Out-Null
K capacity set PLAT "2@$(Month 0)" | Out-Null

# Epics and features: (title, start offset in weeks, length in weeks, effort in hours) per feature.
$roadmap = @(
    @{ Project = 'APP'; Epic = 'Offline-first driver app'; Features = @(
            @('Offline route cache', -2, 6, 480), @('Sync conflict handling', 3, 5, 360), @('Background location', 7, 4, 260)) }
    @{ Project = 'APP'; Epic = 'Proof of delivery'; Features = @(
            @('Photo and signature capture', 10, 6, 420), @('Damage reports', 15, 5, 300)) }
    @{ Project = 'WEB'; Epic = 'Self-service booking'; Features = @(
            @('Quote calculator', -1, 5, 300), @('Booking flow', 4, 7, 600), @('Invoices and receipts', 11, 4, 220)) }
    @{ Project = 'WEB'; Epic = 'Live tracking'; Features = @(
            @('Tracking page', 16, 6, 420), @('Delivery notifications', 21, 4, 180)) }
    @{ Project = 'PLAT'; Epic = 'Routing engine v2'; Features = @(
            @('Multi-stop optimisation', -3, 9, 520), @('Traffic-aware ETAs', 7, 8, 420)) }
    @{ Project = 'PLAT'; Epic = 'Carrier integrations'; Features = @(
            @('PostNord API', 16, 5, 300), @('DHL Freight API', 20, 6, 340)) }
)

$features = @{}
foreach ($e in $roadmap) {
    $first = ($e.Features | ForEach-Object { $_[1] } | Measure-Object -Minimum).Minimum
    $last = ($e.Features | ForEach-Object { $_[1] + $_[2] } | Measure-Object -Maximum).Maximum
    $epic = K issue create -p $e.Project -t $e.Epic --type epic --start (Week $first) --due (Week $last -3)
    Write-Host "  $($epic.key) $($e.Epic)"
    foreach ($f in $e.Features) {
        $state = if ($f[1] -lt 0) { 'in-progress' } else { 'todo' }
        $feature = K issue create -p $e.Project -t $f[0] --type feature --parent $epic.key --state $state `
            --start (Week $f[1]) --due (Week ($f[1] + $f[2]) -3) --effort $f[3]
        $features[$f[0]] = $feature.key
    }
}

# Cross-project dependencies, drawn as arrows on the plan.
K issue relate $features['Multi-stop optimisation'] blocks $features['Background location'] | Out-Null
K issue relate $features['Traffic-aware ETAs'] blocks $features['Tracking page'] | Out-Null

K milestone create --name 'Pilot' --date (Week 9 3) -p APP | Out-Null
K milestone create --name 'Launch' --date (Week 29) | Out-Null

# The APP board: an active two-week sprint with work in every column.
$sprint = K sprint create -p APP --name "Sprint $([Globalization.ISOWeek]::GetWeekOfYear($monday))" `
    --start (Week 0) --end (Week 1 4) --goal 'Drivers can finish a route without signal'
K sprint start $sprint.id | Out-Null

$parent = $features['Offline route cache']
$tasks = @(
    @{ Title = 'Store route stops in SQLite'; State = 'done'; Priority = 'high'; Effort = 8 }
    @{ Title = 'Encrypt the local cache'; State = 'done'; Priority = 'medium'; Effort = 5; Label = 'security' }
    @{ Title = 'Queue scans while offline'; State = 'in-review'; Priority = 'high'; Effort = 10 }
    @{ Title = 'Show offline banner with last sync time'; State = 'in-review'; Priority = 'medium'; Effort = 3; Label = 'ux' }
    @{ Title = 'Retry uploads with backoff'; State = 'in-progress'; Priority = 'high'; Effort = 6 }
    @{ Title = 'Cache map tiles for the day''s route'; State = 'in-progress'; Priority = 'medium'; Effort = 12 }
    @{ Title = 'App crashes when a stop has no address'; State = 'in-progress'; Priority = 'urgent'; Effort = 4; Label = 'bug' }
    @{ Title = 'Measure cache size on low-end Android'; State = 'todo'; Priority = 'low'; Effort = 4 }
    @{ Title = 'Clear cache on sign-out'; State = 'todo'; Priority = 'medium'; Effort = 2; Label = 'security' }
    @{ Title = 'Sync indicator in the route header'; State = 'todo'; Priority = 'medium'; Effort = 3; Label = 'ux' }
    @{ Title = 'Duplicate scan when tapping twice'; State = 'todo'; Priority = 'high'; Effort = 2; Label = 'bug' }
)
foreach ($t in $tasks) {
    $label = if ($t.Label) { '--label', $t.Label } else { @() }
    K issue create -p APP -t $t.Title --type task --parent $parent --state $t.State --priority $t.Priority `
        --effort $t.Effort --assignee me --sprint active @label | Out-Null
}

$backlog = @('Dark mode for night shifts', 'Haptic feedback on successful scan', 'Battery saver mode')
foreach ($title in $backlog) {
    K issue create -p APP -t $title --type task --state backlog | Out-Null
}

Write-Host "Done. Workspace '$Slug': $($tasks.Count) sprint tasks on the APP board, $($features.Count) features on the plan."
