# KarmaKontroller

KarmaKontroller is an experimental Windows utility for GoPro Karma Controller owners who need to restore offline map downloads after the original map endpoints stopped working reliably on the controller.

It provides:

- A public-proxy friendly controller patch for restoring Mapbox downloads without requiring a same-network PC agent.
- A native Windows application for controller backup, image patching, system flashing, and data restore.
- A WinUSB backend for controller backup and flash operations, avoiding the old unsigned WorldCup driver workflow.
- Local network detection for the temporary controller file browser and a built-in Windows Explorer bridge.
- A controller-side data-store path for proxy configuration, so a group release does not need anyone's personal IP address baked into `system.img`.
- A small controller-side file browser and proxy helper so `/data/karma-mapbox-proxy/upstream.txt` can be updated without rebuilding a full system image.

This project is not affiliated with, endorsed by, or supported by GoPro, Mapbox, Amlogic, or any drone manufacturer.

## Safety

This tool patches and flashes controller firmware partitions. A bad image, interrupted flash, dead battery, driver problem, or unplugged USB cable can leave a controller unrecoverable.

Use this only on hardware you own or are authorized to service. Make complete backups before flashing, including `dataBU.img` when practical. Keep the controller powered and connected until operations finish.

The controller OS depends on a valid `/data` partition. If `/data` is wiped or zeroed, reflashing only `system.img` may still leave the controller stuck at a boot logo or black screen. Current WinUSB builds can flash `system.img` without also flashing Data, but the app still recommends a raw ext4 `dataBU.img` recovery backup before changing System and includes a separate Data restore flow for recovery.

KarmaKontroller does not include firmware images. End users must provide their own controller images.

## How It Works

The original offline map flow fails because the controller's old Android/Mapbox stack can no longer complete the modern HTTPS/API path cleanly. The patch adds a compatibility layer:

1. `system.img` is patched to install a controller-side Mapbox proxy launcher, file browser, trusted proxy certificate, and startup hook.
2. `data.img` is used for mutable configuration at `/data/karma-mapbox-proxy/`.
3. The controller reads `/data/karma-mapbox-proxy/upstream.txt` to learn which proxy host/port to use.
4. For the group release, that upstream is `karmakontroller.duckdns.org:443`.
5. Offline map downloads then flow through the public proxy; the Windows application does not run a local proxy or listen on port `443`.

## Online Config

The GitHub Pages config file lives at:

```text
docs/karma-mapbox-proxy.txt
```

Current contents:

```text
upstream=karmakontroller.duckdns.org:443
```

The published configuration URL is:

```text
https://larryboyg.github.io/KarmaKontroller/karma-mapbox-proxy.txt
```

Patched controllers store that URL in:

```text
/data/karma-mapbox-proxy/online-hosts-url.txt
```

When present, the controller-side proxy periodically refreshes the online config and updates its local `upstream.txt`, `hosts.txt`, or `dns.txt` values. If the online config is missing or unavailable, the default `upstream.txt` still points at `karmakontroller.duckdns.org:443`.

## Repository Layout

- `KarmaWinUSB/` - WinUSB backend CLI for controller identify, backup, and flash operations.
- `KarmaWinUSBApp/` - Native Windows Karma Kontroller 2.1 application.
- `karma_mapbox_proxy/` - Go source for the patch helper, controller/Ubuntu proxy, and legacy image tools.
- `karma_mapbox_proxy/assets/` - Runtime files copied into patched images or used by the GUI.
- `karma_mapbox_proxy/certs/` - Proxy certificates and upstream root certificates used by the compatibility proxy.
- `installer/` - Windows installer bootstrap and install script.

Generated release folders, firmware images, extracted filesystems, logs, and third-party binary tool bundles are intentionally ignored by Git.

## Requirements

- Windows 10 or Windows 11.
- PowerShell 5+.
- .NET Framework C# compiler for the current installer bootstrap (`csc.exe`).
- Go toolchain for building from source.
- A user-supplied GoPro Karma Controller `system.img`.
- WinUSB driver binding for `USB\VID_1B8E&PID_C003`. The app can reuse an installed libwdi/Zadig WinUSB package or generate one with its bundled libwdi helper when switching drivers.

## Building

Build the WinUSB backend and native Windows app:

```powershell
.\KarmaWinUSBApp\build.ps1
```

Build the installer and release folder:

```powershell
.\installer\Build-KarmaKontrollerInstaller.ps1
```

The installer embeds the app payload and creates `Documents\KarmaKontroller Backups`, `Documents\KarmaKontroller Patch`, and `Documents\KarmaKontroller Logs` during installation.

## Using The App

1. Start `KarmaKontroller.exe`.
2. The image tools window opens directly. Closing it exits KarmaKontroller.
3. Patch a user-provided stock `system.img`.
4. Back up controller partitions before flashing. A Data backup is strongly recommended.
5. Flash only after confirming the backup and patched image are correct.
6. Use `Restore Data` only with a valid `dataBU.img` from the same controller.
7. Use `Browse Partitions` to detect the gated controller file browser on port `8080`, open it in a web browser, or start the temporary Windows Explorer bridge.

KarmaKontroller does not run in the notification tray or start a PC-side Mapbox proxy. USB backup and flash require the controller to be in update mode and visible through WinUSB.

The Browse Partitions network scan is user-initiated and only looks for the gated controller file browser on the local subnet. The Explorer bridge listens on localhost and runs only while Karma Kontroller is open.

## Data Configuration

The patched controller expects mutable proxy settings under:

```text
/data/karma-mapbox-proxy/
```

Important files:

- `upstream.txt` - public proxy address; defaults to `karmakontroller.duckdns.org:443`.
- `hosts.txt` - allowed/rewritten Mapbox host configuration.
- `dns.txt` - optional DNS override list.
- `online-hosts-url.txt` - GitHub Pages configuration URL.

This is what keeps the group patch flexible: releases should not hardcode one person's local IP address into `system.img`.

The controller file browser only allows write, edit, upload, create, and delete actions inside `/data/karma-mapbox-proxy/`. Other `/data` paths can be browsed for diagnostics but are not exposed as writable through the browser.

## Driver Notes

Karma Kontroller 2.1 uses WinUSB for `USB\VID_1B8E&PID_C003`. If the controller appears with another driver, the app prompts before attempting a driver switch. On a fresh Windows install, Karma Kontroller uses its bundled libwdi helper to generate and install a WinUSB driver package after administrator approval. The old WorldCup/libusb path is no longer required for the WinUSB workflow.

## Licensing

Project source code is licensed under the Apache License 2.0. See `LICENSE`.

That license applies to the KarmaKontroller source created for this project. It does not grant rights to third-party firmware images, vendor tools, drivers, Mapbox services, Microsoft runtime DLLs, Amlogic utilities, GoPro software, or any other separately licensed components.

See `THIRD_PARTY_NOTICES.md` before publishing binaries or installer packages.
