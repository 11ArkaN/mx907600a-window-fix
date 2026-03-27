param(
    [Parameter(Mandatory = $true)]
    [string]$AppDir,
    [string]$OutDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$OutDir = if ($OutDir) { $OutDir } else { Join-Path $repoRoot "dist" }
$launcherSource = Join-Path $repoRoot "src\\launcher\\Program.cs"
$proxySource = Join-Path $repoRoot "src\\drawproxy\\Draw9076Proxy.c"
$proxyDef = Join-Path $repoRoot "src\\drawproxy\\Draw9076Proxy.def"
$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$vcvarsPath = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars32.bat"

if (-not (Test-Path (Join-Path $AppDir "MX907600A.exe"))) {
    throw "MX907600A.exe not found in $AppDir"
}

if (-not (Test-Path $cscPath)) {
    throw "C# compiler not found at $cscPath"
}

if (-not (Test-Path $vcvarsPath)) {
    throw "vcvars32.bat not found at $vcvarsPath"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$proxyOut = Join-Path $OutDir "Draw9076.dll"
$launcherOut = Join-Path $OutDir "MX907600A_FixedLauncher.exe"

$clCommand = @(
    "call `"$vcvarsPath`" >nul",
    "cl /nologo /O2 /LD /TC `"$proxySource`" /link /DEF:`"$proxyDef`" /OUT:`"$proxyOut`" user32.lib kernel32.lib"
) -join " && "

cmd /c $clCommand
if ($LASTEXITCODE -ne 0) {
    throw "Proxy DLL build failed"
}

& $cscPath `
    /nologo `
    /optimize+ `
    /target:winexe `
    "/resource:$proxyOut,MX907600AWindowFix.Draw9076.dll" `
    "/out:$launcherOut" `
    $launcherSource

if ($LASTEXITCODE -ne 0) {
    throw "Launcher build failed"
}

Write-Host ""
Write-Host "Built:"
Write-Host "  $proxyOut"
Write-Host "  $launcherOut"
