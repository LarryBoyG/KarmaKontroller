# Publishing Checklist

Before publishing this repository or creating a GitHub Release, verify:

- No `system.img`, `data.img`, partition backup, extracted firmware tree, or user-specific controller file is included.
- No `cmdUpdTool2` bundle, Amlogic DLL, Microsoft runtime DLL, or driver binary is included unless redistribution rights are verified.
- Release packages do not hardcode a maintainer's local IP address.
- `upstream.txt` examples use documentation-only private IPs or placeholders.
- The release notes clearly warn users to back up partitions before flashing.
- The release notes clearly state that USB backup/flash requires update mode and a working WorldCup/libusb driver.
- The controller-side binaries in `karma_mapbox_proxy/assets/` were freshly built from source for the release.
- The proxy certificate/key approach is intentional for the release. The current source layout uses a static proxy certificate and key; a public release may instead want per-user or per-release certificate generation.

Recommended first GitHub commit:

```powershell
git add .gitignore README.md LICENSE THIRD_PARTY_NOTICES.md SECURITY.md CONTRIBUTING.md PUBLISHING_CHECKLIST.md installer karma_mapbox_proxy karma-file-browser.c upstream.txt.example
git commit -m "Initial KarmaKontroller source release"
```
