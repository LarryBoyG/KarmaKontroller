# WinUSB Driver Note

Karma Kontroller uses WinUSB for the Karma Controller update-mode USB device:

```text
USB\VID_1B8E&PID_C003
```

Karma Kontroller bundles a small libwdi-based helper that can generate and install the WinUSB driver package automatically. Windows will ask for administrator permission during the switch.

Normal flow:

1. Put the controller in update mode while connected over USB.
2. Open Karma Kontroller.
3. Click `Switch Driver`.
4. Approve the Windows administrator prompt.
5. Click `Refresh` if Windows takes a moment to report the new driver.

The bundled `KarmaWinUSB.inf` remains only as a last-resort fallback binding file. On a fresh Windows install, Windows may reject that plain INF because it is not packaged with a trusted signed catalog.

After WinUSB is installed once, Karma Kontroller can identify, back up, patch, flash, and restore through the WinUSB backend.
