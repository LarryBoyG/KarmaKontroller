//go:build windows

package main

import (
	"bytes"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

const (
	messageBoxOK          = 0x00000000
	messageBoxYesNo       = 0x00000004
	messageBoxIconError   = 0x00000010
	messageBoxIconWarning = 0x00000030
	messageBoxIconInfo    = 0x00000040
	messageBoxDefButton2  = 0x00000100
	messageBoxIDYes       = 6
	processQueryInfo      = 0x0400
	processQueryLimited   = 0x1000
)

var (
	messageBoxW              = syscall.NewLazyDLL("user32.dll").NewProc("MessageBoxW")
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	openProcessProc          = kernel32.NewProc("OpenProcess")
	closeHandleProc          = kernel32.NewProc("CloseHandle")
	getProcessIoCountersProc = kernel32.NewProc("GetProcessIoCounters")
)

func backupControllerToFolder(folder string, partitionNames []string, progress patchProgress) error {
	if err := os.MkdirAll(folder, 0755); err != nil {
		return err
	}
	updateExe, err := resolveUpdateExe()
	if err != nil {
		return err
	}

	steps, err := selectedBackupSteps(partitionNames)
	if err != nil {
		return err
	}
	ranges := backupProgressRanges(steps, 99)

	reportPatchProgress(progress, 0, "Starting controller backup")
	for i, step := range steps {
		stage := ranges[i]
		stepLabel := fmt.Sprintf("%s (%d/%d)", step.name, i+1, len(steps))
		outPath := filepath.Join(folder, step.file)
		tmpPath := outPath + ".tmp"
		_ = os.Remove(tmpPath)
		reportPatchProgress(progress, stage.start, "Backing up "+stepLabel)
		if err := runUpdateCommandMonitored(updateExe, []string{"mread", "store", step.name, "normal", step.size, tmpPath}, func(pid int) {
			current := fileSize(tmpPath)
			if _, writeBytes, ok := processTransferCounts(pid); ok && int64(writeBytes) > current {
				current = int64(writeBytes)
			}
			if current > step.bytes {
				current = step.bytes
			}
			reportPatchProgress(progress, stage.percent(current, step.bytes), "Backing up "+stepLabel)
		}); err != nil {
			_ = os.Remove(tmpPath)
			return fmt.Errorf("backup %s: %w", step.name, err)
		}
		info, err := os.Stat(tmpPath)
		if err != nil {
			return fmt.Errorf("backup %s: update.exe did not create %s; make sure the controller is connected by USB in update mode and the WorldCup driver is installed", step.name, tmpPath)
		}
		if info.Size() < step.bytes {
			_ = os.Remove(tmpPath)
			return fmt.Errorf("backup %s: update.exe created an incomplete file (%d of %d bytes)", step.name, info.Size(), step.bytes)
		}
		_ = os.Remove(outPath)
		if err := os.Rename(tmpPath, outPath); err != nil {
			_ = os.Remove(tmpPath)
			return fmt.Errorf("finish backup %s: %w", step.name, err)
		}
		reportPatchProgress(progress, stage.end, "Backed up "+stepLabel)
	}
	reportPatchProgress(progress, 100, "Backup complete")
	return nil
}

func flashSystemImageWithDataBackup(image, dataBackup string, progress patchProgress) error {
	reportPatchProgress(progress, 0, "Validating data backup")
	if err := validateDataImage(dataBackup); err != nil {
		return fmt.Errorf("data backup preflight failed: %w", err)
	}
	return flashSystemImage(image, progress)
}

func flashSystemImage(image string, progress patchProgress) error {
	reportPatchProgress(progress, 0, "Validating system image")
	if err := validateSystemImagePath(image); err != nil {
		return fmt.Errorf("system image preflight failed: %w", err)
	}
	return flashPartitionImage("system", image, progress)
}

func flashDataImage(image string, progress patchProgress) error {
	reportPatchProgress(progress, 0, "Validating data image")
	if err := validateDataImage(image); err != nil {
		return err
	}
	return flashPartitionImage("data", image, progress)
}

func validateDataImage(image string) error {
	info, err := os.Stat(image)
	if err != nil {
		return err
	}
	if info.IsDir() {
		return fmt.Errorf("%s is a directory, not a data image", image)
	}

	const expectedDataImageBytes int64 = 0x51bf0000
	if info.Size() != expectedDataImageBytes {
		return fmt.Errorf("data image is %d bytes; expected %d bytes for the Karma data partition", info.Size(), expectedDataImageBytes)
	}

	ext4, err := openExt4ImageReadOnly(image)
	if err != nil {
		return fmt.Errorf("data image is not a valid ext4 filesystem: %w", err)
	}
	defer ext4.close()
	return nil
}

func flashPartitionImage(partition, image string, progress patchProgress) error {
	updateExe, err := resolveUpdateExe()
	if err != nil {
		return err
	}
	info, err := os.Stat(image)
	if err != nil {
		return err
	}
	if info.IsDir() {
		return fmt.Errorf("%s is a directory, not an image file", image)
	}
	totalBytes := info.Size()
	reportPatchProgress(progress, 1, "Starting "+partition+" flash")
	if err := runUpdateCommandMonitored(updateExe, []string{"partition", partition, image}, func(pid int) {
		readBytes, _, ok := processTransferCounts(pid)
		if !ok {
			return
		}
		reportPatchProgress(progress, percentFromBytes(int64(readBytes), totalBytes, 99), "Flashing "+partition)
	}); err != nil {
		return err
	}
	reportPatchProgress(progress, 100, partition+" flash complete")
	return nil
}

func launchPatchWindow() error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	dir := karmaKontrollerDir()
	if err := os.MkdirAll(dir, 0755); err != nil {
		return err
	}
	script, err := readPatchAsset("KarmaKontroller-GUI.ps1")
	if err != nil {
		return err
	}
	scriptPath := filepath.Join(dir, "KarmaKontroller-GUI.ps1")
	if err := os.WriteFile(scriptPath, script, 0644); err != nil {
		return err
	}

	cmd := exec.Command("powershell.exe", "-NoProfile", "-STA", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Exe", exe)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	logPath := filepath.Join(dir, "gui.log")
	errPath := filepath.Join(dir, "gui.err")
	if stdout, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644); err == nil {
		defer stdout.Close()
		cmd.Stdout = stdout
	}
	if stderr, err := os.OpenFile(errPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644); err == nil {
		defer stderr.Close()
		cmd.Stderr = stderr
	}
	log.Printf("opening image tools window with %s", scriptPath)
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Wait()
}

/* GUI script lives in assets/KarmaKontroller-GUI.ps1.
param([string]$Exe)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:LastRunOK = $false

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

function Invoke-Karma($arguments, $label) {
    $script:LastRunOK = $false
    Set-ControlsEnabled $false
    $script:StatusLabel.Text = "$label running..."
    $script:Form.Refresh()
    Add-Log ""
    Add-Log ("> KarmaKontroller.exe " + ($arguments -join " "))
    try {
        $output = & $Exe @arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($output.Trim().Length -gt 0) {
            Add-Log $output.Trim()
        }
        if ($exitCode -eq 0) {
            $script:StatusLabel.Text = "$label complete"
            $script:LastRunOK = $true
            Add-Log "$label complete."
        } else {
            $script:StatusLabel.Text = "$label failed"
            [System.Windows.Forms.MessageBox]::Show("$label failed. Check the log in this window.", "KarmaKontroller", "OK", "Error") | Out-Null
        }
    } catch {
        $script:StatusLabel.Text = "$label failed"
        Add-Log $_.Exception.Message
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "KarmaKontroller", "OK", "Error") | Out-Null
    } finally {
        Set-ControlsEnabled $true
    }
}

function Default-PatchedPath($sourcePath) {
    if ([string]::IsNullOrWhiteSpace($sourcePath)) { return "" }
    $dir = [System.IO.Path]::GetDirectoryName($sourcePath)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($sourcePath)
    $ext = [System.IO.Path]::GetExtension($sourcePath)
    if ([string]::IsNullOrWhiteSpace($ext)) { $ext = ".img" }
    return [System.IO.Path]::Combine($dir, "$name.karma-patched$ext")
}

$form = New-Object System.Windows.Forms.Form
$script:Form = $form
$form.Text = "KarmaKontroller"
$form.StartPosition = "CenterScreen"
$form.Size = New-Object System.Drawing.Size(760, 560)
$form.MinimumSize = New-Object System.Drawing.Size(760, 560)

$title = New-Label "KarmaKontroller Image Tools" 18 14 420 28
$title.Font = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($title)

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

$backupLabel = New-Label "Backup folder" 20 334 180 22
$backupBox = New-TextBox 20 358 590
$backupBrowse = New-Button "Browse..." 620 356 100
$backupButton = New-Button "Backup Controller" 20 394 150
$form.Controls.AddRange(@($backupLabel, $backupBox, $backupBrowse, $backupButton))

$statusLabel = New-Label "Ready" 190 400 520 22
$script:StatusLabel = $statusLabel
$form.Controls.Add($statusLabel)

$logBox = New-Object System.Windows.Forms.TextBox
$script:LogBox = $logBox
$logBox.Location = New-Object System.Drawing.Point(20, 430)
$logBox.Size = New-Object System.Drawing.Size(700, 80)
$logBox.Multiline = $true
$logBox.ScrollBars = "Vertical"
$logBox.ReadOnly = $true
$form.Controls.Add($logBox)

$script:ActionControls = @($sourceBrowse, $destBrowse, $patchButton, $flashBrowse, $flashButton, $backupBrowse, $backupButton)

$sourceBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = "Select original system.img"
    $dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*"
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $sourceBox.Text = $dialog.FileName
        if ([string]::IsNullOrWhiteSpace($destBox.Text)) {
            $destBox.Text = Default-PatchedPath $dialog.FileName
        }
    }
})

$destBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.SaveFileDialog
    $dialog.Title = "Save patched system image"
    $dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*"
    $dialog.OverwritePrompt = $true
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
    $message = "Flash this image to the controller system partition?" + [Environment]::NewLine + [Environment]::NewLine + $flashBox.Text + [Environment]::NewLine + [Environment]::NewLine + "Only continue if you have a backup."
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "Confirm Flash", "YesNo", "Warning", "Button2")
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    Invoke-Karma -arguments @("--flash-system", $flashBox.Text) -label "Flash"
})

$backupBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Choose a folder for controller partition backups"
    $dialog.ShowNewFolderButton = $true
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $backupBox.Text = $dialog.SelectedPath
    }
})

$backupButton.Add_Click({
    if ([string]::IsNullOrWhiteSpace($backupBox.Text)) {
        [System.Windows.Forms.MessageBox]::Show("Choose a backup folder first.", "KarmaKontroller", "OK", "Warning") | Out-Null
        return
    }
    $message = "Back up controller partitions to:" + [Environment]::NewLine + [Environment]::NewLine + $backupBox.Text + [Environment]::NewLine + [Environment]::NewLine + "Keep the controller connected until this finishes."
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "Confirm Backup", "YesNo", "Warning", "Button2")
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    Invoke-Karma -arguments @("--backup-controller", $backupBox.Text) -label "Backup"
})

[void]$form.ShowDialog()
*/

func patchedImageDefaultPath(source string) string {
	ext := filepath.Ext(source)
	base := strings.TrimSuffix(filepath.Base(source), ext)
	if ext == "" {
		ext = ".img"
	}
	return filepath.Join(filepath.Dir(source), base+".karma-patched"+ext)
}

func resolveUpdateExe() (string, error) {
	var candidates []string
	if exe, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exe)
		candidates = append(candidates,
			filepath.Join(exeDir, "cmdUpdTool2", "update.exe"),
			filepath.Join(exeDir, "tools", "cmdUpdTool2", "update.exe"),
			filepath.Join(exeDir, "update.exe"),
		)
	}
	candidates = append(candidates,
		filepath.Join(karmaKontrollerDir(), "cmdUpdTool2", "update.exe"),
		filepath.Join(karmaKontrollerDir(), "update.exe"),
		`C:\Users\lawre\Downloads\Karma Update\cmdUpdTool2\update.exe`,
		`C:\update.exe`,
	)
	for _, candidate := range candidates {
		if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
			return candidate, nil
		}
	}

	selected, ok := chooseOpenFile("Select update.exe", "Amlogic update tool (update.exe)|update.exe|Executable (*.exe)|*.exe|All files (*.*)|*.*", `C:\Users\lawre\Downloads\Karma Update\cmdUpdTool2\update.exe`)
	if !ok {
		return "", fmt.Errorf("update.exe was not found. Put it in the cmdUpdTool2 folder, put it at C:\\update.exe, or select it when prompted")
	}
	return selected, nil
}

func runUpdateCommand(updateExe string, args ...string) error {
	return runUpdateCommandMonitored(updateExe, args, nil)
}

func runUpdateCommandMonitored(updateExe string, args []string, monitor func(pid int)) error {
	log.Printf("running %s %s", updateExe, strings.Join(args, " "))
	cmd := exec.Command(updateExe, args...)
	cmd.Dir = filepath.Dir(updateExe)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	var output bytes.Buffer
	cmd.Stdout = &output
	cmd.Stderr = &output

	if err := cmd.Start(); err != nil {
		return err
	}
	done := make(chan error, 1)
	go func() {
		done <- cmd.Wait()
	}()

	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()

	var err error
	for {
		select {
		case err = <-done:
			goto finished
		case <-ticker.C:
			if monitor != nil && cmd.Process != nil {
				monitor(cmd.Process.Pid)
			}
		}
	}

finished:
	outputBytes := output.Bytes()
	if len(outputBytes) > 0 {
		log.Printf("update.exe output:\n%s", bytes.TrimSpace(outputBytes))
	}
	if outputErr := updateToolOutputError(outputBytes); outputErr != nil {
		return outputErr
	}
	if err != nil {
		if len(outputBytes) > 0 {
			return fmt.Errorf("%w\n\n%s", err, bytes.TrimSpace(outputBytes))
		}
		return err
	}
	return nil
}

func updateToolOutputError(output []byte) error {
	for _, line := range strings.Split(string(output), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(strings.ToUpper(line), "ERR:") {
			if strings.Contains(strings.ToLower(line), "can not find dev0 device") {
				return fmt.Errorf("update.exe could not find the controller USB device. Connect the controller by USB in update mode and make sure the WorldCup driver is installed")
			}
			return fmt.Errorf("update.exe reported an error: %s", line)
		}
	}
	return nil
}

type processIOCounters struct {
	readOperationCount  uint64
	writeOperationCount uint64
	otherOperationCount uint64
	readTransferCount   uint64
	writeTransferCount  uint64
	otherTransferCount  uint64
}

func processTransferCounts(pid int) (uint64, uint64, bool) {
	handle, _, _ := openProcessProc.Call(processQueryInfo|processQueryLimited, 0, uintptr(uint32(pid)))
	if handle == 0 {
		return 0, 0, false
	}
	defer closeHandleProc.Call(handle)

	var counters processIOCounters
	ok, _, _ := getProcessIoCountersProc.Call(handle, uintptr(unsafe.Pointer(&counters)))
	if ok == 0 {
		return 0, 0, false
	}
	return counters.readTransferCount, counters.writeTransferCount, true
}

type backupStep struct {
	name  string
	size  string
	file  string
	bytes int64
}

func controllerBackupSteps() []backupStep {
	return []backupStep{
		{name: "bootloader", size: "0x400000", file: "bootloaderBU.img", bytes: backupSizeBytes("0x400000")},
		{name: "boot", size: "0x2000000", file: "bootBU.img", bytes: backupSizeBytes("0x2000000")},
		{name: "recovery", size: "0x2000000", file: "recoveryBU.img", bytes: backupSizeBytes("0x2000000")},
		{name: "system", size: "0x40000000", file: "systemBU.img", bytes: backupSizeBytes("0x40000000")},
		{name: "data", size: "0x51bf0000", file: "dataBU.img", bytes: backupSizeBytes("0x51bf0000")},
		{name: "gopro", size: "0x20000000", file: "goproBU.img", bytes: backupSizeBytes("0x20000000")},
	}
}

func selectedBackupSteps(partitionNames []string) ([]backupStep, error) {
	all := controllerBackupSteps()
	if len(partitionNames) == 0 {
		return all, nil
	}

	byName := make(map[string]backupStep, len(all))
	for _, step := range all {
		byName[step.name] = step
	}

	seen := make(map[string]bool)
	var selected []backupStep
	for _, name := range partitionNames {
		key := normalizeBackupPartitionName(name)
		step, ok := byName[key]
		if !ok {
			return nil, fmt.Errorf("unknown backup partition %q; choose bootloader, boot, recovery, system, data, or gopro", name)
		}
		if seen[key] {
			continue
		}
		seen[key] = true
		selected = append(selected, step)
	}
	if len(selected) == 0 {
		return nil, fmt.Errorf("select at least one partition to back up")
	}
	return selected, nil
}

func normalizeBackupPartitionName(name string) string {
	key := strings.ToLower(strings.TrimSpace(name))
	key = strings.TrimSuffix(key, ".img")
	key = strings.TrimSuffix(key, "bu")
	key = strings.ReplaceAll(key, "_", "")
	key = strings.ReplaceAll(key, "-", "")
	switch key {
	case "bootloader", "loader":
		return "bootloader"
	case "boot":
		return "boot"
	case "recovery":
		return "recovery"
	case "system":
		return "system"
	case "data":
		return "data"
	case "gopro", "go", "media":
		return "gopro"
	default:
		return key
	}
}

type backupProgressRange struct {
	start int
	end   int
}

func (r backupProgressRange) percent(done, total int64) int {
	if total <= 0 || r.end <= r.start {
		return r.start
	}
	if done < 0 {
		done = 0
	}
	if done > total {
		done = total
	}
	percent := r.start + int((done*int64(r.end-r.start))/total)
	if percent > r.end {
		return r.end
	}
	return percent
}

func backupProgressRanges(steps []backupStep, maxPercent int) []backupProgressRange {
	ranges := make([]backupProgressRange, len(steps))
	if len(steps) == 0 || maxPercent <= 0 {
		return ranges
	}

	const minStepPercent = 5
	totalBytes := int64(0)
	for _, step := range steps {
		totalBytes += step.bytes
	}

	current := 0
	minBudget := minStepPercent * len(steps)
	if totalBytes <= 0 || minBudget >= maxPercent {
		for i := range steps {
			end := ((i + 1) * maxPercent) / len(steps)
			ranges[i] = backupProgressRange{start: current, end: end}
			current = end
		}
		return ranges
	}

	variableBudget := maxPercent - minBudget
	for i, step := range steps {
		width := minStepPercent
		if i == len(steps)-1 {
			width = maxPercent - current
		} else {
			width += int((step.bytes * int64(variableBudget)) / totalBytes)
			if width < 1 {
				width = 1
			}
			if current+width > maxPercent {
				width = maxPercent - current
			}
		}
		ranges[i] = backupProgressRange{start: current, end: current + width}
		current += width
	}
	return ranges
}

func backupSizeBytes(size string) int64 {
	value, err := strconv.ParseInt(strings.TrimPrefix(strings.ToLower(size), "0x"), 16, 64)
	if err != nil {
		return 0
	}
	return value
}

func fileSize(path string) int64 {
	info, err := os.Stat(path)
	if err != nil {
		return 0
	}
	return info.Size()
}

func percentFromBytes(done, total int64, maxPercent int) int {
	if total <= 0 {
		return 0
	}
	if done < 0 {
		done = 0
	}
	if done > total {
		done = total
	}
	percent := int((done * int64(maxPercent)) / total)
	if percent > maxPercent {
		return maxPercent
	}
	return percent
}

func chooseOpenFile(title, filter, initialPath string) (string, bool) {
	initialDir, fileName := dialogInitialParts(initialPath)
	script := fmt.Sprintf(`
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
$dialog = New-Object System.Windows.Forms.OpenFileDialog
$dialog.Title = '%s'
$dialog.Filter = '%s'
$dialog.CheckFileExists = $true
if ('%s' -ne '') { $dialog.InitialDirectory = '%s' }
if ('%s' -ne '') { $dialog.FileName = '%s' }
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dialog.FileName }
`, psQuote(title), psQuote(filter), psQuote(initialDir), psQuote(initialDir), psQuote(fileName), psQuote(fileName))
	return runPowerShellPicker(script)
}

func chooseSaveFile(title, filter, initialPath string) (string, bool) {
	initialDir, fileName := dialogInitialParts(initialPath)
	script := fmt.Sprintf(`
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
$dialog = New-Object System.Windows.Forms.SaveFileDialog
$dialog.Title = '%s'
$dialog.Filter = '%s'
$dialog.OverwritePrompt = $true
if ('%s' -ne '') { $dialog.InitialDirectory = '%s' }
if ('%s' -ne '') { $dialog.FileName = '%s' }
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dialog.FileName }
`, psQuote(title), psQuote(filter), psQuote(initialDir), psQuote(initialDir), psQuote(fileName), psQuote(fileName))
	return runPowerShellPicker(script)
}

func chooseFolder(description string) (string, bool) {
	script := fmt.Sprintf(`
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = '%s'
$dialog.ShowNewFolderButton = $true
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dialog.SelectedPath }
`, psQuote(description))
	return runPowerShellPicker(script)
}

func dialogInitialParts(path string) (string, string) {
	if strings.TrimSpace(path) == "" {
		return "", ""
	}
	info, err := os.Stat(path)
	if err == nil && info.IsDir() {
		return path, ""
	}
	return filepath.Dir(path), filepath.Base(path)
}

func runPowerShellPicker(script string) (string, bool) {
	cmd := exec.Command("powershell.exe", "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-Command", script)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	output, err := cmd.Output()
	if err != nil {
		log.Printf("dialog failed: %v", err)
		return "", false
	}
	result := strings.TrimSpace(string(output))
	if result == "" {
		return "", false
	}
	return result, true
}

func psQuote(value string) string {
	return strings.ReplaceAll(value, "'", "''")
}

func showMessage(title, text string, flags uintptr) {
	titlePtr, _ := syscall.UTF16PtrFromString(title)
	textPtr, _ := syscall.UTF16PtrFromString(text)
	_, _, _ = messageBoxW.Call(
		0,
		uintptr(unsafe.Pointer(textPtr)),
		uintptr(unsafe.Pointer(titlePtr)),
		messageBoxOK|flags,
	)
}

func confirmMessage(title, text string) bool {
	titlePtr, _ := syscall.UTF16PtrFromString(title)
	textPtr, _ := syscall.UTF16PtrFromString(text)
	ret, _, _ := messageBoxW.Call(
		0,
		uintptr(unsafe.Pointer(textPtr)),
		uintptr(unsafe.Pointer(titlePtr)),
		messageBoxYesNo|messageBoxIconWarning|messageBoxDefButton2,
	)
	return ret == messageBoxIDYes
}
