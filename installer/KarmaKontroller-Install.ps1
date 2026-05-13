param([switch]$NoStart)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $powershell = Join-Path $PSHOME "powershell.exe"
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath)
    if ($NoStart) { $args += "-NoStart" }
    Start-Process -FilePath $powershell -ArgumentList $args -Verb RunAs -Wait
    exit
}

Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$programFilesX86 = ${env:ProgramFiles(x86)}
if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
    $programFilesX86 = $programFiles
}

$choice = [System.Windows.Forms.MessageBox]::Show(
    "Install KarmaKontroller in Program Files?`r`n`r`nChoose No to install in Program Files (x86).",
    "KarmaKontroller Setup",
    [System.Windows.Forms.MessageBoxButtons]::YesNoCancel,
    [System.Windows.Forms.MessageBoxIcon]::Question,
    [System.Windows.Forms.MessageBoxDefaultButton]::Button1
)
if ($choice -eq [System.Windows.Forms.DialogResult]::Cancel) {
    exit
}

if ($choice -eq [System.Windows.Forms.DialogResult]::No) {
    $targetBase = $programFilesX86
} else {
    $targetBase = $programFiles
}

$installDir = Join-Path $targetBase "KarmaKontroller"
$zipPath = Join-Path $PSScriptRoot "payload.zip"
if (-not (Test-Path -LiteralPath $zipPath)) {
    [System.Windows.Forms.MessageBox]::Show("payload.zip is missing from the installer package.", "KarmaKontroller Setup", "OK", "Error") | Out-Null
    exit 1
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("KarmaKontrollerInstall-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $tempDir -Force
    $payloadDir = Join-Path $tempDir "payload"
    if (-not (Test-Path -LiteralPath $payloadDir)) {
        throw "Installer payload folder was not found."
    }

    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Get-Process -Name "KarmaKontroller" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Copy-Item -Path (Join-Path $payloadDir "*") -Destination $installDir -Recurse -Force

    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if ([string]::IsNullOrWhiteSpace($documents)) {
        $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    }
    $dataRoot = Join-Path $documents "KarmaKontroller"
    New-Item -ItemType Directory -Path (Join-Path $dataRoot "Backup") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dataRoot "Patch") -Force | Out-Null

    $wsh = New-Object -ComObject WScript.Shell
    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
    $shortcutPath = Join-Path $programs "KarmaKontroller.lnk"
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $installDir "KarmaKontroller.exe"
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = Join-Path $installDir "assets\karma_k.ico"
    $shortcut.Save()

    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
    if (-not [string]::IsNullOrWhiteSpace($desktop)) {
        $desktopShortcut = $wsh.CreateShortcut((Join-Path $desktop "KarmaKontroller.lnk"))
        $desktopShortcut.TargetPath = Join-Path $installDir "KarmaKontroller.exe"
        $desktopShortcut.WorkingDirectory = $installDir
        $desktopShortcut.IconLocation = Join-Path $installDir "assets\karma_k.ico"
        $desktopShortcut.Save()
    }

    $driverReadme = Join-Path $installDir "cmdUpdTool2\Drivers\README-Unsigned-Driver-Test-Mode.txt"
    if (-not (Test-Path -LiteralPath $driverReadme)) {
        "See the KarmaKontroller documentation for unsigned driver test mode instructions." | Set-Content -LiteralPath $driverReadme -Encoding ASCII
    }

    $backupDir = Join-Path $dataRoot "Backup"
    $patchDir = Join-Path $dataRoot "Patch"
    $message = "KarmaKontroller was installed to:`r`n$installDir`r`n`r`nBackup folder:`r`n$backupDir`r`n`r`nPatch folder:`r`n$patchDir"
    [System.Windows.Forms.MessageBox]::Show($message, "KarmaKontroller Setup", "OK", "Information") | Out-Null

    if (-not $NoStart) {
        $start = [System.Windows.Forms.MessageBox]::Show("Start KarmaKontroller now?", "KarmaKontroller Setup", "YesNo", "Question")
        if ($start -eq [System.Windows.Forms.DialogResult]::Yes) {
            Start-Process -FilePath (Join-Path $installDir "KarmaKontroller.exe") -WorkingDirectory $installDir
        }
    }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
