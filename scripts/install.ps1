param(
    [Parameter(Mandatory = $true)]
    [string]$AppDir,
    [string]$DistDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$DistDir = if ($DistDir) { $DistDir } else { Join-Path $repoRoot "dist" }

$launcherPath = Join-Path $DistDir "MX907600A_FixedLauncher.exe"
if (-not (Test-Path $launcherPath)) {
    throw "Build output not found: $launcherPath"
}

if (-not (Test-Path (Join-Path $AppDir "MX907600A.exe"))) {
    throw "MX907600A.exe not found in $AppDir"
}

Copy-Item $launcherPath (Join-Path $AppDir "MX907600A_FixedLauncher.exe") -Force

Write-Host "Installed:"
Write-Host "  $(Join-Path $AppDir 'MX907600A_FixedLauncher.exe')"
