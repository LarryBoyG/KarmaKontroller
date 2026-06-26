# Karma Kontroller 2.1

This release moves Karma Kontroller to a native WinUSB workflow for backup, patching, flashing, and recovery. The public Mapbox compatibility proxy remains hosted online, so end users do not need to run a local PC proxy or stay on the same network as a helper application.

## Highlights

- Added the native Windows `Karma Kontroller 2.1` application.
- Added WinUSB controller detection, identify, partition list, backup, system flash, and data restore workflows.
- Added an in-app prompt to switch the connected controller to WinUSB when Windows is using another driver.
- Added cleaner backup and flash progress with current partition/status, percent complete, and detailed logs written separately.
- Added automatic padding for sparse `system.img` files before raw WinUSB flashing.
- Changed `dataBU.img` from a hard requirement to a recommended safety backup for system-only flashing.
- Added About, Language, Logs, license, and GitHub issue links to the app.
- Added a setup executable that installs the app payload and creates the backup, patch, and log folders under Documents.

## Restored Patch Features

- Offline Mapbox map downloads through the public Karma Kontroller proxy.
- GitHub Pages online proxy configuration at `https://larryboyg.github.io/KarmaKontroller/karma-mapbox-proxy.txt`.
- Controller-side proxy startup, DNS/upstream refresh behavior, trusted certificate installation, and hosts configuration.
- WMM2025 coefficient update.
- Shutter + Mode button gate for the temporary controller file browser on port `8080`.

## Safety

Back up the controller before flashing. A system-only flash does not require restoring Data, but a valid `dataBU.img` from the same controller is still the recovery safety net if `/data` is damaged or needs to be restored later.

Firmware images are not included. Users must provide their own clean stock `system.img`.

Karma Kontroller is an unofficial community utility and is not affiliated with or endorsed by GoPro, Mapbox, Amlogic, or Microsoft.
