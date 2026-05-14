# KarmaKontroller Initial Working Release

This is the first working preview release of KarmaKontroller, a Windows utility for restoring GoPro Karma Controller offline map downloads using a local Mapbox compatibility proxy and a patched user-supplied `system.img`.

## Highlights

- Added Windows tray agent named `KarmaKontroller`.
- Added green/red `K` tray icon state for listening/stopped.
- Added tray menu actions for Start, Stop, Patch / Flash / Backup, Open Folder, and Quit.
- Added local HTTPS Mapbox compatibility proxy for controller map downloads.
- Changed the controller file browser to stay off by default and open only when Shutter + Mode are held during boot.
- Verified the button-gated file browser avoids the controller error triggered by the earlier always-on port `8080` test build.
- Added controller discovery on the local Wi-Fi network.
- Added patching GUI for user-provided `system.img` files.
- Added backup and flash GUI using the Amlogic update tool.
- Added backup partition checkboxes for bootloader, boot, recovery, system, data, and GoPro.
- Added progress reporting for patching, backup, and flashing.
- Added flash confirmation warning before writing to the controller.
- Added a required `dataBU.img` preflight check before system flashing.
- Added a separate `Restore Data` action for restoring a valid controller data partition backup.
- Added installer support for Program Files / Program Files (x86), desktop shortcut, app icon, and default Backup/Patch folders.

## Map Download Fix

Offline maps were broken because the controller's older Android/Mapbox stack could no longer reliably talk to the modern online map endpoints. This release patches the controller image so map requests can be routed through the PC-side KarmaKontroller proxy.

The proxy address is stored on the controller data partition at:

```text
/data/karma-mapbox-proxy/upstream.txt
```

That keeps releases flexible and avoids hardcoding one maintainer's local IP address into `system.img`.

## Data Store Access

The patched image includes a small browser-accessible file service for the controller data store. This makes it easier to inspect and update proxy configuration without rebuilding and reflashing a full system image for every change.

For safety, the file browser is not started automatically. Hold Shutter + Mode while the controller boots to open the file browser for a short maintenance window on port `8080`.

Write actions in the browser are intentionally limited to `/data/karma-mapbox-proxy/` so proxy configuration remains flexible without exposing the rest of `/data` for edits.

## Pairing Safety

Earlier experimental images started the controller file browser automatically on Wi-Fi. Testing showed that this could trigger a controller error during normal boot or drone pairing. The current patch leaves the file browser dormant unless the boot-time button combo is held, while the Mapbox proxy continues to start normally for offline map search and downloads.

## Safety Notes

Backup and flash operations require the controller to be connected over USB in update mode with the WorldCup/libusb driver installed. Wi-Fi detection alone is not enough for backup or flashing.

Always back up controller partitions before flashing. Do not unplug the controller, close the app, or power off the PC/controller during a flash operation.

Keep a valid `dataBU.img` from the same controller. The controller OS may not boot if the data partition is wiped or invalid, even after flashing a good `system.img`.

## Fixed In This Build

- Fixed desktop shortcut launching a black console window.
- Fixed Patch / Flash / Backup window opening with an extra visible PowerShell window.
- Improved backup failure messages when `update.exe` cannot find the controller USB device.
- Added validation so missing or incomplete backup files are reported clearly.
- Updated installer behavior so upgrading stops the running tray app before replacing files.
- Added a patcher safety check that rejects older experimental images containing stale DHCP/startup hook modifications.
- Added system flash preflight validation so stale experimental images are rejected before `update.exe` is called.
- Added data image validation so zero-filled or non-ext4 `dataBU.img` files are rejected.
- Changed patched-system startup so the Mapbox proxy starts automatically but the file browser does not expose port `8080` unless the boot button combo is detected.

## Known Limitations

- Firmware images are not included. Users must provide their own `system.img`.
- Amlogic update tools, vendor DLLs, Microsoft runtime DLLs, and driver binaries are not included in the source repository unless redistribution rights are verified.
- The source repo omits generated controller-side binaries; release packages need freshly built copies of those assets.
- Current builds use a static proxy certificate/key layout. A future public release should consider generated per-user or per-release certificates.

## Not Official

KarmaKontroller is an unofficial community tool. It is not affiliated with, endorsed by, or supported by GoPro, Mapbox, Amlogic, or Microsoft.
