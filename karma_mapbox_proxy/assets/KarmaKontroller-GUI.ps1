param([string]$Exe)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:LastRunOK = $false
$script:OperationRunning = $false
$script:OutputOffsets = @{}
$script:LastProgressLog = ""
$script:LastErrorLine = ""
$script:LastResultCode = $null
$script:LastResultMessage = ""
$script:ActiveLabel = ""
$script:ActiveArguments = @()
$script:BackupStartTime = [DateTime]::MinValue
$script:AllBackupSteps = @(
    [pscustomobject]@{ Name = "bootloader"; File = "bootloaderBU.img"; Bytes = [int64]0x400000 },
    [pscustomobject]@{ Name = "boot"; File = "bootBU.img"; Bytes = [int64]0x2000000 },
    [pscustomobject]@{ Name = "recovery"; File = "recoveryBU.img"; Bytes = [int64]0x2000000 },
    [pscustomobject]@{ Name = "system"; File = "systemBU.img"; Bytes = [int64]0x40000000 },
    [pscustomobject]@{ Name = "data"; File = "dataBU.img"; Bytes = [int64]0x51bf0000 },
    [pscustomobject]@{ Name = "gopro"; File = "goproBU.img"; Bytes = [int64]0x20000000 }
)
$script:BackupSteps = $script:AllBackupSteps

function New-Label($text, $x, $y, $w, $h) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $text
    $label.Location = New-Object System.Drawing.Point($x, $y)
    $label.Size = New-Object System.Drawing.Size($w, $h)
    return $label
}

function New-TextBox($x, $y, $w) {
    $box = New-Object System.Windows.Forms.TextBox
    $box.Location = New-Object System.Drawing.Point($x, $y)
    $box.Size = New-Object System.Drawing.Size($w, 24)
    return $box
}

function New-Button($text, $x, $y, $w) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $text
    $button.Location = New-Object System.Drawing.Point($x, $y)
    $button.Size = New-Object System.Drawing.Size($w, 28)
    return $button
}

function New-CheckBox($text, $x, $y, $w) {
    $box = New-Object System.Windows.Forms.CheckBox
    $box.Text = $text
    $box.Checked = $true
    $box.Location = New-Object System.Drawing.Point($x, $y)
    $box.Size = New-Object System.Drawing.Size($w, 22)
    return $box
}

function Get-AppRoot {
    if (-not [string]::IsNullOrWhiteSpace($Exe) -and (Test-Path -LiteralPath $Exe)) {
        return [System.IO.Path]::GetDirectoryName($Exe)
    }
    return (Get-Location).Path
}

function Get-DataRoot {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if ([string]::IsNullOrWhiteSpace($documents)) {
        $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    }
    return [System.IO.Path]::Combine($documents, "KarmaKontroller")
}

function Get-DefaultBackupDir {
    $path = [System.IO.Path]::Combine((Get-DataRoot), "Backup")
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function Get-DefaultPatchDir {
    $path = [System.IO.Path]::Combine((Get-DataRoot), "Patch")
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function Find-AppIcon {
    $appRoot = Get-AppRoot
    foreach ($path in @(
        [System.IO.Path]::Combine($appRoot, "assets", "karma_k.ico"),
        [System.IO.Path]::Combine($appRoot, "karma_k.ico"),
        [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($PSCommandPath), "karma_k.ico")
    )) {
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return $null
}

function Set-WindowIcon($window) {
    $iconPath = Find-AppIcon
    if ([string]::IsNullOrWhiteSpace($iconPath)) { return }
    try {
        $window.Icon = New-Object System.Drawing.Icon($iconPath)
    } catch {
        Write-Host ("Could not load window icon: " + $_.Exception.Message)
    }
}

function Set-ControlsEnabled($enabled) {
    foreach ($control in $script:ActionControls) {
        $control.Enabled = $enabled
    }
}

function Add-Log($text) {
    $script:LogBox.AppendText($text + [Environment]::NewLine)
    $script:LogBox.SelectionStart = $script:LogBox.Text.Length
    $script:LogBox.ScrollToCaret()
}

function Set-OperationProgress($percent, $status) {
    if ($percent -lt 0) { $percent = 0 }
    if ($percent -gt 100) { $percent = 100 }
    $script:ProgressBar.Style = [System.Windows.Forms.ProgressBarStyle]::Continuous
    $script:ProgressBar.Value = [int]$percent
    $script:ProgressText.Text = ("{0}% - {1}" -f [int]$percent, $status)
    $script:StatusLabel.Text = $status
    $script:Form.Refresh()
}

function Add-ProgressLog($percent, $status) {
    $progressKey = "$percent|$status"
    if ($progressKey -ne $script:LastProgressLog) {
        Add-Log ("[{0}%] {1}" -f [int]$percent, $status)
        $script:LastProgressLog = $progressKey
    }
}

function Set-ActiveBackupSteps($names) {
    $selected = @()
    foreach ($step in $script:AllBackupSteps) {
        if ($names -contains $step.Name) {
            $selected += $step
        }
    }
    $script:BackupSteps = $selected
}

function Get-SelectedBackupNames {
    $names = @()
    foreach ($entry in $script:BackupCheckBoxes.GetEnumerator()) {
        if ($entry.Value.Checked) {
            $names += $entry.Key
        }
    }
    return $names
}

function Get-BackupRanges {
    $maxPercent = 99
    $minStepPercent = 5
    $totalBytes = [int64]0
    foreach ($step in $script:BackupSteps) {
        $totalBytes += [int64]$step.Bytes
    }

    $ranges = @()
    $current = 0
    $minBudget = $minStepPercent * $script:BackupSteps.Count
    if ($totalBytes -le 0 -or $minBudget -ge $maxPercent) {
        for ($i = 0; $i -lt $script:BackupSteps.Count; $i++) {
            $end = [int][Math]::Floor((($i + 1) * $maxPercent) / $script:BackupSteps.Count)
            $ranges += [pscustomobject]@{ Start = $current; End = $end }
            $current = $end
        }
        return $ranges
    }

    $variableBudget = $maxPercent - $minBudget
    for ($i = 0; $i -lt $script:BackupSteps.Count; $i++) {
        if ($i -eq $script:BackupSteps.Count - 1) {
            $width = $maxPercent - $current
        } else {
            $width = $minStepPercent + [int][Math]::Floor(([double]$script:BackupSteps[$i].Bytes * $variableBudget) / [double]$totalBytes)
            if ($width -lt 1) { $width = 1 }
            if (($current + $width) -gt $maxPercent) { $width = $maxPercent - $current }
        }
        $ranges += [pscustomobject]@{ Start = $current; End = ($current + $width) }
        $current += $width
    }
    return $ranges
}

function Get-RecentFileLength($path) {
    if (-not (Test-Path -LiteralPath $path)) { return [int64]0 }
    $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if ($null -eq $item) { return [int64]0 }
    if ($script:BackupStartTime -ne [DateTime]::MinValue -and $item.LastWriteTime -lt $script:BackupStartTime.AddSeconds(-2)) {
        return [int64]0
    }
    return [int64]$item.Length
}

function Update-BackupProgress($folder) {
    if ([string]::IsNullOrWhiteSpace($folder)) { return }
    $ranges = Get-BackupRanges
    for ($i = 0; $i -lt $script:BackupSteps.Count; $i++) {
        $step = $script:BackupSteps[$i]
        $range = $ranges[$i]
        $tmpPath = [System.IO.Path]::Combine($folder, $step.File + ".tmp")
        $finalPath = [System.IO.Path]::Combine($folder, $step.File)
        $tmpBytes = Get-RecentFileLength $tmpPath
        $finalBytes = Get-RecentFileLength $finalPath

        if ($finalBytes -ge [int64]$step.Bytes) {
            continue
        }

        $doneBytes = $tmpBytes
        if ($doneBytes -gt [int64]$step.Bytes) { $doneBytes = [int64]$step.Bytes }
        $width = [int]$range.End - [int]$range.Start
        $percent = [int]$range.Start
        $stepPercent = 0
        if ([int64]$step.Bytes -gt 0) {
            $percent = [int]$range.Start + [int][Math]::Floor(([double]$doneBytes * $width) / [double]$step.Bytes)
            $stepPercent = [int][Math]::Floor(([double]$doneBytes * 100) / [double]$step.Bytes)
        }
        if ($percent -gt [int]$range.End) { $percent = [int]$range.End }
        if ($stepPercent -gt 100) { $stepPercent = 100 }

        $status = "Backing up {0} {1}% ({2}/{3})" -f $step.Name, $stepPercent, ($i + 1), $script:BackupSteps.Count
        Set-OperationProgress $percent $status
        Add-ProgressLog $percent $status
        return
    }

    Set-OperationProgress 100 "Backup complete"
    Add-ProgressLog 100 "Backup complete"
}

function Test-BackupComplete($folder) {
    if ([string]::IsNullOrWhiteSpace($folder)) { return $false }
    foreach ($step in $script:BackupSteps) {
        $path = [System.IO.Path]::Combine($folder, $step.File)
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if ($null -eq $item) { return $false }
        if ([int64]$item.Length -ne [int64]$step.Bytes) { return $false }
    }
    return $true
}

function Test-Ext4Image($path, [int64]$expectedBytes) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        return [pscustomobject]@{ Ok = $false; Message = "No image path was selected." }
    }
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{ Ok = $false; Message = "Image was not found: $path" }
    }
    $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return [pscustomobject]@{ Ok = $false; Message = "Image could not be opened: $path" }
    }
    if ($item.PSIsContainer) {
        return [pscustomobject]@{ Ok = $false; Message = "Selected path is a folder, not an image file." }
    }
    if ($expectedBytes -gt 0 -and [int64]$item.Length -ne $expectedBytes) {
        return [pscustomobject]@{ Ok = $false; Message = ("Image is {0} bytes; expected {1} bytes." -f [int64]$item.Length, $expectedBytes) }
    }
    if ([int64]$item.Length -lt 1082) {
        return [pscustomobject]@{ Ok = $false; Message = "Image is too small to contain an ext4 superblock." }
    }

    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek(1080, [System.IO.SeekOrigin]::Begin)
        $b1 = $stream.ReadByte()
        $b2 = $stream.ReadByte()
        if ($b1 -eq 0x53 -and $b2 -eq 0xEF) {
            return [pscustomobject]@{ Ok = $true; Message = "Image looks like a raw ext4 filesystem." }
        }
        return [pscustomobject]@{ Ok = $false; Message = ("Image does not have a valid ext4 signature at byte 1080. Found 0x{0:X2} 0x{1:X2}." -f $b1, $b2) }
    } catch {
        return [pscustomobject]@{ Ok = $false; Message = $_.Exception.Message }
    } finally {
        $stream.Dispose()
    }
}

function Get-DataBackupPath {
    if ([string]::IsNullOrWhiteSpace($backupBox.Text)) {
        return [System.IO.Path]::Combine($script:DefaultBackupDir, "dataBU.img")
    }
    return [System.IO.Path]::Combine($backupBox.Text, "dataBU.img")
}

function Set-DataRestoreFromBackupFolder {
    if ($null -eq $script:DataRestoreBox) { return }
    $path = Get-DataBackupPath
    if (Test-Path -LiteralPath $path) {
        $script:DataRestoreBox.Text = $path
    }
}

function Require-ValidDataBackupBeforeSystemFlash {
    $path = Get-DataBackupPath
    $check = Test-Ext4Image $path ([int64]0x51bf0000)
    if ($check.Ok) {
        if ($null -ne $script:DataRestoreBox) {
            $script:DataRestoreBox.Text = $path
        }
        return $true
    }

    $message = "A valid dataBU.img backup is required before flashing system." + [Environment]::NewLine + [Environment]::NewLine + "Expected:" + [Environment]::NewLine + $path + [Environment]::NewLine + [Environment]::NewLine + $check.Message + [Environment]::NewLine + [Environment]::NewLine + "The controller OS may fail to boot if /data is wiped or invalid. Back up the Data partition first, or select the folder that contains a valid dataBU.img from this controller."
    [System.Windows.Forms.MessageBox]::Show($message, "KarmaKontroller", "OK", "Warning") | Out-Null
    return $false
}

function Quote-ProcessArgument([string]$argument) {
    if ($null -eq $argument -or $argument.Length -eq 0) { return '""' }
    if ($argument -notmatch '[\s"]') { return $argument }

    $result = '"'
    $slashes = 0
    foreach ($ch in $argument.ToCharArray()) {
        if ($ch -eq '\') {
            $slashes++
            continue
        }
        if ($ch -eq '"') {
            if ($slashes -gt 0) { $result += ('\' * ($slashes * 2)) }
            $result += '\"'
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            $result += ('\' * $slashes)
            $slashes = 0
        }
        $result += $ch
    }
    if ($slashes -gt 0) { $result += ('\' * ($slashes * 2)) }
    $result += '"'
    return $result
}

function Join-ProcessArguments($arguments) {
    $quoted = @()
    foreach ($argument in $arguments) {
        $quoted += Quote-ProcessArgument ([string]$argument)
    }
    return ($quoted -join " ")
}

function Handle-ProcessText($text) {
    if ([string]::IsNullOrEmpty($text)) { return }
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    foreach ($line in $normalized.Split("`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^KK_PROGRESS\|([0-9]{1,3})\|(.*)$') {
            $percent = [int]$matches[1]
            $status = $matches[2]
            if ($script:ActiveLabel -eq "Backup") { continue }
            Set-OperationProgress $percent $status
            Add-ProgressLog $percent $status
        } elseif ($line -match '^KK_RESULT\|([0-9]+)\|(.*)$') {
            $script:LastResultCode = [int]$matches[1]
            $script:LastResultMessage = $matches[2]
            if ($script:LastResultCode -ne 0 -and -not [string]::IsNullOrWhiteSpace($script:LastResultMessage)) {
                Add-Log $script:LastResultMessage
                $script:LastErrorLine = $script:LastResultMessage
            }
        } else {
            Add-Log $line
            if (-not [string]::IsNullOrWhiteSpace($line) -and -not $line.StartsWith("> ")) {
                $script:LastErrorLine = $line
            }
        }
    }
}

function Read-NewProcessText($path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    if (-not $script:OutputOffsets.ContainsKey($path)) {
        $script:OutputOffsets[$path] = [int64]0
    }

    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek([int64]$script:OutputOffsets[$path], [System.IO.SeekOrigin]::Begin)
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            $text = $reader.ReadToEnd()
            $script:OutputOffsets[$path] = $stream.Position
        } finally {
            $reader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
    Handle-ProcessText $text
}

function Invoke-Karma($arguments, $label) {
    $script:LastRunOK = $false
    $script:OperationRunning = $true
    $script:LastProgressLog = ""
    $script:LastErrorLine = ""
    $script:LastResultCode = $null
    $script:LastResultMessage = ""
    $script:ActiveLabel = $label
    $script:ActiveArguments = $arguments
    if ($label -eq "Backup") {
        $script:BackupStartTime = Get-Date
    }
    Set-ControlsEnabled $false
    Set-OperationProgress 0 "$label starting..."
    Add-Log ""
    Add-Log ("> KarmaKontroller.exe " + ($arguments -join " "))

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    $script:OutputOffsets[$stdoutPath] = [int64]0
    $script:OutputOffsets[$stderrPath] = [int64]0

    try {
        $argLine = Join-ProcessArguments $arguments
        $process = Start-Process -FilePath $Exe -ArgumentList $argLine -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
        while (-not $process.HasExited) {
            Read-NewProcessText $stdoutPath
            Read-NewProcessText $stderrPath
            if ($label -eq "Backup" -and $arguments.Count -ge 2) {
                Update-BackupProgress $arguments[1]
            }
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 200
        }
        $process.WaitForExit()
        $process.Refresh()
        Read-NewProcessText $stdoutPath
        Read-NewProcessText $stderrPath
        if ($label -eq "Backup" -and $arguments.Count -ge 2) {
            Update-BackupProgress $arguments[1]
        }

        $backupLooksComplete = $false
        if ($label -eq "Backup" -and $arguments.Count -ge 2) {
            $backupLooksComplete = Test-BackupComplete $arguments[1]
        }

        $exitCode = $null
        try {
            $exitCode = $process.ExitCode
        } catch {
            $exitCode = $null
        }

        if ($null -ne $script:LastResultCode) {
            $commandSucceeded = ($script:LastResultCode -eq 0)
        } else {
            $commandSucceeded = ($null -ne $exitCode -and [int]$exitCode -eq 0)
        }

        if ($commandSucceeded -or $backupLooksComplete) {
            Set-OperationProgress 100 "$label complete"
            $script:LastRunOK = $true
            if (-not $commandSucceeded -and $backupLooksComplete) {
                if ($null -eq $exitCode) {
                    Add-Log "Backup files match the expected partition sizes, so this backup is being treated as complete even though the helper process did not return a readable exit code."
                } else {
                    Add-Log "Backup files match the expected partition sizes, so this backup is being treated as complete even though the helper process returned exit code $exitCode."
                }
            }
            Add-Log "$label complete."
        } else {
            $script:StatusLabel.Text = "$label failed"
            if ($null -ne $script:LastResultCode) {
                $exitText = [string]$script:LastResultCode
            } elseif ($null -ne $exitCode) {
                $exitText = [string]$exitCode
            } else {
                $exitText = "unknown"
            }
            Add-Log "$label failed with exit code $exitText."
            $message = "$label failed. Check the log in this window."
            if (-not [string]::IsNullOrWhiteSpace($script:LastErrorLine)) {
                $message = "$label failed." + [Environment]::NewLine + [Environment]::NewLine + $script:LastErrorLine
            }
            [System.Windows.Forms.MessageBox]::Show($message, "KarmaKontroller", "OK", "Error") | Out-Null
        }
    } catch {
        $script:StatusLabel.Text = "$label failed"
        Add-Log $_.Exception.Message
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "KarmaKontroller", "OK", "Error") | Out-Null
    } finally {
        $script:OperationRunning = $false
        $script:ActiveLabel = ""
        $script:ActiveArguments = @()
        Set-ControlsEnabled $true
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Default-PatchedPath($sourcePath) {
    $dir = Get-DefaultPatchDir
    if ([string]::IsNullOrWhiteSpace($sourcePath)) {
        return [System.IO.Path]::Combine($dir, "system.karma-patched.img")
    }
    $name = [System.IO.Path]::GetFileNameWithoutExtension($sourcePath)
    $ext = [System.IO.Path]::GetExtension($sourcePath)
    if ([string]::IsNullOrWhiteSpace($ext)) { $ext = ".img" }
    return [System.IO.Path]::Combine($dir, "$name.karma-patched$ext")
}

function Show-FlashConfirmation($imagePath) {
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = "Confirm Flash"
    $dialog.StartPosition = "CenterParent"
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ClientSize = New-Object System.Drawing.Size(560, 260)
    Set-WindowIcon $dialog

    $title = New-Label "Flash controller system partition?" 18 16 520 26
    $title.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)

    $message = New-Label ("Flashing rewrites the controller system partition." + [Environment]::NewLine + [Environment]::NewLine + "If the controller is unplugged, loses power, or this program is closed or killed while flashing, the controller may become unrecoverable." + [Environment]::NewLine + [Environment]::NewLine + "Flashing may take a long time. Continue only if this is your controller and you have a backup.") 20 54 520 108
    $image = New-Label $imagePath 20 168 520 34

    $continue = New-Button "Continue" 350 216 90
    $cancel = New-Button "Cancel" 450 216 90
    $continue.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dialog.AcceptButton = $cancel
    $dialog.CancelButton = $cancel
    $dialog.Controls.AddRange(@($title, $message, $image, $continue, $cancel))
    $dialog.Add_Shown({ $cancel.Focus() })

    $result = $dialog.ShowDialog($script:Form)
    $dialog.Dispose()
    return $result -eq [System.Windows.Forms.DialogResult]::OK
}

function Show-DataRestoreConfirmation($imagePath) {
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = "Confirm Data Restore"
    $dialog.StartPosition = "CenterParent"
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ClientSize = New-Object System.Drawing.Size(580, 292)
    Set-WindowIcon $dialog

    $title = New-Label "Restore controller data partition?" 18 16 540 26
    $title.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)

    $message = New-Label ("Restoring data rewrites the controller /data partition." + [Environment]::NewLine + [Environment]::NewLine + "This can recover a controller that boots to a black screen after /data was wiped, but it also replaces current controller settings and pairing data." + [Environment]::NewLine + [Environment]::NewLine + "Use a valid dataBU.img from this same controller. Keep USB and power connected until the restore completes.") 20 54 540 132
    $image = New-Label $imagePath 20 194 540 34

    $continue = New-Button "Continue" 370 248 90
    $cancel = New-Button "Cancel" 470 248 90
    $continue.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dialog.AcceptButton = $cancel
    $dialog.CancelButton = $cancel
    $dialog.Controls.AddRange(@($title, $message, $image, $continue, $cancel))
    $dialog.Add_Shown({ $cancel.Focus() })

    $result = $dialog.ShowDialog($script:Form)
    $dialog.Dispose()
    return $result -eq [System.Windows.Forms.DialogResult]::OK
}

$form = New-Object System.Windows.Forms.Form
$script:Form = $form
$form.Text = "KarmaKontroller"
$form.StartPosition = "CenterScreen"
$form.Size = New-Object System.Drawing.Size(760, 860)
$form.MinimumSize = New-Object System.Drawing.Size(760, 860)
Set-WindowIcon $form

$title = New-Label "KarmaKontroller Image Tools" 18 14 420 28
$title.Font = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($title)

$script:DefaultBackupDir = Get-DefaultBackupDir
$script:DefaultPatchDir = Get-DefaultPatchDir

$sourceLabel = New-Label "Original system.img" 20 58 180 22
$sourceBox = New-TextBox 20 82 590
$sourceBrowse = New-Button "Browse..." 620 80 100
$form.Controls.AddRange(@($sourceLabel, $sourceBox, $sourceBrowse))

$destLabel = New-Label "Patched output image" 20 118 180 22
$destBox = New-TextBox 20 142 590
$destBrowse = New-Button "Save As..." 620 140 100
$patchButton = New-Button "Patch Image" 20 178 150
$form.Controls.AddRange(@($destLabel, $destBox, $destBrowse, $patchButton))

$flashLabel = New-Label "System image to flash" 20 226 180 22
$flashBox = New-TextBox 20 250 590
$flashBrowse = New-Button "Browse..." 620 248 100
$flashButton = New-Button "Flash System" 20 286 150
$form.Controls.AddRange(@($flashLabel, $flashBox, $flashBrowse, $flashButton))

$dataRestoreLabel = New-Label "Data image to restore" 20 334 180 22
$dataRestoreBox = New-TextBox 20 358 590
$script:DataRestoreBox = $dataRestoreBox
$dataRestoreBrowse = New-Button "Browse..." 620 356 100
$dataRestoreButton = New-Button "Restore Data" 20 394 150
$form.Controls.AddRange(@($dataRestoreLabel, $dataRestoreBox, $dataRestoreBrowse, $dataRestoreButton))

$backupLabel = New-Label "Backup folder" 20 442 180 22
$backupBox = New-TextBox 20 466 590
$backupBox.Text = $script:DefaultBackupDir
$backupBrowse = New-Button "Browse..." 620 464 100
$backupPartitionsLabel = New-Label "Backup partitions" 20 502 180 22
$bootloaderCheck = New-CheckBox "Bootloader" 20 526 110
$bootCheck = New-CheckBox "Boot" 140 526 80
$recoveryCheck = New-CheckBox "Recovery" 240 526 100
$systemCheck = New-CheckBox "System" 360 526 90
$dataCheck = New-CheckBox "Data" 470 526 80
$goproCheck = New-CheckBox "GoPro" 570 526 90
$script:BackupCheckBoxes = [ordered]@{
    bootloader = $bootloaderCheck
    boot = $bootCheck
    recovery = $recoveryCheck
    system = $systemCheck
    data = $dataCheck
    gopro = $goproCheck
}
$backupButton = New-Button "Backup Controller" 20 564 150
$form.Controls.AddRange(@($backupLabel, $backupBox, $backupBrowse, $backupPartitionsLabel, $bootloaderCheck, $bootCheck, $recoveryCheck, $systemCheck, $dataCheck, $goproCheck, $backupButton))

$statusLabel = New-Label "Ready" 190 570 520 22
$script:StatusLabel = $statusLabel
$form.Controls.Add($statusLabel)

$progressBar = New-Object System.Windows.Forms.ProgressBar
$script:ProgressBar = $progressBar
$progressBar.Location = New-Object System.Drawing.Point(20, 600)
$progressBar.Size = New-Object System.Drawing.Size(700, 22)
$progressBar.Minimum = 0
$progressBar.Maximum = 100
$progressBar.Value = 0
$form.Controls.Add($progressBar)

$progressText = New-Label "0% - Ready" 20 628 700 22
$script:ProgressText = $progressText
$form.Controls.Add($progressText)

$logBox = New-Object System.Windows.Forms.TextBox
$script:LogBox = $logBox
$logBox.Location = New-Object System.Drawing.Point(20, 656)
$logBox.Size = New-Object System.Drawing.Size(700, 150)
$logBox.Multiline = $true
$logBox.ScrollBars = "Vertical"
$logBox.ReadOnly = $true
$form.Controls.Add($logBox)

$script:ActionControls = @($sourceBrowse, $destBrowse, $patchButton, $flashBrowse, $flashButton, $dataRestoreBrowse, $dataRestoreButton, $backupBrowse, $bootloaderCheck, $bootCheck, $recoveryCheck, $systemCheck, $dataCheck, $goproCheck, $backupButton)
Set-DataRestoreFromBackupFolder

$form.Add_FormClosing({
    param($sender, $eventArgs)
    if ($script:OperationRunning) {
        [System.Windows.Forms.MessageBox]::Show("An operation is still running. Wait for it to finish before closing this window.", "KarmaKontroller", "OK", "Warning") | Out-Null
        $eventArgs.Cancel = $true
    }
})

$sourceBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = "Select original system.img"
    $dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*"
    $dialog.InitialDirectory = $script:DefaultPatchDir
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $sourceBox.Text = $dialog.FileName
        $destBox.Text = Default-PatchedPath $dialog.FileName
    }
})

$destBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.SaveFileDialog
    $dialog.Title = "Save patched system image"
    $dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*"
    $dialog.OverwritePrompt = $true
    $dialog.InitialDirectory = $script:DefaultPatchDir
    if (-not [string]::IsNullOrWhiteSpace($destBox.Text)) {
        $dialog.FileName = $destBox.Text
    } elseif (-not [string]::IsNullOrWhiteSpace($sourceBox.Text)) {
        $dialog.FileName = Default-PatchedPath $sourceBox.Text
    }
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $destBox.Text = $dialog.FileName
    }
})

$patchButton.Add_Click({
    if (-not (Test-Path $sourceBox.Text)) {
        [System.Windows.Forms.MessageBox]::Show("Select an original system.img first.", "KarmaKontroller", "OK", "Warning") | Out-Null
        return
    }
    if ([string]::IsNullOrWhiteSpace($destBox.Text)) {
        $destBox.Text = Default-PatchedPath $sourceBox.Text
    }
    Invoke-Karma -arguments @("--patch-system", $sourceBox.Text, $destBox.Text) -label "Patch"
    if ($script:LastRunOK) {
        $flashBox.Text = $destBox.Text
    }
})

$flashBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = "Select system image to flash"
    $dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*"
    $dialog.InitialDirectory = $script:DefaultPatchDir
    if (-not [string]::IsNullOrWhiteSpace($flashBox.Text)) {
        $dialog.FileName = $flashBox.Text
    }
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $flashBox.Text = $dialog.FileName
    }
})

$flashButton.Add_Click({
    if (-not (Test-Path $flashBox.Text)) {
        [System.Windows.Forms.MessageBox]::Show("Select a system image to flash first.", "KarmaKontroller", "OK", "Warning") | Out-Null
        return
    }
    if (-not (Require-ValidDataBackupBeforeSystemFlash)) { return }
    if (-not (Show-FlashConfirmation $flashBox.Text)) { return }
    Invoke-Karma -arguments @("--flash-system", $flashBox.Text, (Get-DataBackupPath)) -label "Flash"
})

$dataRestoreBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = "Select data image to restore"
    $dialog.Filter = "Karma data backup (*.img)|*.img|All files (*.*)|*.*"
    if (-not [string]::IsNullOrWhiteSpace($backupBox.Text) -and (Test-Path -LiteralPath $backupBox.Text)) {
        $dialog.InitialDirectory = $backupBox.Text
    } else {
        $dialog.InitialDirectory = $script:DefaultBackupDir
    }
    if (-not [string]::IsNullOrWhiteSpace($dataRestoreBox.Text)) {
        $dialog.FileName = $dataRestoreBox.Text
    } else {
        $candidate = Get-DataBackupPath
        if (Test-Path -LiteralPath $candidate) {
            $dialog.FileName = $candidate
        }
    }
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $dataRestoreBox.Text = $dialog.FileName
    }
})

$dataRestoreButton.Add_Click({
    $check = Test-Ext4Image $dataRestoreBox.Text ([int64]0x51bf0000)
    if (-not $check.Ok) {
        [System.Windows.Forms.MessageBox]::Show("Select a valid dataBU.img first." + [Environment]::NewLine + [Environment]::NewLine + $check.Message, "KarmaKontroller", "OK", "Warning") | Out-Null
        return
    }
    if (-not (Show-DataRestoreConfirmation $dataRestoreBox.Text)) { return }
    Invoke-Karma -arguments @("--flash-data", $dataRestoreBox.Text) -label "Restore Data"
})

$backupBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Choose a folder for controller partition backups"
    $dialog.ShowNewFolderButton = $true
    if (-not [string]::IsNullOrWhiteSpace($backupBox.Text) -and (Test-Path -LiteralPath $backupBox.Text)) {
        $dialog.SelectedPath = $backupBox.Text
    } else {
        $dialog.SelectedPath = $script:DefaultBackupDir
    }
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $backupBox.Text = $dialog.SelectedPath
        Set-DataRestoreFromBackupFolder
    }
})

$backupButton.Add_Click({
    if ([string]::IsNullOrWhiteSpace($backupBox.Text)) {
        [System.Windows.Forms.MessageBox]::Show("Choose a backup folder first.", "KarmaKontroller", "OK", "Warning") | Out-Null
        return
    }
    $selectedNames = @(Get-SelectedBackupNames)
    if ($selectedNames.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Choose at least one partition to back up.", "KarmaKontroller", "OK", "Warning") | Out-Null
        return
    }
    if ($selectedNames -notcontains "data") {
        $dataAnswer = [System.Windows.Forms.MessageBox]::Show("The Data partition is the recovery safety net if the controller resets or boots to a black screen." + [Environment]::NewLine + [Environment]::NewLine + "Backups without Data cannot be used as the required preflight backup before flashing system." + [Environment]::NewLine + [Environment]::NewLine + "Continue without backing up Data?", "KarmaKontroller", "YesNo", "Warning", "Button2")
        if ($dataAnswer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    }
    Set-ActiveBackupSteps $selectedNames
    $partitionText = ($selectedNames -join ", ")
    $message = "Back up these controller partitions:" + [Environment]::NewLine + $partitionText + [Environment]::NewLine + [Environment]::NewLine + "Backup folder:" + [Environment]::NewLine + $backupBox.Text + [Environment]::NewLine + [Environment]::NewLine + "Keep the controller connected until this finishes."
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "Confirm Backup", "YesNo", "Warning", "Button2")
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    Invoke-Karma -arguments (@("--backup-controller", $backupBox.Text) + $selectedNames) -label "Backup"
    if ($script:LastRunOK) {
        Set-DataRestoreFromBackupFolder
    }
})

[void]$form.ShowDialog()
