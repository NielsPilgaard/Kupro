# Installs the kupro CLI: irm https://kupro.app/install.ps1 | iex
# Downloads one binary from our EU bucket, checks it against SHA256SUMS and adds it to your user PATH (ADR-018).
#
# KUPRO_VERSION       a version instead of the latest
# KUPRO_INSTALL_DIR   a folder instead of %LOCALAPPDATA%\Programs\kupro
# KUPRO_DOWNLOAD_URL  a mirror instead of our bucket
#
# Everything is in a function, called on the last line, so a cut-off download runs nothing. It throws instead of
# calling exit, which would close the window of whoever piped it into iex. Works in Windows PowerShell 5.1.

function Install-Kupro {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    # Windows on Arm runs the x64 build through its emulation.
    if ($env:PROCESSOR_ARCHITECTURE -notin 'AMD64', 'ARM64') {
        throw "kupro install: no build for $env:PROCESSOR_ARCHITECTURE"
    }

    $base = if ($env:KUPRO_DOWNLOAD_URL) { $env:KUPRO_DOWNLOAD_URL.TrimEnd('/') } else { 'https://kupro-downloads.s3.gra.io.cloud.ovh.net' }
    $dir = if ($env:KUPRO_INSTALL_DIR) { $env:KUPRO_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\kupro' }
    $file = 'kupro-win-x64.exe'

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $version = $env:KUPRO_VERSION
        if (-not $version) {
            Invoke-WebRequest -UseBasicParsing "$base/cli/latest" -OutFile "$tmp\latest"
            $version = (Get-Content "$tmp\latest" -Raw).Trim()
        }

        Write-Host "Installing kupro $version (win-x64)"
        Invoke-WebRequest -UseBasicParsing "$base/cli/$version/SHA256SUMS" -OutFile "$tmp\SHA256SUMS"
        Invoke-WebRequest -UseBasicParsing "$base/cli/$version/$file" -OutFile "$tmp\kupro.exe"

        $line = Get-Content "$tmp\SHA256SUMS" | Where-Object { ($_ -split '\s+')[1] -in $file, "*$file" } | Select-Object -First 1
        if (-not $line) { throw "kupro install: release $version has no checksum for $file" }
        $expected = ($line -split '\s+')[0]
        if ((Get-FileHash "$tmp\kupro.exe" -Algorithm SHA256).Hash -ne $expected) {
            throw "kupro install: the download doesn't match its checksum; nothing was installed"
        }

        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $target = Join-Path $dir 'kupro.exe'
        if (Test-Path $target) {
            # A running exe can't be overwritten but can be renamed, and MCP clients keep kupro running.
            Get-ChildItem $dir -Filter 'kupro.exe.*.old' | Remove-Item -Force -ErrorAction SilentlyContinue
            Move-Item $target "$target.$([IO.Path]::GetRandomFileName()).old"
        }
        Move-Item "$tmp\kupro.exe" $target

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $dir) {
            [Environment]::SetEnvironmentVariable('Path', (($userPath, $dir | Where-Object { $_ }) -join ';'), 'User')
            Write-Host "Added $dir to your PATH"
        }
        if (($env:Path -split ';') -notcontains $dir) {
            $env:Path = "$env:Path;$dir"
        }

        Write-Host "Installed $target"
        Write-Host 'Next step: kupro install mcp'
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Install-Kupro
