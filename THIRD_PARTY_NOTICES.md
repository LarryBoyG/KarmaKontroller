# Third-Party Notices

This file summarizes third-party components known to be used by KarmaKontroller. It is not legal advice. Verify redistribution rights before publishing release binaries or installers.

## Project License Scope

`LICENSE` covers the KarmaKontroller source code created for this project.

It does not license:

- GoPro Karma Controller firmware images or extracted firmware contents.
- GoPro, Mapbox, Amlogic, Microsoft, or other vendor software.
- User-provided `system.img`, `data.img`, or partition backup images.
- Separately licensed driver packages, DLLs, runtime libraries, or command-line flashing tools.

## Go Dependencies

The Go module imports these third-party packages through `go.mod`:

| Component | Version | License found locally |
| --- | --- | --- |
| `github.com/getlantern/context` | `v0.0.0-20190109183933-c447772a6520` | Apache-2.0 |
| `github.com/getlantern/errors` | `v0.0.0-20190325191628-abdb3e3e36f7` | Apache-2.0 |
| `github.com/getlantern/golog` | `v0.0.0-20190830074920-4ef2e798c2d7` | Apache-2.0 |
| `github.com/getlantern/hex` | `v0.0.0-20190417191902-c6586a6fe0b7` | BSD-style license; copyright notices include The Go Authors and Brave New Software Project, Inc. |
| `github.com/getlantern/hidden` | `v0.0.0-20190325191715-f02dbb02be55` | Apache-2.0 |
| `github.com/getlantern/ops` | `v0.0.0-20190325191751-d70cb0d6f85f` | Apache-2.0 |
| `github.com/getlantern/systray` | `v1.2.2` | Apache-2.0 |
| `github.com/go-stack/stack` | `v1.8.0` | MIT |
| `github.com/oxtoacart/bpool` | `v0.0.0-20190530202638-03653db5a59c` | Apache-2.0 |
| `golang.org/x/sys` | `v0.1.0` | BSD-3-Clause-style Go license |

When distributing binaries, include the applicable dependency license texts or a generated third-party license bundle.

## Driver Files

The WorldCup driver INF used during local testing states:

```text
Copyright (c) 2010 libusb-win32 (GNU LGPL)
```

If you distribute the driver package, include the libusb-win32 LGPL license text and comply with the LGPL requirements for the driver binaries you distribute. If you are unsure, do not publish the driver package in the GitHub repository; instead, document where users can obtain or supply it themselves.

## Amlogic Update Tool Bundle

Backup and flash operations call an external Amlogic update tool bundle, commonly containing files such as:

- `update.exe`
- `AmlLibusb.dll`
- `AmlUsbScanX3.dll`
- `UsbRomDrv.dll`
- `Amldbglog.dll`

No open-source license for those files was found in this workspace. Treat them as proprietary or redistribution-restricted unless you have separate permission to distribute them.

## Microsoft Runtime DLLs

Some local update-tool bundles include Microsoft Visual C++ runtime files such as:

- `msvcr100.dll`
- `msvcp100.dll`
- `mfc100u.dll`

These are governed by Microsoft redistributable terms, not the KarmaKontroller license. Prefer instructing users to install the official Microsoft Visual C++ Redistributable unless you have confirmed redistribution rights for the specific files in your installer.

## Certificates

The repository contains proxy certificate material used by the local compatibility proxy and public upstream root certificates used for TLS validation.

Before publishing a public release, decide whether you want a shared trust anchor or a per-build/per-user generated certificate. A shared private key is convenient for testing, but it is not ideal for a security-conscious public release.

## Trademarks And Services

GoPro, Karma, Mapbox, Amlogic, Microsoft, Windows, and other names are trademarks of their respective owners. This project is independent and unofficial.

Users are responsible for complying with the terms of any map, firmware, driver, or vendor service they use.
