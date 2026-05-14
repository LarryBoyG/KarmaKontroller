//go:build windows

package main

import (
	"fmt"
	"os"
)

func runCLI(args []string) (bool, error) {
	if len(args) == 0 {
		return false, nil
	}

	switch args[0] {
	case "--patch-system":
		if len(args) != 3 {
			return true, fmt.Errorf("usage: KarmaKontroller.exe --patch-system <source-system.img> <patched-system.img>")
		}
		return true, patchSystemImage(args[1], args[2], cliProgress)
	case "--backup-controller":
		if len(args) < 2 {
			return true, fmt.Errorf("usage: KarmaKontroller.exe --backup-controller <backup-folder> [bootloader] [boot] [recovery] [system] [data] [gopro]")
		}
		return true, backupControllerToFolder(args[1], args[2:], cliProgress)
	case "--flash-system":
		if len(args) != 3 {
			return true, fmt.Errorf("usage: KarmaKontroller.exe --flash-system <system.img> <dataBU.img>")
		}
		return true, flashSystemImageWithDataBackup(args[1], args[2], cliProgress)
	case "--flash-data":
		if len(args) != 2 {
			return true, fmt.Errorf("usage: KarmaKontroller.exe --flash-data <data.img>")
		}
		return true, flashDataImage(args[1], cliProgress)
	case "--validate-data-image":
		if len(args) != 2 {
			return true, fmt.Errorf("usage: KarmaKontroller.exe --validate-data-image <data.img>")
		}
		return true, validateDataImage(args[1])
	case "--show-patcher":
		return true, launchPatchWindow()
	default:
		return false, nil
	}
}

func cliProgress(percent int, status string) {
	fmt.Fprintf(os.Stdout, "KK_PROGRESS|%d|%s\n", percent, status)
}
