# KarmaKontroller

KarmaKontroller is an experimental Windows utility for GoPro Karma Controller owners who need to restore offline map downloads after the original map endpoints stopped working reliably on the controller.

It provides:

- A Windows tray agent named `KarmaKontroller` that runs a local HTTPS Mapbox compatibility proxy.
- A patching GUI for user-supplied `system.img` files.
- Controller backup and flash helpers that call the Amlogic `update.exe` tool.
- A controller-side data-store path for proxy configuration, so a group release does not need anyone's personal IP address baked into `system.img`.
- A button-gated controller-side file browser and proxy helper so `/data/karma-mapbox-proxy/upstream.txt` can be updated without rebuilding a full system image.

This project is not affiliated with, endorsed by, or supported by GoPro, Mapbox, Amlogic, or any drone manufacturer.

## Safety

This tool patches and flashes controller firmware partitions. A bad image, interrupted flash, dead battery, driver problem, or unplugged USB cable can leave a controller unrecoverable.

Use this only on hardware you own or are authorized to service. Make complete backups before flashing, including `dataBU.img`. Keep the controller powered and connected until operations finish.

The controller OS depends on a valid `/data` partition. If `/data` is wiped or zeroed, reflashing only `system.img` may still leave the controller stuck at a boot logo or black screen. Current builds require a valid raw ext4 `dataBU.img` backup before system flashing and include a separate Data restore flow for recovery.

KarmaKontroller does not include firmware images. End users must provide their own controller images.

## How It Works

The original offline map flow fails because the controller's old Android/Mapbox stack can no longer complete the modern HTTPS/API path cleanly. The patch adds a compatibility layer:

1. `system.img` is patched to install a controller-side Mapbox proxy launcher, button-gated file browser, trusted proxy certificate, and startup hook.
2. `data.img` is used for mutable configuration at `/data/karma-mapbox-proxy/`.
3. The PC tray agent listens on the local network, usually on port `443`.
4. The controller reads `/data/karma-mapbox-proxy/upstream.txt` to learn which PC IP/port to use.
5. Offline map downloads can then flow through the PC proxy without rebuilding `system.img` for each user's IP address.

## Repository Layout

- `karma_mapbox_proxy/` - Go source for the Windows tray agent, image patcher, proxy, backup/flash wrapper, and GUI launcher.
- `karma_mapbox_proxy/assets/` - Runtime files copied into patched images or used by the GUI.
- `karma_mapbox_proxy/certs/` - Proxy certificates and upstream root certificates used by the compatibility proxy.
- `installer/` - Windows installer bootstrap and install script.
- `installer/Driver-Unsigned-Test-Mode-README.txt` - Driver test-signing instructions copied into packaged releases.

Generated release folders, firmware images, extracted filesystems, logs, and third-party binary tool bundles are intentionally ignored by Git.

The source repository also omits generated controller-side binaries such as `karma_mapbox_proxy/assets/karma-mapbox-proxy`, `karma_mapbox_proxy/assets/karma-file-browser`, and `karma_mapbox_proxy/assets/karma-button-gate`. Build or add those only when preparing a release package.

## Requirements

- Windows 10 or Windows 11.
- Go toolchain for building from source.
- PowerShell 5+.
- .NET Framework C# compiler for the current installer bootstrap (`csc.exe`).
- A user-supplied GoPro Karma Controller `system.img`.
- The Amlogic update tool bundle for backup/flash operations. Do not redistribute this bundle unless you have verified that you have the right to do so.
- The WorldCup/libusb driver installed when using USB backup/flash mode.

## Building

Build the Windows tray app:

```powershell
cd karma_mapbox_proxy
go build -ldflags="-H windowsgui" -o ..\KarmaKontroller.exe .
```

For local release packaging, create a `KarmaKontroller-release` folder containing:

- `KarmaKontroller.exe`
- `assets\`, including freshly built controller-side `karma-mapbox-proxy`, `karma-file-browser`, and `karma-button-gate` binaries
- optional `cmdUpdTool2\` if you are allowed to distribute the update tool bundle
- `Backup\` and `Patch\` folders

Then rebuild the installer from the files in `installer/`. The current bootstrap embeds `payload.zip` and `KarmaKontroller-Install.ps1` into `KarmaKontroller-Setup.exe`.

## Using The App

1. Start `KarmaKontroller.exe`.
2. Use the tray menu to start or stop the proxy.
3. Use `Patch / Flash / Backup...` to open the image tools window.
4. Patch a user-provided `system.img`.
5. Back up controller partitions before flashing. Keep `Data` selected.
6. Flash only after confirming the backup and patched image are correct.
7. Use `Restore Data` only with a valid `dataBU.img` from the same controller.

The tray tooltip/menu reports whether the app can see the controller on the local Wi-Fi network. USB backup and flash are separate: the controller must also be visible to `update.exe` in update mode.

## Data Configuration

The patched controller expects mutable proxy settings under:

```text
/data/karma-mapbox-proxy/
```

Important files:

- `upstream.txt` - PC proxy address, for example `192.168.1.50:443`.
- `hosts.txt` - allowed/rewritten Mapbox host configuration.
- `dns.txt` - optional DNS override list.

This is what keeps the group patch flexible: releases should not hardcode one person's local IP address into `system.img`.

The controller file browser is off by default. Hold the controller's Shutter and Mode buttons during boot (After the GOPRO Splash screen and the system loading bar appears) to open it for a short maintenance window on port `8080`.

The controller file browser only allows write, edit, upload, create, and delete actions inside `/data/karma-mapbox-proxy/`. Other `/data` paths can be browsed for diagnostics but are not exposed as writable through the browser.

This button-gated behavior replaced an earlier always-on test build because automatic exposure of the file browser could trigger a controller error during normal boot or drone pairing. (Always reboot controller and allow it boot normally after the host edit, Never attempt to pair or fly the drone if the controller is still using port '8080')

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
