# KarmaWinUSB

Standalone WinUSB-compatible CLI for GoPro Karma Controller update-mode access.

This is the WinUSB-compatible backend used by the native Karma Kontroller app. The stable path is still backup-first, and flash operations remain protected by explicit safety flags and GUI confirmations.

## Requirements

- Windows
- GoPro Karma Controller in update mode
- USB connected before invoking controller update mode
- WinUSB driver installed for `VID_1B8E&PID_C003` with Zadig

## Build

Run from this folder:

```powershell
.\build.ps1
```

The executable is written to:

```text
.\bin\KarmaWinUSB.exe
```

## CLI

```powershell
.\bin\KarmaWinUSB.exe help
.\bin\KarmaWinUSB.exe version
.\bin\KarmaWinUSB.exe partitions
.\bin\KarmaWinUSB.exe identify
```

Back up every known partition:

```powershell
.\bin\KarmaWinUSB.exe backup .\backups\controller-full
```

Back up selected partitions:

```powershell
.\bin\KarmaWinUSB.exe backup .\backups\system-data --part system --part data
```

Compatibility commands from the probe:

```powershell
.\bin\KarmaWinUSB.exe backup-one system .\backups
.\bin\KarmaWinUSB.exe backup-all .\backups\controller-full
```

Flash a known partition after the GUI/user has confirmed the risk:

```powershell
.\bin\KarmaWinUSB.exe flash-partition system C:\path\to\system.img --i-understand-this-can-brick --verify-after-write
.\bin\KarmaWinUSB.exe flash-partition system C:\path\to\patched-system.img --expect-current C:\path\to\stock-system.img --i-understand-this-can-brick --verify-after-write
.\bin\KarmaWinUSB.exe flash-partition data C:\path\to\dataBU.img --i-understand-this-can-brick --verify-after-write
```

## Experimental Write Path

First prove USB write/download behavior without touching flash storage:

```powershell
.\bin\KarmaWinUSB.exe experimental-ram-write-test 0x10000 --experimental-write
```

That command writes a deterministic test pattern to RAM at `0x01000000`, uploads it back, and compares the bytes.

Check a flash operation without writing anything:

```powershell
.\bin\KarmaWinUSB.exe experimental-flash system C:\path\to\system.img --dry-run
```

Before a full flash, test one canary chunk. This reads the current controller chunk first and refuses to write unless it already matches the image:

```powershell
.\bin\KarmaWinUSB.exe experimental-flash-canary system C:\path\to\system.img --offset 0x0 --dry-run
.\bin\KarmaWinUSB.exe experimental-flash-canary system C:\path\to\system.img --offset 0x0 --experimental-write --i-understand-this-can-brick --verify-before-write --verify-after-write
```

The default canary size is one 2 MiB flash chunk. You can use `--size 0x10000` for a smaller 64 KiB test, but the normal full-flash chunk size is 2 MiB.

Actually flashing a partition requires all safety flags:

```powershell
.\bin\KarmaWinUSB.exe experimental-flash system C:\path\to\system.img --experimental-write --i-understand-this-can-brick --verify-before-write --verify-after-write
```

When intentionally changing a partition, pass the image that should already be on the controller with `--expect-current`. The tool verifies each controller chunk against that expected-current image before writing the new target image:

```powershell
.\bin\KarmaWinUSB.exe experimental-flash system C:\path\to\new-system.img --expect-current C:\path\to\current-system.img --experimental-write --i-understand-this-can-brick --verify-before-write --verify-after-write
```

The flash path:

- Requires the image size to exactly match the known partition size.
- Supports a one-chunk canary write with mandatory pre-write and post-write verification.
- Downloads one 2 MiB chunk to RAM at a time.
- Reads and compares each existing controller chunk before writing it when `--verify-before-write` is supplied. If `--expect-current` is provided, the before-write comparison uses that image; otherwise it uses the target image.
- Runs `store write <partition> <addr> <offset> <size>`.
- Reads the same chunk back with `store read` plus `upload mem`.
- Fails immediately if verification differs.
- Refuses `bootloader` unless `--allow-bootloader` is also supplied.

## Known Partitions

| Partition | Size | Output file |
| --- | ---: | --- |
| `bootloader` | 4,194,304 | `bootloaderBU.img` |
| `boot` | 33,554,432 | `bootBU.img` |
| `recovery` | 33,554,432 | `recoveryBU.img` |
| `system` | 1,073,741,824 | `systemBU.img` |
| `data` | 1,371,471,872 | `dataBU.img` |
| `gopro` | 536,870,912 | `goproBU.img` |

## Status

- Full read-only backup was proven against the controller on 2026-06-25.
- The backup path uses offset-based `store read` chunks plus `upload mem`, avoiding the 2 MiB limit seen with `upload store`.
- RAM write round-trip and partition flashing were promoted into the native GUI behind explicit confirmations after successful controller tests.
