# Builds EQ2Advanced.dll on Windows against your INSTALLED ACT.
#
#   Right-click -> Run with PowerShell      (or)
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-ActPath "C:\...\Advanced Combat Tracker"]
#
# You usually do not need this: GitHub Actions builds a loadable DLL on every
# push (it downloads ACT itself — see tools/fetch-act.sh), and `bash build.sh`
# does the same locally. This exists for building against a specific ACT install,
# and it prints that install's assembly identity, which is handy when diagnosing
# a load failure.
#
# Prereq: the .NET SDK — https://dotnet.microsoft.com/download
# The .NET Framework 4.8 Developer Pack is NOT needed; the reference assemblies
# come from NuGet.

param([string]$ActPath = "", [string]$Config = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-Act {
    $candidates = @(
        "${Env:ProgramFiles(x86)}\Advanced Combat Tracker",
        "$Env:ProgramFiles\Advanced Combat Tracker",
        "$Env:APPDATA\Advanced Combat Tracker",
        "$Env:LOCALAPPDATA\Advanced Combat Tracker"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "Advanced Combat Tracker.exe"))) { return $c }
    }
    foreach ($r in @($Env:ProgramFiles, "${Env:ProgramFiles(x86)}", $Env:APPDATA, $Env:LOCALAPPDATA)) {
        if (-not $r) { continue }
        $hit = Get-ChildItem -Path $r -Recurse -Filter "Advanced Combat Tracker.exe" -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.DirectoryName }
    }
    return $null
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "X  The .NET SDK isn't installed." -ForegroundColor Red
    Write-Host "   Get it here: https://dotnet.microsoft.com/download"
    exit 1
}

if (-not $ActPath) { $ActPath = Find-Act }
if (-not $ActPath -or -not (Test-Path (Join-Path $ActPath "Advanced Combat Tracker.exe"))) {
    Write-Host "X  Couldn't find 'Advanced Combat Tracker.exe'." -ForegroundColor Red
    Write-Host "   Pass it: .\build.ps1 -ActPath ""C:\Program Files (x86)\Advanced Combat Tracker"""
    Write-Host "   Or just use the DLL from GitHub Actions, which needs no ACT here."
    exit 1
}

$exe = Join-Path $ActPath "Advanced Combat Tracker.exe"
Write-Host "ACT:  $exe"
$identity = [System.Reflection.AssemblyName]::GetAssemblyName($exe).FullName
Write-Host "ACT assembly identity:" -ForegroundColor Cyan
Write-Host "  $identity" -ForegroundColor Cyan
Write-Host ""

dotnet build (Join-Path $root "EQ2Advanced.sln") -c $Config -p:ACTPath="$ActPath"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $root "EQ2Advanced\bin\$Config\EQ2Advanced.dll"
Write-Host ""
Write-Host "Built: $dll" -ForegroundColor Green
Write-Host "In ACT: Plugins -> Plugin Listing -> Browse -> pick that file -> Add/Enable."
