# KarmaKontroller

KarmaKontroller is an experimental Windows utility for GoPro Karma Controller owners who need to restore offline map downloads after the original map endpoints stopped working reliably on the controller.

It provides:

- A public-proxy friendly controller patch for restoring Mapbox downloads without requiring a same-network PC agent.
- A Windows image-tools application for patching user-supplied `system.img` files.
- Controller backup and flash helpers that call the Amlogic `update.exe` tool.
- A controller-side data-store path for proxy configuration, so a group release does not need anyone's personal IP address baked into `system.img`.
- A small controller-side file browser and proxy helper so `/data/karma-mapbox-proxy/upstream.txt` can be updated without rebuilding a full system image.

This project is not affiliated with, endorsed by, or supported by GoPro, Mapbox, Amlogic, or any drone manufacturer.

## Safety

This tool patches and flashes controller firmware partitions. A bad image, interrupted flash, dead battery, driver problem, or unplugged USB cable can leave a controller unrecoverable.

Use this only on hardware you own or are authorized to service. Make complete backups before flashing, including `dataBU.img`. Keep the controller powered and connected until operations finish.

The controller OS depends on a valid `/data` partition. If `/data` is wiped or zeroed, reflashing only `system.img` may still leave the controller stuck at a boot logo or black screen. Current builds require a valid raw ext4 `dataBU.img` backup before system flashing and include a separate Data restore flow for recovery.

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

- `karma_mapbox_proxy/` - Go source for the Windows image tools, controller/Ubuntu proxy, backup/flash wrapper, and GUI launcher.
- `karma_mapbox_proxy/assets/` - Runtime files copied into patched images or used by the GUI.
- `karma_mapbox_proxy/certs/` - Proxy certificates and upstream root certificates used by the compatibility proxy.
- `installer/` - Windows installer bootstrap and install script.
- `installer/Driver-Unsigned-Test-Mode-README.txt` - Driver test-signing instructions copied into packaged releases.

Generated release folders, firmware images, extracted filesystems, logs, and third-party binary tool bundles are intentionally ignored by Git.

## Requirements

- Windows 10 or Windows 11.
- Go toolchain for building from source.
- PowerShell 5+.
- .NET Framework C# compiler for the current installer bootstrap (`csc.exe`).
- A user-supplied GoPro Karma Controller `system.img`.
- The Amlogic update tool bundle for backup/flash operations. Do not redistribute this bundle unless you have verified that you have the right to do so.
- The WorldCup/libusb driver installed when using USB backup/flash mode.

## Building

Build the Windows image-tools app:

```powershell
cd karma_mapbox_proxy
go build -ldflags="-H windowsgui" -o ..\KarmaKontroller.exe .
```

For local release packaging, create a `KarmaKontroller-release` folder containing:

- `KarmaKontroller.exe`
- `assets\`
- optional `cmdUpdTool2\` if you are allowed to distribute the update tool bundle
- `Backup\` and `Patch\` folders

Then rebuild the installer from the files in `installer/`. The current bootstrap embeds `payload.zip` and `KarmaKontroller-Install.ps1` into `KarmaKontroller-Setup.exe`.

## Using The App

1. Start `KarmaKontroller.exe`.
2. The image tools window opens directly. Closing it exits KarmaKontroller.
3. Patch a user-provided stock `system.img`.
4. Back up controller partitions before flashing. Keep `Data` selected.
5. Flash only after confirming the backup and patched image are correct.
6. Use `Restore Data` only with a valid `dataBU.img` from the same controller.

KarmaKontroller does not run in the notification tray, start a PC-side Mapbox proxy, or scan the local Wi-Fi network. USB backup and flash require the controller to be visible to `update.exe` in update mode.

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

Some Windows systems reject the WorldCup/libusb driver unless test-signing mode or one-boot driver signature enforcement bypass is used. See:

```text
installer/Driver-Unsigned-Test-Mode-README.txt
```

Re-enable normal Windows driver signature enforcement after driver installation if you do not need test-signing mode.

## Licensing

Project source code is licensed under the Apache License 2.0. See `LICENSE`.

That license applies to the KarmaKontroller source created for this project. It does not grant rights to third-party firmware images, vendor tools, drivers, Mapbox services, Microsoft runtime DLLs, Amlogic utilities, GoPro software, or any other separately licensed components.

See `THIRD_PARTY_NOTICES.md` before publishing binaries or installer packages.
