# Builds self-contained release archives for every supported platform.
# Output: publish/dist/InfinityCI.{Server|Agent}.{Win64|Linux64|LinuxArm64|Osx64|OsxArm64}.{zip|tar.gz}
# Windows archives are .zip; Unix archives are .tar.gz (extracted binaries may
# need `chmod +x` — the bundled StartServer.sh fixes that automatically when
# run via `bash StartServer.sh`).
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

Write-Host "== Building web UI =="
Push-Location web
npm ci
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }
npm run build
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }
Pop-Location

$platforms = @(
    @{ Rid = "win-x64";     Name = "Win64" },
    @{ Rid = "linux-x64";   Name = "Linux64" },
    @{ Rid = "linux-arm64"; Name = "LinuxArm64" },
    @{ Rid = "osx-x64";     Name = "Osx64" },
    @{ Rid = "osx-arm64";   Name = "OsxArm64" }
)

$serverStartCmd = @'
@echo off
rem Infinity CI server: web UI on 0.0.0.0:5000, agent gRPC on 0.0.0.0:5001.
set InfinityCI__ListenHost=0.0.0.0
"InfinityCI.Server.exe" %*
'@

$serverStartSh = @'
#!/usr/bin/env bash
# Infinity CI server: web UI on 0.0.0.0:5000, agent gRPC on 0.0.0.0:5001.
# Linux system dependencies: git and libldap (LDAP login). On first use the
# binary may need an execute permission: chmod +x InfinityCI.Server
export InfinityCI__ListenHost=0.0.0.0
chmod +x ./InfinityCI.Server 2>/dev/null || true
./InfinityCI.Server "$@"
'@

$agentStartCmd = @'
@echo off
rem Infinity CI agent: pass the master address, e.g.
rem   StartAgent.cmd --Agent:MasterUrl=http://<master>:5000
"InfinityCI.Agent.exe" %*
'@

$agentStartSh = @'
#!/usr/bin/env bash
# Infinity CI agent: pass the master address, e.g.
#   bash StartAgent.sh --Agent:MasterUrl=http://<master>:5000
chmod +x ./InfinityCI.Agent 2>/dev/null || true
./InfinityCI.Agent "$@"
'@

New-Item -ItemType Directory -Force -Path "publish/dist" | Out-Null

foreach ($platform in $platforms) {
    $rid = $platform.Rid
    $name = $platform.Name
    $serverDir = "publish/$rid/server"
    $agentDir = "publish/$rid/agent"

    Write-Host "== Publishing server $rid =="
    dotnet publish src/InfinityCI.Server -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $serverDir
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Host "== Publishing agent $rid =="
    dotnet publish src/InfinityCI.Agent -c Release -r $rid --self-contained true -p:PublishSingleFile=true -o $agentDir
    if ($LASTEXITCODE -ne 0) { exit 1 }

    # Bundle the built SPA into the server layout.
    Copy-Item -Recurse -Force "web/dist" (Join-Path $serverDir "wwwroot")

    if ($rid -eq "win-x64") {
        Set-Content -Path (Join-Path $serverDir "StartServer.cmd") -Value $serverStartCmd -Encoding ascii
        Set-Content -Path (Join-Path $agentDir "StartAgent.cmd") -Value $agentStartCmd -Encoding ascii
        Compress-Archive -Path (Join-Path $serverDir "*") -DestinationPath "publish/dist/InfinityCI.Server.$name.zip" -Force
        Compress-Archive -Path (Join-Path $agentDir "*") -DestinationPath "publish/dist/InfinityCI.Agent.$name.zip" -Force
    }
    else {
        [System.IO.File]::WriteAllText((Join-Path $serverDir "StartServer.sh"), ($serverStartSh.Replace("`r`n", "`n")))
        [System.IO.File]::WriteAllText((Join-Path $agentDir "StartAgent.sh"), ($agentStartSh.Replace("`r`n", "`n")))
        tar -czf "publish/dist/InfinityCI.Server.$name.tar.gz" -C $serverDir .
        if ($LASTEXITCODE -ne 0) { exit 1 }
        tar -czf "publish/dist/InfinityCI.Agent.$name.tar.gz" -C $agentDir .
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }
    Write-Host "== $rid done =="
}

Write-Host "All archives written to publish/dist/"
