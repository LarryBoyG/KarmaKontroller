$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\KarmaWinUSBManager.cs'
$outDir = Join-Path $root 'bin'
$out = Join-Path $outDir 'KarmaKontroller.exe'
$backendRoot = Split-Path -Parent $root
$backendBuild = Join-Path $backendRoot 'KarmaWinUSB\build.ps1'
$backendExe = Join-Path $backendRoot 'KarmaWinUSB\bin\KarmaWinUSB.exe'
$patchToolExe = Join-Path $backendRoot 'KarmaKontroller-release-tools-only\KarmaKontroller.exe'
$patchAssets = Join-Path $backendRoot 'karma_mapbox_proxy\assets'
$icon = Join-Path $backendRoot 'karma_mapbox_proxy\assets\karma_k.ico'
$license = Join-Path $backendRoot 'LICENSE'
$thirdPartyNotices = Join-Path $backendRoot 'THIRD_PARTY_NOTICES.md'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (!(Test-Path $src)) {
    throw "Source file not found: $src"
}

if (!(Test-Path $csc)) {
    throw "C# compiler not found: $csc"
}

& $backendBuild
if ($LASTEXITCODE -ne 0) {
    throw "KarmaWinUSB backend build failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $backendExe)) {
    throw "Backend executable not found after build: $backendExe"
}

if (!(Test-Path $patchToolExe)) {
    throw "Patch helper executable not found: $patchToolExe"
}

if (!(Test-Path $patchAssets)) {
    throw "Patch assets folder not found: $patchAssets"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'drivers') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'assets') | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll "/out:$out" "$src"
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler failed with exit code $LASTEXITCODE"
}

Copy-Item -Force $backendExe (Join-Path $outDir 'KarmaWinUSB.exe')
Copy-Item -Force $patchToolExe (Join-Path $outDir 'KarmaKontrollerPatchTool.exe')
Copy-Item -Force (Join-Path $root 'drivers\KarmaWinUSB.inf') (Join-Path $outDir 'drivers\KarmaWinUSB.inf')
Copy-Item -Force (Join-Path $patchAssets '*') (Join-Path $outDir 'assets')
if (Test-Path $icon) {
    Copy-Item -Force $icon (Join-Path $outDir 'karma_k.ico')
}
if (Test-Path $license) {
    Copy-Item -Force $license (Join-Path $outDir 'LICENSE')
}
if (Test-Path $thirdPartyNotices) {
    Copy-Item -Force $thirdPartyNotices (Join-Path $outDir 'THIRD_PARTY_NOTICES.md')
}

Write-Host "Built $out"
