# Publishing Checklist

- Build `KarmaKontroller-Setup-2.1.exe` with `.\installer\Build-KarmaKontrollerInstaller.ps1`.
- Confirm the installer payload contains `KarmaKontroller.exe`, `KarmaWinUSB.exe`, `KarmaKontrollerPatchTool.exe`, `assets\`, `drivers\`, `LICENSE`, and `THIRD_PARTY_NOTICES.md`.
- Confirm the installer creates `Documents\KarmaKontroller Backups`, `Documents\KarmaKontroller Patch`, and `Documents\KarmaKontroller Logs`.
- No firmware images, controller backups, extracted filesystems, private keys, or personal controller data are included.
- No Amlogic update-tool bundle, Microsoft runtime DLL bundle, WorldCup/libusb driver bundle, or other redistribution-sensitive vendor package is included unless redistribution rights are verified.
- The release notes clearly warn users to back up partitions before flashing.
- The release notes clearly state that USB backup/flash requires update mode and WinUSB for `USB\VID_1B8E&PID_C003`.
- The controller-side binaries in `karma_mapbox_proxy/assets/` were freshly built or intentionally carried forward for the release.
- The proxy certificate/key approach is intentional for the release. The current source layout uses a static proxy certificate and key; a public release may instead want per-user or per-release certificate generation.
- The public config at `docs/karma-mapbox-proxy.txt` points to the intended upstream proxy host.
- The setup executable and release notes were tested on a clean Windows machine or VM when possible.

Suggested commit:

```text
Package Karma Kontroller 2.1 WinUSB installer
```
