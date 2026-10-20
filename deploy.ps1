# deploy.ps1 - Build Release (x64) and stage Sparta Clips to SpartaClips-final (the run target).
# IMPORTANT: keep this file ASCII-ONLY. Windows PowerShell 5.1 reads UTF-8-without-BOM scripts as
# ANSI; a UTF-8 em-dash ends in byte 0x94, which ANSI maps to a smart closing quote that PowerShell
# honors as a REAL quote - strings close early and runaway literals swallow whole statements.
# Usage:  ./deploy.ps1            (stops any running instance, builds, stages, then relaunches to the tray)
#         ./deploy.ps1 -NoLaunch  (same, but do NOT relaunch at the end)
#         ./deploy.ps1 -TestHooks (include the CDP UI-automation hook the tools/*.py smoke tests need)
param([switch]$NoLaunch, [switch]$TestHooks)
$ErrorActionPreference = "Stop"

$root  = $PSScriptRoot
$proj  = Join-Path $root "SpartaClips.csproj"
$bin   = Join-Path $root "bin\x64\Release\net8.0-windows10.0.22621.0"
$final = Join-Path $root "SpartaClips-final"

# Stop any running instance first: it locks SpartaClips.dll (and the staged exe), and a fresh launch would only poke
# the existing single-instance process instead of running the new build. Force-kill, then wait for the OS to release
# the file locks before we stage over them.
$running = Get-Process SpartaClips -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running SpartaClips..." -ForegroundColor DarkGray
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 50; $i++) {
        if (-not (Get-Process SpartaClips -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Milliseconds 300   # grace for the exe/DLL file locks to release
}

# Full rebuild every time: MSBuild's incremental check has skipped recompiles and deployed stale DLLs before.
$buildStart = Get-Date
$hooks = if ($TestHooks) { "-p:EnableTestHooks=true" } else { "-p:EnableTestHooks=false" }
dotnet build $proj -c Release --no-incremental --nologo $hooks
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed - not deploying."; exit 1 }

# Verify the compile actually happened.
$dll = Join-Path $bin "SpartaClips.dll"
if (-not (Test-Path $dll)) { Write-Error "Build output missing: $dll"; exit 1 }
if ((Get-Item $dll).LastWriteTime -lt $buildStart) {
    Write-Error "STALE BUILD: $dll predates this build run - the compile did not happen."
    exit 1
}

# Full payload - copy the ENTIRE build output (a framework-dependent .NET app needs its .runtimeconfig.json,
# .deps.json, the dependency DLLs (WebView2, ScreenRecorderLib, NAudio) and the runtimes/ folder).
New-Item -ItemType Directory -Force -Path $final | Out-Null
Copy-Item (Join-Path $bin "*") $final -Recurse -Force

# Web frontend - copy from SOURCE (an incremental build can skip the Content copy when only web/ changed).
$webSrc = Join-Path $root "web"
$webDst = Join-Path $final "web"
New-Item -ItemType Directory -Force -Path $webDst | Out-Null
Copy-Item (Join-Path $webSrc "*") $webDst -Recurse -Force

# Prove the staged copies match what we just built/edited before claiming success.
foreach ($pair in @(
    @{ src = $dll;                         dst = (Join-Path $final "SpartaClips.dll") },
    @{ src = (Join-Path $webSrc "app.js"); dst = (Join-Path $webDst "app.js") }
)) {
    if ((Get-FileHash $pair.src).Hash -ne (Get-FileHash $pair.dst).Hash) {
        Write-Error ("STAGED COPY MISMATCH: " + $pair.dst)
        exit 1
    }
}

Write-Host ("Deployed to " + $final + "  [verified: DLL built " +
    (Get-Item $dll).LastWriteTime.ToString("HH:mm:ss") + ", hashes match]") -ForegroundColor Green

# Relaunch and open the window (bare launch = not "--tray", so TrayCore shows the window instead of staying hidden).
# The single-instance mutex is free now that the old instance was stopped. (--tray stays reserved for autostart.)
if (-not $NoLaunch) {
    $exe = Join-Path $final "SpartaClips.exe"
    if (Test-Path $exe) {
        Write-Host "Starting SpartaClips (opening window)..." -ForegroundColor DarkGray
        Start-Process -FilePath $exe
    }
}
