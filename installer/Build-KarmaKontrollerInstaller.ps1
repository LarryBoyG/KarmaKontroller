$ErrorActionPreference = "Stop"

$version = "2.1"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$installerDir = Join-Path $root "installer"
$buildDir = Join-Path $installerDir "build"
$payloadDir = Join-Path $buildDir "payload"
$payloadZip = Join-Path $buildDir "payload.zip"
$appBuild = Join-Path $root "KarmaWinUSBApp\build.ps1"
$appBin = Join-Path $root "KarmaWinUSBApp\bin"
$setupScript = Join-Path $installerDir "KarmaKontroller-Install.ps1"
$setupScriptBuild = Join-Path $buildDir "KarmaKontroller-Install.ps1"
$bootstrap = Join-Path $installerDir "SetupBootstrap.cs"
$setupOut = Join-Path $root ("KarmaKontroller-Setup-" + $version + ".exe")
$latestSetupOut = Join-Path $root "KarmaKontroller-Setup.exe"
$releaseDir = Join-Path $root ("KarmaKontroller-release-winusb-" + $version)
$releaseSetup = Join-Path $releaseDir ("KarmaKontroller-Setup-" + $version + ".exe")
$releaseLatestSetup = Join-Path $releaseDir "KarmaKontroller-Setup.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$icon = Join-Path $appBin "karma_k.ico"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory=$true)][string]$Parent,
        [Parameter(Mandatory=$true)][string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [System.IO.Path]::GetFullPath($Child).TrimEnd('\') + '\'
    if (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside expected parent: $Child"
    }
}

function Reset-Directory {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$AllowedParent
    )

    Assert-ChildPath -Parent $AllowedParent -Child $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory=$true)][string]$Source,
        [Parameter(Mandatory=$true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required file is missing: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-RequiredDirectory {
    param(
        [Parameter(Mandatory=$true)][string]$Source,
        [Parameter(Mandatory=$true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required directory is missing: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

if (-not (Test-Path -LiteralPath $appBuild -PathType Leaf)) {
    throw "App build script is missing: $appBuild"
}
if (-not (Test-Path -LiteralPath $setupScript -PathType Leaf)) {
    throw "Installer script is missing: $setupScript"
}
if (-not (Test-Path -LiteralPath $bootstrap -PathType Leaf)) {
    throw "Setup bootstrap source is missing: $bootstrap"
}
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw "C# compiler not found: $csc"
}

& $appBuild
if ($LASTEXITCODE -ne 0) {
    throw "Karma Kontroller app build failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
Reset-Directory -Path $payloadDir -AllowedParent $buildDir

Copy-RequiredFile -Source (Join-Path $appBin "KarmaKontroller.exe") -Destination $payloadDir
Copy-RequiredFile -Source (Join-Path $appBin "KarmaWinUSB.exe") -Destination $payloadDir
Copy-RequiredFile -Source (Join-Path $appBin "KarmaKontrollerPatchTool.exe") -Destination $payloadDir
Copy-RequiredFile -Source (Join-Path $appBin "karma_k.ico") -Destination $payloadDir
Copy-RequiredFile -Source (Join-Path $appBin "LICENSE") -Destination $payloadDir
Copy-RequiredFile -Source (Join-Path $appBin "THIRD_PARTY_NOTICES.md") -Destination $payloadDir
Copy-RequiredDirectory -Source (Join-Path $appBin "assets") -Destination $payloadDir
Copy-RequiredDirectory -Source (Join-Path $appBin "drivers") -Destination $payloadDir

New-Item -ItemType Directory -Path (Join-Path $payloadDir "Backups") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payloadDir "Patch") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payloadDir "Logs") -Force | Out-Null

Copy-RequiredFile -Source $setupScript -Destination $setupScriptBuild

if (Test-Path -LiteralPath $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}
Compress-Archive -LiteralPath $payloadDir -DestinationPath $payloadZip -Force

$cscArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/r:System.Windows.Forms.dll",
    "/r:System.Drawing.dll",
    "/resource:$setupScriptBuild,KarmaKontroller-Install.ps1",
    "/resource:$payloadZip,payload.zip",
    "/out:$setupOut",
    $bootstrap
)
if (Test-Path -LiteralPath $icon -PathType Leaf) {
    $cscArgs += "/win32icon:$icon"
}

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    throw "Setup bootstrap build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath $setupOut -Destination $latestSetupOut -Force

Reset-Directory -Path $releaseDir -AllowedParent $root
Copy-RequiredFile -Source $setupOut -Destination $releaseSetup
Copy-RequiredFile -Source $latestSetupOut -Destination $releaseLatestSetup
Copy-RequiredFile -Source (Join-Path $payloadDir "KarmaKontroller.exe") -Destination $releaseDir
Copy-RequiredFile -Source (Join-Path $payloadDir "KarmaWinUSB.exe") -Destination $releaseDir
Copy-RequiredFile -Source (Join-Path $payloadDir "KarmaKontrollerPatchTool.exe") -Destination $releaseDir
Copy-RequiredFile -Source (Join-Path $payloadDir "LICENSE") -Destination $releaseDir
Copy-RequiredFile -Source (Join-Path $payloadDir "THIRD_PARTY_NOTICES.md") -Destination $releaseDir
Copy-RequiredDirectory -Source (Join-Path $payloadDir "assets") -Destination $releaseDir
Copy-RequiredDirectory -Source (Join-Path $payloadDir "drivers") -Destination $releaseDir
New-Item -ItemType Directory -Path (Join-Path $releaseDir "Backups") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $releaseDir "Patch") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $releaseDir "Logs") -Force | Out-Null

Write-Host "Built installer: $setupOut"
Write-Host "Built release folder: $releaseDir"
