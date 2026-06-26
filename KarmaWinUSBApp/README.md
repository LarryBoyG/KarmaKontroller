# Karma Kontroller

Native Windows GUI for GoPro Karma Controller maintenance over WinUSB.

## First-run Flow

1. Detects the Karma Controller update-mode USB device: `USB\VID_1B8E&PID_C003`.
2. Checks the active Windows service for that device.
3. If the service is not `WinUSB`, asks before attempting a driver switch.
4. Looks for an already-installed signed libwdi/Zadig WinUSB package for the controller and uses that first.
5. Uses the bundled libwdi helper to generate and install a WinUSB package when no existing package is available.
6. Falls back to the bundled `drivers\KarmaWinUSB.inf` only if the helper files are missing.
7. Verifies that Windows is actively reporting the `WinUSB` service before enabling controller commands.
8. Once WinUSB is active, asks whether to make a full controller backup.

## Features

- Selected-partition controller backup through `KarmaWinUSB.exe`.
- Clean progress display for backup and flash operations.
- Detailed command transcripts saved under `Documents\KarmaKontroller Logs`.
- System image patching through the bundled `KarmaKontrollerPatchTool.exe`.
- System flashing and data restore through the WinUSB backend.
- Driver status and device detection without showing backend paths in the main window.
- Versioned application title bar: `Karma Kontroller 2.1`.
- Footer buttons for language selection, About, and opening operation logs.
- About window with restored feature summary, GitHub issue link, and bundled license notices.

## Build

Run from this folder:

```powershell
.\build.ps1
```

Output:

```text
.\bin\KarmaKontroller.exe
.\bin\KarmaWinUSB.exe
.\bin\KarmaKontrollerPatchTool.exe
.\bin\assets\
.\bin\drivers\KarmaWinUSB.inf
.\bin\drivers\karma-winusb-driver.exe
.\bin\drivers\libwdi.dll
.\bin\LICENSE
.\bin\THIRD_PARTY_NOTICES.md
```

## Notes

- Windows requires administrator approval for driver changes.
- Fresh Windows installs should use the bundled libwdi helper through the `Switch Driver` button. The plain INF is only a last-resort fallback.
- Flashing still requires explicit confirmation in the GUI.
- A valid `dataBU.img` is recommended before system flashing as a recovery backup, but WinUSB system-only flashing can continue without it after confirmation.
