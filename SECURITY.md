# Security Policy

KarmaKontroller is a firmware patching tool with controller-side and public-server proxy components. Treat it as a high-trust utility.

## Supported Use

- Use only on controllers you own or are authorized to repair.
- Back up all controller partitions before flashing, including a valid `dataBU.img`.
- Keep firmware images, backups, and device-specific files private.
- Do not publish images that contain personal data, credentials, Wi-Fi details, or vendor-owned firmware.

## Sensitive Files

Do not commit:

- `system.img`, `data.img`, partition backups, or extracted firmware trees.
- Private server addresses, credentials, API tokens, or configuration values that identify your setup.
- Private certificate material generated for your own release.
- Amlogic update tools, vendor DLLs, Microsoft runtime DLLs, or drivers unless redistribution rights are verified.

## Driver And Flashing Risk

Unsigned USB drivers and flashing tools require elevated trust. Install drivers only from sources you trust, and return Windows to normal driver-signing mode after installation if test-signing was enabled.

Do not unplug or power off the controller during backup or flash operations.

Do not flash `system.img` unless you also have a valid `dataBU.img` from the same controller. A wiped or invalid data partition can prevent the controller OS from booting.

The controller-side browser should keep write actions scoped to `/data/karma-mapbox-proxy/`. Treat any change that broadens writable paths as security-sensitive.

## Reporting Issues

For a public GitHub repository, use private security advisories for issues that could expose users to unwanted firmware modification, unsafe flashing behavior, credential leakage, or proxy trust problems.
