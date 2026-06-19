# KarmaKontroller Public Proxy Update

This release changes KarmaKontroller into a focused Windows patch, flash, backup, and restore utility. Map downloads are handled by the controller-side compatibility proxy and the public KarmaKontroller server.

## Highlights

- Removed the Windows notification-tray agent, PC-side HTTPS proxy, port `443` listener, and local Wi-Fi controller scan.
- Changed `KarmaKontroller.exe` to open Image Tools directly and exit when the tools window closes.
- Added the validated `v8-upstream-dns` controller proxy, which resolves the DuckDNS upstream through the controller proxy's configured DNS servers.
- Added 30-second online-config retry behavior while the controller is waiting for network connectivity.
- Set the default upstream to `karmakontroller.duckdns.org:443`.
- Set the online configuration URL to `https://larryboyg.github.io/KarmaKontroller/karma-mapbox-proxy.txt`.
- Kept the Shutter + Mode button gate for the temporary controller file-browser maintenance window on port `8080`.
- Kept WMM2025 coefficients, proxy certificate installation, backup/flash validation, and Data restore support.

## Patching

Users provide their own clean stock `system.img`. The patcher injects the controller proxy, startup scripts, trusted certificate, hosts configuration, WMM2025 file, button gate, and maintenance file browser.

Firmware images are not included in the repository or release package.

## Safety

Back up all controller partitions, including a valid `dataBU.img`, before flashing. Keep the controller powered and connected throughout backup and flash operations.

KarmaKontroller is an unofficial community utility and is not affiliated with or endorsed by GoPro, Mapbox, Amlogic, or Microsoft.
