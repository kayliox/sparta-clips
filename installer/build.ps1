# build.ps1 - build the Windows installer locally: self-contained publish + Inno Setup -> artifacts\SpartaClipsSetup.exe
# (The same steps run on GitHub for every push to main: .github/workflows/release.yml.)
# Keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI - see deploy.ps1).
# Usage:  ./installer/build.ps1 [-Version 1.2.3]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts\publish"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Error "Inno Setup 6 not found - install it: winget install JRSoftware.InnoSetup"; exit 1 }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "SpartaClips.csproj") -c Release -r win-x64 --self-contained true `
    -p:EnableTestHooks=false -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed."; exit 1 }

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $PSScriptRoot "SpartaClips.iss")
if ($LASTEXITCODE -ne 0) { Write-Error "Installer build failed."; exit 1 }
Write-Host ("Installer: " + (Join-Path $root "artifacts\SpartaClipsSetup.exe")) -ForegroundColor Green
