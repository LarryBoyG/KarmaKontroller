# Contributing

Thanks for helping improve KarmaKontroller.

## Do Not Submit

Do not open issues, pull requests, or release assets that include:

- GoPro Karma firmware images.
- `system.img`, `data.img`, partition backups, or extracted firmware trees.
- Personal controller data, Wi-Fi details, local IP configuration, or logs with private network information.
- Amlogic update tools, vendor DLLs, Microsoft runtime DLLs, or USB driver binaries unless redistribution rights have been verified.

## Good Contributions

Useful contributions include:

- Bug fixes in the Go tray agent, patcher, or GUI wrapper.
- Safer backup/flash validation.
- Clearer recovery and driver-install documentation.
- Build scripts that reproduce release packages from source.
- Compatibility fixes for Windows versions or controller software versions.

## Testing

When testing backup or flash behavior, describe:

- Windows version.
- Controller software version if known.
- Whether the controller was detected over Wi-Fi, USB update mode, or both.
- Which partition operation was attempted.
- Relevant KarmaKontroller log lines, with private IPs or personal paths redacted if needed.
