# Security Policy

KarmaKontroller is a firmware patching and local proxy tool. Treat it as a high-trust utility.

## Supported Use

- Use only on controllers you own or are authorized to repair.
- Back up all controller partitions before flashing.
- Keep firmware images, backups, and device-specific files private.
- Do not publish images that contain personal data, credentials, Wi-Fi details, or vendor-owned firmware.

## Sensitive Files

Do not commit:

- `system.img`, `data.img`, partition backups, or extracted firmware trees.
- Personal `upstream.txt` values containing your local IP address if they identify your setup.
- Private certificate material generated for your own release.
- Amlogic update tools, vendor DLLs, Microsoft runtime DLLs, or drivers unless redistribution rights are verified.

## Driver And Flashing Risk

Unsigned USB drivers and flashing tools require elevated trust. Install drivers only from sources you trust, and return Windows to normal driver-signing mode after installation if test-signing was enabled.

Do not unplug or power off the controller during backup or flash operations.

## Reporting Issues

For a public GitHub repository, use private security advisories for issues that could expose users to unwanted firmware modification, unsafe flashing behavior, credential leakage, or proxy trust problems.
