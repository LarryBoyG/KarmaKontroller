$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\KarmaWinUSB.cs'
$outDir = Join-Path $root 'bin'
$out = Join-Path $outDir 'KarmaWinUSB.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (!(Test-Path $src)) {
    throw "Source file not found: $src"
}

if (!(Test-Path $csc)) {
    throw "C# compiler not found: $csc"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $csc /nologo /platform:x64 /optimize+ "/out:$out" "$src"

Write-Host "Built $out"
