#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Backup and restore rehearsal (task 021): breaks the backup agent on purpose and restores, on the production-style
    compose stack (deploy/docker-compose.yaml + scripts/backup-rehearsal/compose.yaml), and prints pass/fail per case
    and the measured restore times. Exits 1 when a case fails.
.DESCRIPTION
    Starts from a clean stack every run: removes the previous rehearsal project and its kupro-rehearsal-* volumes,
    creates the external volumes as the deploy runbook does, starts the stack and seeds two workspaces through the API.
    The dev stack's kupro-* volumes are never touched, and its host ports are one hundred above the dev stack's
    (app 5280, Keycloak 8280, Silo 8290, console 9290), so it runs next to `aspire run`.

    Cases (infrastructure/backup-agent/README.md, "Rehearsals"):
      1 Agent stopped past the slot cap        5 Console reached from the app network
      2 Wrong S3 key                           6 Point-in-time restore before a mass delete, go live, switch back
      3 Agent recreated with WAL not pushed    7 Lost server: new volumes, restore the newest point, go live
      4 Postgres stopped and started           8 Wrong cipher pass
.PARAMETER Cases
    Which cases to run, e.g. -Cases 4,5. Default: all.
.PARAMETER SkipBuild
    Use the kupro-*:rehearsal images from the last run instead of building them.
.PARAMETER Keep
    Leave the stack running at the end, e.g. to click through the console (http://localhost:9290) and the app
    (http://localhost:5280, dev@kupro.local / dev). Remove it later with -Down.
.PARAMETER Down
    Only remove the rehearsal stack and its volumes.
.EXAMPLE
    pwsh scripts/backup-rehearsal.ps1
.EXAMPLE
    pwsh scripts/backup-rehearsal.ps1 -Cases 6 -SkipBuild -Keep
#>
param(
    [ValidateRange(1, 8)][int[]]$Cases = 1..8,
    [switch]$SkipBuild,
    [switch]$Keep,
    [switch]$Down
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$env:COMPOSE_PROJECT_NAME = 'kupro-rehearsal'
$env:COMPOSE_FILE = "deploy/docker-compose.yaml$([IO.Path]::PathSeparator)scripts/backup-rehearsal/compose.yaml"
$env:COMPOSE_ENV_FILES = 'scripts/backup-rehearsal/rehearsal.env'

$cfg = @{}
foreach ($line in Get-Content scripts/backup-rehearsal/rehearsal.env) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') { $cfg[$Matches[1]] = $Matches[2] }
}

$project = $env:COMPOSE_PROJECT_NAME
$prefix = 'kupro-rehearsal-'
$externalVolumes = 'pgdata-a', 'pgdata-b', 'pg-control', 'wal-receive', 'backup-agent-state' | ForEach-Object { "$prefix$_" }
$consolePort = [int]$cfg.BACKUP_CONSOLE_PORT
$console = "http://localhost:$consolePort"
$app = $cfg.API_PUBLIC_URL
$keycloak = $cfg.KEYCLOAK_PUBLIC_URL
# Everything but the public site, which the rehearsal doesn't need.
$services = 'web', 'grafana', 'silo-buckets', 'heartbeats', 'backup-agent'
$copenhagen = [TimeZoneInfo]::FindSystemTimeZoneById('Europe/Copenhagen')
$results = [System.Collections.Generic.List[object]]::new()
$timings = [ordered]@{}

# ---------------------------------------------------------------------------------------------------------------------
# Helpers

function Say([string]$text, [string]$color = 'Gray') { Write-Host "[$(Get-Date -Format HH:mm:ss)] $text" -ForegroundColor $color }

function Compose {
    $output = docker compose @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed:`n$($output -join "`n")" }
    $output | Where-Object { $_ -is [string] }
}

function Wait-Until([scriptblock]$condition, [string]$what, [int]$seconds = 300, [int]$every = 5) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Seconds $every
    }
    throw "Timed out after $seconds s waiting for: $what"
}

function Read-AgentFile([string]$path) {
    $text = docker compose exec -T backup-agent cat $path 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $text) { return $null }
    ($text -join "`n") | ConvertFrom-Json -DateKind Offset
}

function Get-Status { Read-AgentFile /var/lib/backup-agent/ops/status.json }
function Get-State { Read-AgentFile /var/lib/backup-agent/state.json }

function Invoke-Sql([string]$database, [string]$sql) {
    $output = $sql | docker compose exec -T -e "PGPASSWORD=$($cfg.POSTGRES_PASSWORD)" postgres `
        psql -h /var/run/postgresql -U postgres -d $database -At -q -v ON_ERROR_STOP=1 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $($output -join "`n")" }
    $output | Where-Object { $_ -is [string] }
}

function Test-PostgresUp {
    docker compose exec -T postgres pg_isready -h /var/run/postgresql 2>$null | Out-Null
    $LASTEXITCODE -eq 0
}

function Get-HeartbeatPosts([datetime]$since) {
    $log = docker compose logs heartbeats --no-color --since $since.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') 2>$null
    @($log | Select-String -Pattern '"POST (/[a-z]+(/fail)?) ' -AllMatches | ForEach-Object { $_.Matches[0].Groups[1].Value })
}

function Get-PrometheusMax([string]$query) {
    $url = "http://localhost:9090/api/v1/query?query=$([uri]::EscapeDataString($query))"
    $json = docker compose exec -T prometheus wget -qO- $url 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $result = ($json -join '' | ConvertFrom-Json).data.result
    if ($result.Count -eq 0) { return $null }
    [double]$result[0].value[1]
}

function Get-DatabaseSize {
    Invoke-Sql postgres 'SELECT pg_size_pretty(sum(pg_database_size(datname))) FROM pg_database'
}

function ConvertTo-CopenhagenInput([DateTimeOffset]$at) {
    [TimeZoneInfo]::ConvertTime($at, $copenhagen).ToString('yyyy-MM-ddTHH:mm:ss')
}

# The console: cookie from the access link, then forms with their antiforgery token, as a browser would.
function Connect-Console {
    $link = (docker compose exec -T backup-agent cat /var/lib/backup-agent/console-link 2>$null | Select-Object -First 1)
    if (-not $link) { $link = (docker exec "$project-restore-agent" cat /var/lib/backup-agent/console-link | Select-Object -First 1) }
    $link = $link.Trim().Replace('localhost:9090', "localhost:$consolePort")
    $script:consoleSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    Wait-Until { (Invoke-WebRequest $link -WebSession $script:consoleSession).StatusCode -eq 200 } 'console access link' 120 | Out-Null
}

function Get-ConsolePage([string]$path) {
    (Invoke-WebRequest "$console$path" -WebSession $script:consoleSession).Content
}

# Posts a form like the browser. Returns the job (when it started one) or the error the console redirected with.
function Send-ConsoleForm([string]$page, [string]$action, [hashtable]$fields) {
    $html = Get-ConsolePage $page
    if ($html -notmatch 'name="__RequestVerificationToken" value="([^"]+)"') { throw "No form token on $page" }
    $body = @{ __RequestVerificationToken = $Matches[1] } + $fields
    $response = Invoke-WebRequest "$console$action" -Method Post -Body $body -WebSession $script:consoleSession
    $final = $response.BaseResponse.RequestMessage.RequestUri
    $query = [System.Web.HttpUtility]::ParseQueryString($final.Query)
    [pscustomobject]@{
        Path  = $final.AbsolutePath
        Job   = if ($final.AbsolutePath -match '^/job/([0-9a-f-]+)$') { $Matches[1] } else { $null }
        Error = $query['error']
    }
}

# Go live (or switch back). The job log can say done a moment before the agent lets the next action in ("Another job
# is running"), so retry that one refusal for a while.
function Invoke-GoLive([string]$volume, [switch]$AcceptFailedChecks) {
    $fields = @{ confirmation = $volume }
    if ($AcceptFailedChecks) { $fields.acceptFailedChecks = 'true' }
    for ($i = 0; $i -lt 10; $i++) {
        $result = Send-ConsoleForm /restore /restore/go-live $fields
        if ($result.Error -notmatch 'Another job') { return $result }
        Start-Sleep -Seconds 3
    }
    $result
}

function Wait-ConsoleJob([string]$id, [int]$seconds = 900) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $after = 0
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $log = Invoke-RestMethod "$console/job/$id/log?after=$after" -WebSession $script:consoleSession
        foreach ($line in $log.lines) { $lines.Add($line) }
        $after = $log.next
        if ($log.done) { return [pscustomobject]@{ Ok = [bool]$log.ok; Summary = $log.summary; Lines = $lines } }
        Start-Sleep -Seconds 3
    }
    throw "Console job $id still running after $seconds s"
}

# The API, as the dev user. The realm has no password grant; the rehearsal turns it on for the CLI client, in
# Keycloak's database, which is in the cluster and so restored with everything else.
function Enable-PasswordGrant {
    $kcadm = 'set -e; k=/opt/keycloak/bin/kcadm.sh; c=--config=/tmp/kcadm.config
$k config credentials $c --server http://localhost:8080 --realm master --user admin --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null
id=$($k get clients $c -r kupro -q clientId=cli --fields id --format csv --noquotes)
$k update clients/$id $c -r kupro -s directAccessGrantsEnabled=true'
    $output = $kcadm | docker compose exec -T keycloak bash 2>&1
    if ($LASTEXITCODE -ne 0) { throw "kcadm failed: $($output -join "`n")" }
}

function Get-AccessToken {
    (Invoke-RestMethod "$keycloak/realms/kupro/protocol/openid-connect/token" -Method Post -Body @{
            grant_type = 'password'; client_id = 'cli'; username = 'dev@kupro.local'; password = 'dev'
        }).access_token
}

function Invoke-Api([string]$method, [string]$path, $body = $null) {
    $headers = @{ Authorization = "Bearer $(Get-AccessToken)" }
    $params = @{ Method = $method; Uri = "$app/api/v1$path"; Headers = $headers }
    if ($null -ne $body) { $params.Body = ($body | ConvertTo-Json -Depth 5); $params.ContentType = 'application/json' }
    Invoke-RestMethod @params
}

function Wait-App([int]$seconds = 300) {
    Wait-Until {
        # Invoke-Api throws unless the call succeeds; an empty list is a success.
        (Invoke-WebRequest "$app/api/v1/status").StatusCode -eq 200 -and $null -eq (Invoke-Api GET /workspaces | Out-Null)
    } 'the app (API status, Keycloak sign-in, an authenticated call)' $seconds | Out-Null
}

$seed = @{
    'rehearsal-a' = @{ Name = 'Rehearsal A'; Projects = [ordered]@{ OPS = 6; WEB = 4 } }
    'rehearsal-b' = @{ Name = 'Rehearsal B'; Projects = [ordered]@{ BETA = 3 } }
}
$attachmentText = "Rehearsal attachment $(New-Guid)"
$script:attachment = $null

function Invoke-Seed {
    foreach ($slug in $seed.Keys | Sort-Object) {
        $ws = $seed[$slug]
        Invoke-Api POST /workspaces @{ name = $ws.Name; slug = $slug } | Out-Null
        foreach ($key in $ws.Projects.Keys) {
            Invoke-Api POST "/workspaces/$slug/projects" @{ key = $key; name = "Project $key" } | Out-Null
            $issues = 1..$ws.Projects[$key] | ForEach-Object { @{ project = $key; title = "$key rehearsal issue $_" } }
            Invoke-Api POST "/workspaces/$slug/issues/bulk" @{ issues = @($issues) } | Out-Null
            Invoke-Api POST "/workspaces/$slug/issues/$key-1/comments" @{ body = "Seeded comment on $key-1" } | Out-Null
        }
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes($attachmentText)
    $upload = Invoke-Api POST /workspaces/rehearsal-a/attachments @{
        fileName = 'rehearsal.txt'; contentType = 'text/plain'; size = $bytes.Length; issue = 'WEB-1'
    }
    Invoke-WebRequest $upload.uploadUrl -Method Put -ContentType 'text/plain' -Body $bytes | Out-Null
    Invoke-Api POST "/workspaces/rehearsal-a/attachments/$($upload.attachment.id)/complete" | Out-Null
    $script:attachment = $upload.attachment.id
}

# What the restore checks compare: workspaces, issues per project, the attachment's content, tenant isolation.
function Get-AppSnapshot {
    $workspaces = @((Invoke-Api GET /workspaces) | ForEach-Object { $_.slug } | Where-Object { $_ -like 'rehearsal-*' } | Sort-Object)
    $issues = [ordered]@{}
    foreach ($slug in $workspaces) {
        foreach ($key in $seed[$slug].Projects.Keys) {
            $issues["$slug/$key"] = @((Invoke-Api GET "/workspaces/$slug/issues?project=$key&limit=200").items).Count
        }
    }

    # Tenant isolation as a user sees it: workspace B's listing holds only BETA issues.
    $leak = @((Invoke-Api GET '/workspaces/rehearsal-b/issues?limit=200').items | Where-Object { $_.key -notlike 'BETA-*' }).Count
    $download = (Invoke-Api GET "/workspaces/rehearsal-a/attachments/$($script:attachment)").downloadUrl
    # The body as bytes: text/plain downloads as application/octet-stream.
    $content = if ($download) { [Text.Encoding]::UTF8.GetString((Invoke-WebRequest $download).RawContentStream.ToArray()) } else { $null }
    [pscustomobject]@{
        Workspaces = $workspaces -join ','
        Issues     = ($issues.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
        Attachment = $content -eq $attachmentText
        Isolated   = $leak -eq 0
    }
}

function Format-Snapshot($s) { "workspaces $($s.Workspaces); issues $($s.Issues); attachment $(if ($s.Attachment) { 'ok' } else { 'MISSING' }); isolation $(if ($s.Isolated) { 'ok' } else { 'LEAK' })" }

function Test-SameApp($expected, $actual) {
    $actual.Workspaces -eq $expected.Workspaces -and $actual.Issues -eq $expected.Issues -and $actual.Attachment -and $actual.Isolated
}

# Waits until the agent has secured everything written up to now (the newest WAL with a commit is in the repo).
function Wait-Secured([string]$what, [int]$seconds = 300) {
    $now = [DateTimeOffset]::UtcNow
    Wait-Until { (Get-Status).dataSecuredAt -gt $now } "data secured after $what" $seconds | Out-Null
}

function Wait-FullBackupAfter([DateTimeOffset]$after, [int]$seconds = 600) {
    Wait-Until {
        $s = Get-Status
        $s.backups.lastFullAt -gt $after -and -not (Get-State).fullBackupRequested
    } "a full backup after $($after.ToString('HH:mm:ss'))" $seconds | Out-Null
}

# Recreates the agent with some variables changed (shell variables win over rehearsal.env), or back to normal.
function Set-AgentEnv([hashtable]$values = @{}) {
    foreach ($name in 'BACKUP_S3_KEY_SECRET', 'BACKUP_CIPHER_PASS') { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
    foreach ($entry in $values.GetEnumerator()) { Set-Item "env:$($entry.Key)" $entry.Value }
    Compose up -d --force-recreate backup-agent | Out-Null
    Wait-Until { (Invoke-WebRequest "$console/healthz").StatusCode -eq 200 } 'agent console up' 120 | Out-Null
    Connect-Console
}

function Restart-Stack {
    # Restore runbook §4: Postgres reads the marker on start; Keycloak and the API restart behind it.
    Compose restart postgres keycloak api | Out-Null
    Wait-Until { Test-PostgresUp } 'Postgres up' 180 | Out-Null
    Wait-App
}

function Remove-Stack {
    Say "Removing the rehearsal stack and its volumes"
    docker rm -f "$project-restore-agent" 2>$null | Out-Null
    docker compose down -v --remove-orphans 2>&1 | Out-Null
    foreach ($volume in $externalVolumes) { docker volume rm $volume 2>$null | Out-Null }
}

function New-DataVolumes {
    # Deploy runbook, "Backups" step 4.
    foreach ($volume in $externalVolumes) { docker volume create $volume | Out-Null }
}

function Add-Result([int]$case, [string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Case = $case; Name = $name; Ok = $ok; Detail = $detail })
    Say "Case ${case}: $(if ($ok) { 'PASS' } else { 'FAIL' }) - $detail" $(if ($ok) { 'Green' } else { 'Red' })
}

# ---------------------------------------------------------------------------------------------------------------------
# Cases. Each writes down what it expects, does it, and checks it.

function Invoke-Case1 {
    Say 'Case 1: agent stopped past the slot cap. Expect: Postgres drops the slot and keeps running; the WAL gap shows in the status, the WAL heartbeat fails and the health metric goes unhealthy; the agent makes a new slot and closes the gap with a full backup.' Cyan
    $since = Get-Date
    Compose stop backup-agent | Out-Null
    Invoke-Sql rehearsal 'CREATE TABLE IF NOT EXISTS wal_filler (x text)' | Out-Null
    $lost = $false
    for ($i = 0; $i -lt 12 -and -not $lost; $i++) {
        Invoke-Sql rehearsal @"
INSERT INTO wal_filler SELECT repeat(md5(random()::text), 32) FROM generate_series(1, 40000);
SELECT pg_switch_wal();
CHECKPOINT;
"@ | Out-Null
        $lost = (Invoke-Sql postgres "SELECT wal_status FROM pg_replication_slots WHERE slot_name = 'agent'") -eq 'lost'
    }
    Invoke-Sql rehearsal 'TRUNCATE wal_filler; CHECKPOINT' | Out-Null
    $postgresUp = (Test-PostgresUp) -and (Invoke-WebRequest "$app/api/v1/status").StatusCode -eq 200

    Compose start backup-agent | Out-Null
    Wait-Until { (Invoke-WebRequest "$console/healthz").StatusCode -eq 200 } 'agent console up' 120 | Out-Null
    Connect-Console
    $gapSeen = $false
    try {
        Wait-Until { @((Get-State).gaps | Where-Object { $_.detectedAt -gt $since }).Count -gt 0 } 'a WAL gap in the agent state' 180 | Out-Null
        $gapSeen = $true
    } catch { }
    $statusSaid = $false
    try {
        Wait-Until { @((Get-Status).issues | Where-Object { $_ -match 'WAL gap|slot lost' }).Count -gt 0 } 'the gap in status.json' 180 | Out-Null
        $statusSaid = $true
    } catch { }
    $closed = $false
    try {
        Wait-Until { $g = @((Get-State).gaps | Where-Object { $_.detectedAt -gt $since }); $g.Count -gt 0 -and -not ($g | Where-Object { -not $_.closedAt }) } 'the gap closed by a full backup' 600 | Out-Null
        $closed = $true
    } catch { }
    $walFail = (Get-HeartbeatPosts $since) -contains '/wal/fail'
    # Not a pass condition: the agent usually closes the gap within a minute, before the next OTLP export, and the
    # Grafana alert waits 5 minutes anyway. Case 2 checks the metric on a lasting failure.
    $metric = Get-PrometheusMax 'max(max_over_time(backup_agent_health[15m]))'
    $ok = $lost -and $postgresUp -and $gapSeen -and $statusSaid -and $closed -and $walFail
    Add-Result 1 'Agent stopped past the slot cap' $ok "slot lost: $lost; Postgres and API up: $postgresUp; gap recorded: $gapSeen; in status: $statusSaid; WAL heartbeat failed: $walFail; health metric max: $metric; gap closed: $closed"
}

function Invoke-Case2 {
    Say 'Case 2: wrong S3 key. Expect: the status goes unhealthy (repo unreachable), the WAL and backup heartbeats fail, a manual backup fails, and Postgres and the app keep working.' Cyan
    $since = Get-Date
    Set-AgentEnv @{ BACKUP_S3_KEY_SECRET = 'wrong-secret' }
    $unhealthy = $false
    try {
        Wait-Until { $s = Get-Status; $s.generatedAt -gt $since -and $s.health -eq 'Unhealthy' -and ($s.issues -match 'Repo unreachable|archive-push failing') } 'unhealthy status naming the repo' 300 | Out-Null
        $unhealthy = $true
    } catch { }
    $issue = @((Get-Status).issues | Where-Object { $_ -match 'Repo unreachable|archive-push' })[0]
    $job = Send-ConsoleForm /backups /actions/backup @{ type = 'full'; confirmIo = 'true' }
    $backupFailed = $false
    if ($job.Job) { $backupFailed = -not (Wait-ConsoleJob $job.Job 600).Ok }
    try { Wait-Until { (Get-HeartbeatPosts $since) -contains '/wal/fail' } 'the WAL heartbeat to fail' 360 | Out-Null } catch { }
    $posts = Get-HeartbeatPosts $since
    # What Grafana's "Backups unhealthy" rule reads (OTLP export every minute, then Prometheus).
    $metric = $null
    try { Wait-Until { ($script:m = Get-PrometheusMax 'max(backup_agent_health)') -eq 2 } 'the health metric at 2' 240 | Out-Null } catch { }
    $metric = $script:m
    Invoke-Sql rehearsal "CREATE TABLE IF NOT EXISTS marker (at timestamptz); INSERT INTO marker VALUES (now())" | Out-Null
    $appUp = (Test-PostgresUp) -and @(Invoke-Api GET /workspaces).Count -gt 0
    $ok = $unhealthy -and $backupFailed -and ($posts -contains '/wal/fail') -and ($posts -contains '/backup/fail') -and $metric -eq 2 -and $appUp
    Add-Result 2 'Wrong S3 key' $ok "unhealthy: $unhealthy ($issue); manual backup failed: $backupFailed; heartbeats: $(($posts | Sort-Object -Unique) -join ', '); health metric: $metric; Postgres and app up: $appUp"
}

function Invoke-Case3 {
    Say 'Case 3: agent container recreated while WAL is not pushed yet. Expect: the WAL waits on the wal-receive volume, the new container pushes it, no gap, and data written before the recreate is secured.' Cyan
    $since = [DateTimeOffset]::UtcNow
    if ($env:BACKUP_S3_KEY_SECRET -ne 'wrong-secret') { Set-AgentEnv @{ BACKUP_S3_KEY_SECRET = 'wrong-secret' } }
    $pushedBefore = (Get-Status).wal.lastPushedSegment
    Invoke-Sql rehearsal "CREATE TABLE IF NOT EXISTS marker (at timestamptz); INSERT INTO marker VALUES (now()); SELECT pg_switch_wal()" | Out-Null
    $marker = [DateTimeOffset]::UtcNow
    Wait-Until {
        @(docker compose exec -T backup-agent sh -c 'ls /wal-receive /wal-receive/outbox 2>/dev/null' | Where-Object { $_ -match '^[0-9A-F]{24}$' }).Count -gt 0
    } 'a finished segment waiting in wal-receive' 180 | Out-Null
    $waiting = @(docker compose exec -T backup-agent sh -c 'ls /wal-receive /wal-receive/outbox 2>/dev/null' | Where-Object { $_ -match '^[0-9A-F]{24}$' }).Count
    Set-AgentEnv
    $secured = $false
    try {
        Wait-Until { (Get-Status).dataSecuredAt -gt $marker } 'the marker secured' 300 | Out-Null
        $secured = $true
    } catch { }
    $status = Get-Status
    $newGaps = @((Get-State).gaps | Where-Object { $_.detectedAt -gt $since }).Count
    $ok = $secured -and $newGaps -eq 0
    Add-Result 3 'Agent recreated with WAL not pushed' $ok "segments waiting: $waiting; pushed before: $pushedBefore, after: $($status.wal.lastPushedSegment); marker secured: $secured; new gaps: $newGaps"
}

function Invoke-Case4 {
    Say 'Case 4: Postgres stopped and started. Expect: the status shows Postgres down, streaming resumes on the same slot after the start, no gap, and the app works again.' Cyan
    $since = [DateTimeOffset]::UtcNow
    $slotBefore = (Get-State).slotCreatedAt
    Compose stop postgres | Out-Null
    $sawDown = $false
    try {
        Wait-Until { $s = Get-Status; $s.generatedAt -gt $since -and -not $s.postgres.reachable } 'status: Postgres down' 180 | Out-Null
        $sawDown = $true
    } catch { }
    Compose start postgres | Out-Null
    Wait-Until { Test-PostgresUp } 'Postgres up' 180 | Out-Null
    Invoke-Sql rehearsal "INSERT INTO marker VALUES (now())" | Out-Null
    $secured = $false
    try { Wait-Secured 'the Postgres restart'; $secured = $true } catch { }
    $state = Get-State
    $sameSlot = $state.slotCreatedAt -eq $slotBefore
    $newGaps = @($state.gaps | Where-Object { $_.detectedAt -gt $since }).Count
    $appUp = $false
    try { Wait-App 120; $appUp = $true } catch { }
    $ok = $sawDown -and $secured -and $sameSlot -and $newGaps -eq 0 -and $appUp
    Add-Result 4 'Postgres stopped and started' $ok "status showed down: $sawDown; streaming resumed: $secured; same slot: $sameSlot; new gaps: $newGaps; app up: $appUp"
}

function Invoke-Case5 {
    Say 'Case 5: the console reached from a container on the app network. Expect: no route by name, and by IP or the host port either no connection or 401 without the key; never the console.' Cyan
    $agent = Compose ps -q backup-agent | Select-Object -First 1
    $agentIp = docker inspect -f "{{(index .NetworkSettings.Networks `"${project}_backup`").IPAddress}}" $agent
    $gateway = docker network inspect "${project}_aspire" -f '{{(index .IPAM.Config 0).Gateway}}'
    $targets = "backup-agent:9090", "${agentIp}:9090", "host.docker.internal:$consolePort", "${gateway}:$consolePort"
    $answers = foreach ($target in $targets) {
        $hostName, $port = $target -split ':'
        $probe = "timeout 5 bash -c 'exec 3<>/dev/tcp/$hostName/$port && printf ""GET / HTTP/1.0\r\nHost: localhost\r\n\r\n"" >&3 && head -1 <&3' 2>/dev/null || echo 'no connection'"
        $answer = (($probe | docker compose exec -T api bash 2>$null) -join ' ').Trim()
        [pscustomobject]@{ Target = $target; Answer = if ($answer) { $answer } else { 'no connection' } }
    }
    $ok = -not ($answers | Where-Object { $_.Answer -match ' 200 ' })
    Add-Result 5 'Console from the app network' $ok (($answers | ForEach-Object { "$($_.Target) -> $($_.Answer)" }) -join '; ')
}

function Invoke-Case6 {
    Say 'Case 6: point-in-time restore to just before a mass delete of project OPS. Expect: the restore checks pass, the deleted issues are back after go-live and a restart, the agent takes a full backup on the new timeline, and switching back shows the deleted state again.' Cyan
    $before = Get-AppSnapshot
    Wait-Secured 'the seed'
    Start-Sleep -Seconds 5
    $target = [DateTimeOffset]::UtcNow
    Start-Sleep -Seconds 5
    Invoke-Sql kupro "DELETE FROM issues WHERE project_id = (SELECT id FROM projects WHERE key = 'OPS')" | Out-Null
    $deleted = Get-AppSnapshot
    Wait-Secured 'the delete'

    $spare = (Get-Status).volumes.spare
    $start = Send-ConsoleForm /restore /restore @{ mode = 'Time'; time = ConvertTo-CopenhagenInput $target; confirmation = $spare }
    if (-not $start.Job) { Add-Result 6 'Point-in-time restore' $false "restore refused: $($start.Error)"; return }
    $job = Wait-ConsoleJob $start.Job
    $restore = (Get-State).spare
    $checks = ($restore.checks | ForEach-Object { "$($_.name) $(if ($_.ok -eq $true) { 'ok' } elseif ($_.ok -eq $false) { 'FAILED' } else { 'skipped' })" }) -join ', '
    $timings['PITR restore'] = [math]::Round($restore.durationSeconds)

    $arm = Invoke-GoLive $spare
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $restartAt = [DateTimeOffset]::UtcNow
    Restart-Stack
    $timings['PITR go-live restart'] = [math]::Round($clock.Elapsed.TotalSeconds)
    $live = Get-AppSnapshot
    $restored = Test-SameApp $before $live
    $backedUp = $false
    try {
        Wait-Until { (Get-State).liveVolumeName -eq $spare } "the agent on $spare" 180 | Out-Null
        Wait-FullBackupAfter $restartAt
        $backedUp = $true
    } catch { }
    Connect-Console
    Send-ConsoleForm /restore /restore/checklist @{ item = 'full-backup' } | Out-Null

    $original = (Get-Status).volumes.spare
    $back = Invoke-GoLive $original
    $backAt = [DateTimeOffset]::UtcNow
    Restart-Stack
    $switched = Get-AppSnapshot
    $switchedOk = $switched.Issues -eq $deleted.Issues
    $backedUpAgain = $false
    try {
        Wait-Until { (Get-State).liveVolumeName -eq $original } "the agent on $original" 180 | Out-Null
        Wait-FullBackupAfter $backAt
        $backedUpAgain = $true
    } catch { }
    Connect-Console

    $ok = $job.Ok -and -not $arm.Error -and $restored -and $backedUp -and -not $back.Error -and $switchedOk -and $backedUpAgain
    Add-Result 6 'Point-in-time restore, go live, switch back' $ok ("restore: $($job.Summary) Checks: $checks. " +
        "After go-live: $(Format-Snapshot $live) (expected issues $($before.Issues)); full backup on the new timeline: $backedUp. " +
        "Switch back: $(if ($back.Error) { $back.Error } else { 'armed' }), issues $($switched.Issues) (expected $($deleted.Issues)); full backup after: $backedUpAgain")
}

function Invoke-Case7 {
    Say 'Case 7: lost server. Expect: on new empty volumes, a lone agent (restore runbook §10) with the same bucket and cipher pass restores the newest point, go-live writes the marker, and the full stack starts on it with all data and the attachment.' Cyan
    $expected = Get-AppSnapshot
    Invoke-Sql rehearsal "INSERT INTO marker VALUES (now())" | Out-Null
    Wait-Secured 'the last write before the loss'

    Say 'Losing the server: stack down, data volumes and agent state deleted (the bucket stays)'
    Compose down | Out-Null
    foreach ($volume in $externalVolumes + "${project}_kupro-pg-socket", "${project}_kupro-keycloak-data") { docker volume rm $volume 2>$null | Out-Null }
    New-DataVolumes
    Compose up -d silo heartbeats | Out-Null

    $envFile = Join-Path ([IO.Path]::GetTempPath()) "kupro-rehearsal-agent-$PID.env"
    @(
        "PGBACKREST_REPO1_S3_ENDPOINT=$($cfg.BACKUP_S3_ENDPOINT)"
        "PGBACKREST_REPO1_STORAGE_PORT=9000"
        "PGBACKREST_REPO1_S3_REGION=$($cfg.BACKUP_S3_REGION)"
        "PGBACKREST_REPO1_S3_BUCKET=$($cfg.BACKUP_S3_BUCKET)"
        "PGBACKREST_REPO1_S3_KEY=$($cfg.BACKUP_S3_KEY)"
        "PGBACKREST_REPO1_S3_KEY_SECRET=$($cfg.BACKUP_S3_KEY_SECRET)"
        "PGBACKREST_REPO1_CIPHER_PASS=$($cfg.BACKUP_CIPHER_PASS)"
        "OpsBucket__ServiceUrl=$($cfg.BACKUP_OPS_URL)"
        "OpsBucket__BucketName=$($cfg.BACKUP_OPS_BUCKET)"
        "OpsBucket__AccessKey=$($cfg.BACKUP_OPS_ACCESS_KEY)"
        "OpsBucket__SecretKey=$($cfg.BACKUP_OPS_SECRET_KEY)"
        "Agent__VolumeNamePrefix=${prefix}pgdata-"
    ) | Set-Content $envFile
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        docker run -d --name "$project-restore-agent" -p "127.0.0.1:${consolePort}:9090" --network "${project}_backup" `
            -v "${prefix}pgdata-a:/pgdata/a" -v "${prefix}pgdata-b:/pgdata/b" -v "${prefix}pg-control:/pg-control" `
            --env-file $envFile $cfg.BACKUP_AGENT_IMAGE | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'docker run of the lone agent failed' }
    } finally {
        Remove-Item $envFile -ErrorAction SilentlyContinue
    }
    Wait-Until { (Invoke-WebRequest "$console/healthz").StatusCode -eq 200 } 'the lone agent console up' 120 | Out-Null
    Connect-Console
    Wait-Until { (Get-ConsolePage /restore) -match 'name="time"' } 'restorable ranges on the Restore page' 180 | Out-Null
    $page = Get-ConsolePage /restore
    $newest = if ($page -match 'name="time"[^>]*value="([^"]+)"') { $Matches[1] } else { $null }
    $spare = if ($page -match 'Type <code>([^<]+)</code> to wipe it and restore') { $Matches[1] } else { "${prefix}pgdata-b" }
    # The wizard's default, the newest restorable minute, as the runbook does. A time taken after the last write can lie
    # past the newest WAL in the repo; recovery then ends before the target and the restore fails.
    $start = Send-ConsoleForm /restore /restore @{ mode = 'Time'; time = $newest; confirmation = $spare }
    if (-not $start.Job) { Add-Result 7 'Lost server' $false "restore refused: $($start.Error)"; docker rm -f "$project-restore-agent" | Out-Null; return }
    $job = Wait-ConsoleJob $start.Job
    $timings['Lost-server restore'] = [math]::Round($clock.Elapsed.TotalSeconds)
    $arm = Invoke-GoLive $spare -AcceptFailedChecks
    $marker = "$(docker exec "$project-restore-agent" cat /pg-control/active 2>$null)".Trim()
    docker rm -f "$project-restore-agent" | Out-Null

    $clock.Restart()
    $upAt = [DateTimeOffset]::UtcNow
    Compose up -d @services | Out-Null
    Wait-Until { Test-PostgresUp } 'Postgres up' 180 | Out-Null
    Wait-App
    $timings['Lost-server stack start'] = [math]::Round($clock.Elapsed.TotalSeconds)
    $live = Get-AppSnapshot
    $same = Test-SameApp $expected $live
    Connect-Console
    $backedUp = $false
    try { Wait-FullBackupAfter $upAt; $backedUp = $true } catch { }
    $ok = $job.Ok -and -not $arm.Error -and $marker -eq $spare.Substring($spare.Length - 1) -and $same -and $backedUp
    Add-Result 7 'Lost server' $ok "restore: $($job.Summary) Go live: $(if ($arm.Error) { $arm.Error } else { "marker $marker" }). After start: $(Format-Snapshot $live) (expected $($expected.Issues)); full backup: $backedUp"
}

function Invoke-Case8 {
    Say 'Case 8: wrong cipher pass. Expect: the Restore page offers nothing and shows pgBackRest''s error, a restore request is refused, the status is unhealthy; the right pass brings it all back.' Cyan
    $since = [DateTimeOffset]::UtcNow
    Set-AgentEnv @{ BACKUP_CIPHER_PASS = 'wrong-cipher-pass' }
    Wait-Until { (Get-ConsolePage /restore) -notmatch 'Reading the repo' } 'the agent to read the repo' 180 | Out-Null
    $page = Get-ConsolePage /restore
    $offersNothing = $page -match 'Nothing to restore'
    $why = if ($page -match 'Nothing to restore:[^<]*') { [System.Net.WebUtility]::HtmlDecode($Matches[0]) } else { '(no reason shown)' }
    # The Restore page has no form now; post one anyway (token from Backups) to see the agent refuse it.
    $attempt = Send-ConsoleForm /backups /restore @{ mode = 'Time'; time = ConvertTo-CopenhagenInput $since; confirmation = (Get-Status).volumes.spare }
    $refused = -not $attempt.Job
    $unhealthy = $false
    try {
        Wait-Until { $s = Get-Status; $s.generatedAt -gt $since -and $s.health -eq 'Unhealthy' } 'unhealthy status' 180 | Out-Null
        $unhealthy = $true
    } catch { }
    $issue = @((Get-Status).issues | Where-Object { $_ -match 'pgbackrest|Repo' })[0]
    Set-AgentEnv
    $recovered = $false
    try {
        Wait-Until { (Get-ConsolePage /restore) -match 'name="time"' } 'restorable ranges with the right pass' 180 | Out-Null
        Wait-Secured 'the right pass'
        $recovered = $true
    } catch { }
    $ok = $offersNothing -and $why -match 'cipher pass' -and $refused -and $unhealthy -and $recovered
    Add-Result 8 'Wrong cipher pass' $ok "page: $why; restore request: $(if ($refused) { "refused ($($attempt.Error))" } else { 'STARTED' }); status: $issue; right pass recovers: $recovered"
}

# ---------------------------------------------------------------------------------------------------------------------
# Run

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Say 'docker not found' Red; exit 1 }
if ($Down) { Remove-Stack; exit 0 }

# A clean stack every run: a kept one from last time goes first, so its ports are free again.
Remove-Stack

foreach ($port in ([uri]$app).Port, ([uri]$keycloak).Port, ([uri]$cfg.STORAGE_URL).Port, $consolePort) {
    try {
        $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        $probe.Start(); $probe.Stop()
    } catch {
        Say "Port $port is in use by something else. Free it and try again." Red
        exit 1
    }
}

$started = Get-Date
try {
    if (-not $SkipBuild) {
        Say 'Building images (kupro-*:rehearsal)'
        $builds = @(
            @('docker', 'build', '-q', '-t', 'kupro-postgres:rehearsal', 'infrastructure/postgres'),
            @('docker', 'build', '-q', '-t', 'kupro-backup-agent:rehearsal', '-f', 'infrastructure/backup-agent/Dockerfile', '.'),
            @('docker', 'build', '-q', '-t', 'kupro-keycloak:rehearsal', '-f', 'infrastructure/keycloak/Dockerfile', '.'),
            @('docker', 'build', '-q', '-t', 'kupro-web:rehearsal', '-f', 'web/Dockerfile', '--build-arg', "VITE_KEYCLOAK_URL=$keycloak", '.'),
            @('dotnet', 'publish', 'src/Kupro.Api', '-c', 'Release', '-t:PublishContainer', '--os', 'linux', '--arch', 'x64',
                '-p:ContainerRepository=kupro-api', '-p:ContainerImageTags=rehearsal', '-v:q', '-nologo')
        )
        foreach ($build in $builds) {
            $output = & $build[0] $build[1..($build.Count - 1)] 2>&1
            if ($LASTEXITCODE -ne 0) { throw "$($build -join ' ') failed:`n$($output -join "`n")" }
        }
    }

    New-DataVolumes
    Say 'Starting the stack'
    Compose up -d @services | Out-Null
    Wait-Until { (Invoke-WebRequest "$app/api/v1/status").StatusCode -eq 200 -and (Invoke-WebRequest "$keycloak/realms/kupro").StatusCode -eq 200 } 'API and Keycloak' 300 | Out-Null
    Enable-PasswordGrant
    Wait-App
    Wait-Until { (Invoke-WebRequest "$console/healthz").StatusCode -eq 200 } 'agent console up' 120 | Out-Null
    Connect-Console
    Say 'Waiting for the first full backup'
    Wait-Until { (Get-Status).backups.lastFullAt } 'the first full backup' 600 | Out-Null

    Say 'Seeding two workspaces through the API'
    Invoke-Seed
    Invoke-Sql postgres 'CREATE DATABASE rehearsal' | Out-Null
    Say "Seeded: $(Format-Snapshot (Get-AppSnapshot))"

    foreach ($case in $Cases | Sort-Object -Unique) {
        try {
            & "Invoke-Case$case"
        } catch {
            Add-Result $case "Case $case" $false "error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"
            # Back to a running stack with the right settings, so one failure doesn't fail the cases after it.
            try {
                docker rm -f "$project-restore-agent" 2>$null | Out-Null
                Compose up -d @services | Out-Null
                Set-AgentEnv
                Wait-App 180
            } catch {
                Say "Couldn't bring the stack back after case ${case}: $($_.Exception.Message)" Yellow
            }
        }
    }

    $size = Get-DatabaseSize
    $restoreSeconds = @(($timings['PITR restore'] + $timings['PITR go-live restart']), ($timings['Lost-server restore'] + $timings['Lost-server stack start'])) |
        Measure-Object -Maximum
    $rtoMinutes = [math]::Max(1, [math]::Ceiling($restoreSeconds.Maximum / 60))
    if ($timings.Count -gt 0) {
        # Restore runbook: the measured time goes on the console's Drills page as a manual drill.
        $notes = "Rehearsal script (task 021), local compose stack, ${size}: " + (($timings.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value) s" }) -join ', ')
        $drill = Send-ConsoleForm /drills /drills/manual @{ date = (Get-Date -Format 'yyyy-MM-dd'); rtoMinutes = "$rtoMinutes"; notes = $notes }
        if ($drill.Error) { Say "Couldn't log the manual drill: $($drill.Error)" Yellow }
    }
} catch {
    Say "Rehearsal stopped: $($_.Exception.Message)" Red
    $results.Add([pscustomobject]@{ Case = 0; Name = 'Setup'; Ok = $false; Detail = $_.Exception.Message })
} finally {
    foreach ($name in 'BACKUP_S3_KEY_SECRET', 'BACKUP_CIPHER_PASS') { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
    if ($Keep) {
        Say "Stack kept: app $app (dev@kupro.local / dev), console $console (link: docker compose -p $project exec backup-agent cat /var/lib/backup-agent/console-link). Remove with -Down." Yellow
    } else {
        Remove-Stack
    }
}

Write-Host ''
$results | Sort-Object Case | Format-Table Case, Name, @{ n = 'Result'; e = { if ($_.Ok) { 'PASS' } else { 'FAIL' } } }, Detail -Wrap
Write-Host "Database size: $size. Timings (s): $(($timings.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', '). Took $([math]::Round(((Get-Date) - $started).TotalMinutes)) min."
Write-Host ''
Write-Host 'For the agent README (Rehearsals):'
$passed = @($results | Where-Object Ok).Count
Write-Host "| $(Get-Date -Format 'yyyy-MM-dd') | local compose | $(($Cases | Sort-Object -Unique) -join ', ') | $passed/$($results.Count) passed | $size | $(($timings.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value) s" }) -join '; ') |"
Write-Host ''
Write-Host 'By hand, with -Keep (the script cannot click): open the console link, read the Restore page summary and the After go-live checklist; in the app, sign in and open the attachment on WEB-1.'

if ($results | Where-Object { -not $_.Ok }) { exit 1 }
exit 0
