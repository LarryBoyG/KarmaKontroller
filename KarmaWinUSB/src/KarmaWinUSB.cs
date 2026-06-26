using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

internal static class KarmaWinUSB
{
    private const string AppName = "KarmaWinUSB";
    private const string Version = "0.6.0";
    private const string KarmaVidPid = "VID_1B8E&PID_C003";
    private static readonly Guid ZadigInterfaceGuid = new Guid("37CB674F-B014-448B-930B-F86191127103");

    private const byte PipeIn = 0x81;
    private const byte PipeOut = 0x02;
    private const byte RequestWriteMedia = 0x32;
    private const int BulkReplyLen = 512;
    private const int MediaBlockLen = 0x1000;
    private const int PipeChunk = 0x10000;
    private const int StoreChunk = 0x200000;
    private const int WriteMediaAckLen = 0x200;
    private const ushort WriteMediaChecksumAlgAddSum = 0x00ef;
    private const uint StoreReadAddress = 0x01000000;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TRANSFER_TIMEOUT = 0x03;

    private static readonly Dictionary<string, long> KnownPartitions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        { "bootloader", 0x400000L },
        { "boot", 0x2000000L },
        { "recovery", 0x2000000L },
        { "system", 0x40000000L },
        { "data", 0x51bf0000L },
        { "gopro", 0x20000000L },
    };

    private static readonly string[] KnownPartitionOrder =
    {
        "bootloader",
        "boot",
        "recovery",
        "system",
        "data",
        "gopro",
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(IntPtr deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ControlTransfer(
        IntPtr interfaceHandle,
        WINUSB_SETUP_PACKET setupPacket,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(
        IntPtr interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);

    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "version":
                    Console.WriteLine("{0} {1}", AppName, Version);
                    return 0;
                case "partitions":
                    PrintPartitions();
                    return 0;
                case "identify":
                    using (var dev = Open())
                    {
                        dev.PrintIdentify();
                    }
                    return 0;
                case "backup":
                    return RunBackupCommand(args);
                case "backup-one":
                    if (args.Length < 3)
                    {
                        throw new ArgumentException("backup-one requires <partition> <folder>");
                    }
                    using (var dev = Open())
                    {
                        dev.BackupKnownPartition(NormalizePartition(args[1]), args[2]);
                    }
                    return 0;
                case "backup-all":
                    if (args.Length < 2)
                    {
                        throw new ArgumentException("backup-all requires <folder>");
                    }
                    using (var dev = Open())
                    {
                        BackupPartitions(dev, KnownPartitionOrder, args[1]);
                    }
                    return 0;
                case "flash-partition":
                    return RunFlashPartitionCommand(args);
                case "reset-pipes":
                    using (var dev = Open())
                    {
                        dev.ResetPipes();
                    }
                    return 0;
                case "experimental-ram-write-test":
                    return RunExperimentalRamWriteTestCommand(args);
                case "experimental-flash-canary":
                    return RunExperimentalFlashCanaryCommand(args);
                case "experimental-flash":
                    return RunExperimentalFlashCommand(args);
                case "diag-bulkcmd":
                    if (args.Length < 2)
                    {
                        throw new ArgumentException("diag-bulkcmd requires a command string");
                    }
                    using (var dev = Open())
                    {
                        Console.WriteLine(dev.BulkCmd(string.Join(" ", Subarray(args, 1))));
                    }
                    return 0;
                case "diag-read-part-store":
                    if (args.Length < 4)
                    {
                        throw new ArgumentException("diag-read-part-store requires <partition> <size> <out>");
                    }
                    using (var dev = Open())
                    {
                        dev.ReadPartitionViaStore(NormalizePartition(args[1]), ParseSize(args[2]), args[3]);
                    }
                    return 0;
                case "diag-read-current":
                    if (args.Length < 3)
                    {
                        throw new ArgumentException("diag-read-current requires <size> <out>");
                    }
                    using (var dev = Open())
                    {
                        dev.ReadCurrentUpload(ParseSize(args[1]), args[2]);
                    }
                    return 0;
                case "diag-drain":
                    if (args.Length < 2)
                    {
                        throw new ArgumentException("diag-drain requires <bytes>");
                    }
                    using (var dev = Open())
                    {
                        dev.Drain(ParseSize(args[1]));
                    }
                    return 0;
                case "diag-bulkstat":
                    using (var dev = Open())
                    {
                        dev.ReadBulkStatus();
                    }
                    return 0;
                default:
                    throw new ArgumentException("unknown command: " + args[0]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: {0}", ex.Message);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("{0} {1}", AppName, Version);
        Console.WriteLine("WinUSB CLI for the GoPro Karma Controller update-mode device.");
        Console.WriteLine("Usage:");
        Console.WriteLine("  KarmaWinUSB.exe identify");
        Console.WriteLine("  KarmaWinUSB.exe partitions");
        Console.WriteLine("  KarmaWinUSB.exe backup <folder>");
        Console.WriteLine("  KarmaWinUSB.exe backup <folder> --part <partition> [--part <partition> ...]");
        Console.WriteLine("  KarmaWinUSB.exe backup-one <partition> <folder>");
        Console.WriteLine("  KarmaWinUSB.exe backup-all <folder>");
        Console.WriteLine("  KarmaWinUSB.exe flash-partition <partition> <image> --i-understand-this-can-brick --verify-after-write [--expect-current <image>]");
        Console.WriteLine("  KarmaWinUSB.exe reset-pipes");
        Console.WriteLine();
        Console.WriteLine("Experimental write path:");
        Console.WriteLine("  KarmaWinUSB.exe experimental-ram-write-test <size> --experimental-write");
        Console.WriteLine("  KarmaWinUSB.exe experimental-flash-canary <partition> <image> --offset <offset> --dry-run");
        Console.WriteLine("  KarmaWinUSB.exe experimental-flash-canary <partition> <image> --offset <offset> --experimental-write --i-understand-this-can-brick --verify-before-write --verify-after-write");
        Console.WriteLine("  KarmaWinUSB.exe experimental-flash <partition> <image> --dry-run [--expect-current <image>]");
        Console.WriteLine("  KarmaWinUSB.exe experimental-flash <partition> <image> --expect-current <image> --experimental-write --i-understand-this-can-brick --verify-before-write --verify-after-write");
        Console.WriteLine("  Experimental commands remain available for diagnostics and staged flash testing.");
        Console.WriteLine();
        Console.WriteLine("Diagnostics:");
        Console.WriteLine("  KarmaWinUSB.exe diag-bulkcmd \"<u-boot command>\"");
        Console.WriteLine("  KarmaWinUSB.exe diag-bulkstat");
        Console.WriteLine("  KarmaWinUSB.exe diag-drain <bytes>");
    }

    private static bool IsHelp(string command)
    {
        return string.Equals(command, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "/?", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintPartitions()
    {
        Console.WriteLine("Known Karma partitions:");
        foreach (string part in KnownPartitionOrder)
        {
            long size = KnownPartitions[part];
            Console.WriteLine("  {0,-10} {1,12} bytes  0x{1:x}", part, size);
        }
    }

    private static int RunBackupCommand(string[] args)
    {
        string folder;
        List<string> partitions;
        ParseBackupArgs(args, out folder, out partitions);

        using (var dev = Open())
        {
            BackupPartitions(dev, partitions, folder);
        }
        return 0;
    }

    private static void ParseBackupArgs(string[] args, out string folder, out List<string> partitions)
    {
        folder = null;
        partitions = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--part", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-p", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--part requires a partition name");
                }
                partitions.Add(NormalizePartition(args[++i]));
                continue;
            }
            if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--out requires a folder");
                }
                if (folder != null)
                {
                    throw new ArgumentException("backup output folder was specified more than once");
                }
                folder = args[++i];
                continue;
            }
            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException("unknown backup option: " + arg);
            }
            if (folder != null)
            {
                throw new ArgumentException("backup accepts one output folder");
            }
            folder = arg;
        }

        if (folder == null)
        {
            throw new ArgumentException("backup requires <folder>");
        }
        if (partitions.Count == 0)
        {
            partitions.AddRange(KnownPartitionOrder);
        }
    }

    private static string NormalizePartition(string partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new ArgumentException("partition name is required");
        }
        partition = partition.Trim().ToLowerInvariant();
        if (!KnownPartitions.ContainsKey(partition))
        {
            throw new ArgumentException("unknown partition: " + partition);
        }
        return partition;
    }

    private static void BackupPartitions(WinUsbDevice dev, IEnumerable<string> partitions, string folder)
    {
        var selected = new List<string>(partitions);
        long totalBytes = 0;
        foreach (string part in selected)
        {
            totalBytes += KnownPartitions[part];
        }

        long completedBytes = 0;
        ReportProgress(0, "Starting backup");
        foreach (string part in selected)
        {
            long partBytes = KnownPartitions[part];
            var progress = new OperationProgress("Backing up " + DisplayPartition(part), completedBytes, totalBytes);
            progress.Report(0);
            dev.BackupKnownPartition(part, folder, progress);
            completedBytes += partBytes;
            ReportProgress(Percent(completedBytes, totalBytes), "Backed up " + DisplayPartition(part));
        }
        ReportProgress(100, "Backup complete");
    }

    private static int RunFlashPartitionCommand(string[] args)
    {
        ExperimentalFlashOptions options = ParseFlashPartitionArgs(args);
        long expectedSize = KnownPartitions[options.Partition];
        var info = new FileInfo(options.ImagePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("image file not found", options.ImagePath);
        }
        if (info.Length != expectedSize)
        {
            throw new InvalidOperationException(string.Format(
                "image size mismatch for {0}: expected {1} bytes, got {2} bytes",
                options.Partition,
                expectedSize,
                info.Length));
        }

        FileInfo expectedCurrentInfo = null;
        if (!string.IsNullOrEmpty(options.ExpectedCurrentImagePath))
        {
            expectedCurrentInfo = new FileInfo(options.ExpectedCurrentImagePath);
            if (!expectedCurrentInfo.Exists)
            {
                throw new FileNotFoundException("expected-current image file not found", options.ExpectedCurrentImagePath);
            }
            if (expectedCurrentInfo.Length != expectedSize)
            {
                throw new InvalidOperationException(string.Format(
                    "expected-current image size mismatch for {0}: expected {1} bytes, got {2} bytes",
                    options.Partition,
                    expectedSize,
                    expectedCurrentInfo.Length));
            }
            options.VerifyBeforeWrite = true;
        }

        if (!options.UnderstandsBrickRisk)
        {
            throw new InvalidOperationException("flash-partition requires --i-understand-this-can-brick");
        }
        if (!options.VerifyAfterWrite)
        {
            throw new InvalidOperationException("flash-partition requires --verify-after-write");
        }
        if (string.Equals(options.Partition, "bootloader", StringComparison.OrdinalIgnoreCase) && !options.AllowBootloader)
        {
            throw new InvalidOperationException("bootloader flashing requires --allow-bootloader");
        }

        using (var dev = Open())
        {
            dev.FlashKnownPartition(
                options.Partition,
                info.FullName,
                expectedCurrentInfo == null ? null : expectedCurrentInfo.FullName,
                options.VerifyBeforeWrite,
                options.VerifyAfterWrite);
        }
        return 0;
    }

    private static int RunExperimentalRamWriteTestCommand(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("experimental-ram-write-test requires <size>");
        }

        RequireFlag(args, "--experimental-write");
        EnsureOnlyFlags(args, 2, "--experimental-write");

        long sizeLong = ParseSize(args[1]);
        if (sizeLong <= 0 || sizeLong > StoreChunk)
        {
            throw new ArgumentOutOfRangeException("size", "RAM write test size must be between 1 byte and 0x" + StoreChunk.ToString("x"));
        }

        using (var dev = Open())
        {
            dev.RunRamWriteRoundTrip((int)sizeLong);
        }
        return 0;
    }

    private static int RunExperimentalFlashCanaryCommand(string[] args)
    {
        ExperimentalCanaryOptions options = ParseExperimentalFlashCanaryArgs(args);
        long expectedSize = KnownPartitions[options.Partition];
        var info = new FileInfo(options.ImagePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("image file not found", options.ImagePath);
        }
        if (info.Length != expectedSize)
        {
            throw new InvalidOperationException(string.Format(
                "image size mismatch for {0}: expected {1} bytes, got {2} bytes",
                options.Partition,
                expectedSize,
                info.Length));
        }
        if (options.Offset < 0)
        {
            throw new InvalidOperationException("experimental-flash-canary requires --offset <offset>");
        }
        if (options.ChunkSize <= 0 || options.ChunkSize > StoreChunk)
        {
            throw new InvalidOperationException("canary size must be between 1 byte and 0x" + StoreChunk.ToString("x"));
        }
        if (options.Offset % MediaBlockLen != 0)
        {
            throw new InvalidOperationException("canary offset must be 4 KiB aligned");
        }
        if (options.ChunkSize % MediaBlockLen != 0)
        {
            throw new InvalidOperationException("canary size must be 4 KiB aligned");
        }
        if (options.Offset + options.ChunkSize > expectedSize)
        {
            throw new InvalidOperationException("canary range extends beyond partition size");
        }

        Console.WriteLine("partition: {0}", options.Partition);
        Console.WriteLine("image:     {0}", info.FullName);
        Console.WriteLine("offset:    0x{0:x}", options.Offset);
        Console.WriteLine("size:      0x{0:x} ({0} bytes)", options.ChunkSize);

        if (options.DryRun)
        {
            Console.WriteLine("dry run only; no USB write was attempted");
            return 0;
        }

        if (!options.ExperimentalWrite)
        {
            throw new InvalidOperationException("experimental-flash-canary requires --experimental-write");
        }
        if (!options.UnderstandsBrickRisk)
        {
            throw new InvalidOperationException("experimental-flash-canary requires --i-understand-this-can-brick");
        }
        if (!options.VerifyBeforeWrite)
        {
            throw new InvalidOperationException("experimental-flash-canary requires --verify-before-write");
        }
        if (!options.VerifyAfterWrite)
        {
            throw new InvalidOperationException("experimental-flash-canary requires --verify-after-write");
        }
        if (string.Equals(options.Partition, "bootloader", StringComparison.OrdinalIgnoreCase) && !options.AllowBootloader)
        {
            throw new InvalidOperationException("bootloader canary flashing requires --allow-bootloader");
        }

        using (var dev = Open())
        {
            dev.CanaryWritePartition(options.Partition, info.FullName, options.Offset, options.ChunkSize);
        }
        return 0;
    }

    private static int RunExperimentalFlashCommand(string[] args)
    {
        ExperimentalFlashOptions options = ParseExperimentalFlashArgs(args);
        long expectedSize = KnownPartitions[options.Partition];
        var info = new FileInfo(options.ImagePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("image file not found", options.ImagePath);
        }
        if (info.Length != expectedSize)
        {
            throw new InvalidOperationException(string.Format(
                "image size mismatch for {0}: expected {1} bytes, got {2} bytes",
                options.Partition,
                expectedSize,
                info.Length));
        }

        Console.WriteLine("partition: {0}", options.Partition);
        Console.WriteLine("image:     {0}", info.FullName);
        Console.WriteLine("size:      {0} bytes", info.Length);
        Console.WriteLine("chunks:    {0}", (info.Length + StoreChunk - 1) / StoreChunk);

        FileInfo expectedCurrentInfo = null;
        if (!string.IsNullOrEmpty(options.ExpectedCurrentImagePath))
        {
            expectedCurrentInfo = new FileInfo(options.ExpectedCurrentImagePath);
            if (!expectedCurrentInfo.Exists)
            {
                throw new FileNotFoundException("expected-current image file not found", options.ExpectedCurrentImagePath);
            }
            if (expectedCurrentInfo.Length != expectedSize)
            {
                throw new InvalidOperationException(string.Format(
                    "expected-current image size mismatch for {0}: expected {1} bytes, got {2} bytes",
                    options.Partition,
                    expectedSize,
                    expectedCurrentInfo.Length));
            }
            Console.WriteLine("expect-current: {0}", expectedCurrentInfo.FullName);
        }

        if (options.DryRun)
        {
            Console.WriteLine("dry run only; no USB write was attempted");
            return 0;
        }

        if (!options.ExperimentalWrite)
        {
            throw new InvalidOperationException("experimental-flash requires --experimental-write");
        }
        if (!options.UnderstandsBrickRisk)
        {
            throw new InvalidOperationException("experimental-flash requires --i-understand-this-can-brick");
        }
        if (!options.VerifyAfterWrite)
        {
            throw new InvalidOperationException("experimental-flash requires --verify-after-write");
        }
        if (!options.VerifyBeforeWrite)
        {
            throw new InvalidOperationException("experimental-flash requires --verify-before-write");
        }
        if (string.Equals(options.Partition, "bootloader", StringComparison.OrdinalIgnoreCase) && !options.AllowBootloader)
        {
            throw new InvalidOperationException("bootloader flashing requires --allow-bootloader");
        }

        using (var dev = Open())
        {
            dev.FlashKnownPartition(
                options.Partition,
                info.FullName,
                expectedCurrentInfo == null ? null : expectedCurrentInfo.FullName,
                options.VerifyBeforeWrite,
                options.VerifyAfterWrite);
        }
        return 0;
    }

    private static ExperimentalCanaryOptions ParseExperimentalFlashCanaryArgs(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("experimental-flash-canary requires <partition> <image>");
        }

        var options = new ExperimentalCanaryOptions
        {
            Partition = NormalizePartition(args[1]),
            ImagePath = args[2],
            Offset = -1,
            ChunkSize = StoreChunk,
        };

        for (int i = 3; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--offset", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--offset requires a value");
                }
                options.Offset = ParseSize(args[++i]);
            }
            else if (string.Equals(arg, "--size", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--chunk-size", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException(arg + " requires a value");
                }
                long size = ParseSize(args[++i]);
                if (size > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(arg, "canary size is too large");
                }
                options.ChunkSize = (int)size;
            }
            else if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                options.DryRun = true;
            }
            else if (string.Equals(arg, "--experimental-write", StringComparison.OrdinalIgnoreCase))
            {
                options.ExperimentalWrite = true;
            }
            else if (string.Equals(arg, "--i-understand-this-can-brick", StringComparison.OrdinalIgnoreCase))
            {
                options.UnderstandsBrickRisk = true;
            }
            else if (string.Equals(arg, "--verify-before-write", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyBeforeWrite = true;
            }
            else if (string.Equals(arg, "--verify-after-write", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyAfterWrite = true;
            }
            else if (string.Equals(arg, "--allow-bootloader", StringComparison.OrdinalIgnoreCase))
            {
                options.AllowBootloader = true;
            }
            else
            {
                throw new ArgumentException("unknown experimental-flash-canary option: " + arg);
            }
        }

        if (options.DryRun && (options.ExperimentalWrite || options.UnderstandsBrickRisk ||
            options.VerifyBeforeWrite || options.VerifyAfterWrite))
        {
            throw new ArgumentException("--dry-run cannot be combined with write flags");
        }

        return options;
    }

    private static ExperimentalFlashOptions ParseExperimentalFlashArgs(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("experimental-flash requires <partition> <image>");
        }

        var options = new ExperimentalFlashOptions
        {
            Partition = NormalizePartition(args[1]),
            ImagePath = args[2],
        };

        for (int i = 3; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                options.DryRun = true;
            }
            else if (string.Equals(arg, "--expect-current", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--expect-current requires a value");
                }
                options.ExpectedCurrentImagePath = args[++i];
            }
            else if (string.Equals(arg, "--experimental-write", StringComparison.OrdinalIgnoreCase))
            {
                options.ExperimentalWrite = true;
            }
            else if (string.Equals(arg, "--i-understand-this-can-brick", StringComparison.OrdinalIgnoreCase))
            {
                options.UnderstandsBrickRisk = true;
            }
            else if (string.Equals(arg, "--verify-before-write", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyBeforeWrite = true;
            }
            else if (string.Equals(arg, "--verify-after-write", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyAfterWrite = true;
            }
            else if (string.Equals(arg, "--allow-bootloader", StringComparison.OrdinalIgnoreCase))
            {
                options.AllowBootloader = true;
            }
            else
            {
                throw new ArgumentException("unknown experimental-flash option: " + arg);
            }
        }

        if (options.DryRun && (options.ExperimentalWrite || options.UnderstandsBrickRisk ||
            options.VerifyBeforeWrite || options.VerifyAfterWrite))
        {
            throw new ArgumentException("--dry-run cannot be combined with write flags");
        }

        return options;
    }

    private static ExperimentalFlashOptions ParseFlashPartitionArgs(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("flash-partition requires <partition> <image>");
        }

        var options = new ExperimentalFlashOptions
        {
            Partition = NormalizePartition(args[1]),
            ImagePath = args[2],
        };

        for (int i = 3; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--expect-current", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--expect-current requires a value");
                }
                options.ExpectedCurrentImagePath = args[++i];
            }
            else if (string.Equals(arg, "--i-understand-this-can-brick", StringComparison.OrdinalIgnoreCase))
            {
                options.UnderstandsBrickRisk = true;
            }
            else if (string.Equals(arg, "--verify-before-write", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyBeforeWrite = true;
            }
            else if (string.Equals(arg, "--verify-after-write", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyAfterWrite = true;
            }
            else if (string.Equals(arg, "--allow-bootloader", StringComparison.OrdinalIgnoreCase))
            {
                options.AllowBootloader = true;
            }
            else
            {
                throw new ArgumentException("unknown flash-partition option: " + arg);
            }
        }

        return options;
    }

    private static void ReportProgress(int percent, string status)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        Console.WriteLine("KW_PROGRESS|{0}|{1}", percent, ProgressText(status));
    }

    private static int Percent(long done, long total)
    {
        if (total <= 0)
        {
            return 0;
        }
        return (int)Math.Max(0, Math.Min(100, (done * 100L) / total));
    }

    private static string DisplayPartition(string partition)
    {
        if (string.IsNullOrEmpty(partition))
        {
            return "partition";
        }
        return char.ToUpperInvariant(partition[0]) + partition.Substring(1).ToLowerInvariant();
    }

    private static string ProgressText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        return text.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
    }

    private sealed class OperationProgress
    {
        private readonly string status;
        private readonly long baseBytes;
        private readonly long totalBytes;
        private int lastPercent = -1;

        public OperationProgress(string status, long baseBytes, long totalBytes)
        {
            this.status = status;
            this.baseBytes = baseBytes;
            this.totalBytes = totalBytes;
        }

        public void Report(long currentBytes)
        {
            int percent = Percent(baseBytes + Math.Max(0, currentBytes), totalBytes);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                ReportProgress(percent, status);
            }
        }
    }

    private sealed class ExperimentalCanaryOptions
    {
        public string Partition;
        public string ImagePath;
        public long Offset;
        public int ChunkSize;
        public bool DryRun;
        public bool ExperimentalWrite;
        public bool UnderstandsBrickRisk;
        public bool VerifyBeforeWrite;
        public bool VerifyAfterWrite;
        public bool AllowBootloader;
    }

    private sealed class ExperimentalFlashOptions
    {
        public string Partition;
        public string ImagePath;
        public string ExpectedCurrentImagePath;
        public bool DryRun;
        public bool ExperimentalWrite;
        public bool UnderstandsBrickRisk;
        public bool VerifyBeforeWrite;
        public bool VerifyAfterWrite;
        public bool AllowBootloader;
    }

    private static void RequireFlag(string[] args, string requiredFlag)
    {
        if (!HasFlag(args, requiredFlag))
        {
            throw new InvalidOperationException("required flag missing: " + requiredFlag);
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void EnsureOnlyFlags(string[] args, int firstFlagIndex, params string[] allowedFlags)
    {
        for (int i = firstFlagIndex; i < args.Length; i++)
        {
            bool allowed = false;
            for (int j = 0; j < allowedFlags.Length; j++)
            {
                if (string.Equals(args[i], allowedFlags[j], StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
            {
                throw new ArgumentException("unknown option: " + args[i]);
            }
        }
    }

    private static string[] Subarray(string[] values, int start)
    {
        string[] result = new string[values.Length - start];
        Array.Copy(values, start, result, 0, result.Length);
        return result;
    }

    private static long ParseSize(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(text.Substring(2), 16);
        }
        return Convert.ToInt64(text);
    }

    private static WinUsbDevice Open()
    {
        List<string> paths = FindDevicePaths(ZadigInterfaceGuid, KarmaVidPid);
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("No WinUSB interface path found for USB\\" + KarmaVidPid);
        }

        Exception last = null;
        foreach (string path in paths)
        {
            try
            {
                Console.WriteLine("Opening {0}", path);
                return new WinUsbDevice(path);
            }
            catch (Exception ex)
            {
                last = ex;
                Console.Error.WriteLine("Open failed for {0}: {1}", path, ex.Message);
            }
        }

        throw new InvalidOperationException("Could not open any WinUSB interface", last);
    }

    private static List<string> FindDevicePaths(Guid interfaceGuid, string vidPid)
    {
        var paths = new List<string>();
        AddDynamicWinUsbPaths(paths, vidPid);
        string classKey = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\" + interfaceGuid.ToString("B").ToUpperInvariant();
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(classKey))
        {
            if (key == null)
            {
                return paths;
            }

            foreach (string subkeyName in key.GetSubKeyNames())
            {
                if (subkeyName.IndexOf(vidPid, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string path = subkeyName;
                if (path.StartsWith(@"##?#", StringComparison.Ordinal))
                {
                    path = @"\\?\" + path.Substring(4);
                }
                paths.Add(path);
            }
        }
        return paths;
    }

    private static void AddDynamicWinUsbPaths(List<string> paths, string vidPid)
    {
        string enumKey = @"SYSTEM\CurrentControlSet\Enum\USB\" + vidPid;
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(enumKey))
        {
            if (key == null)
            {
                return;
            }

            foreach (string instance in key.GetSubKeyNames())
            {
                using (RegistryKey instanceKey = key.OpenSubKey(instance))
                using (RegistryKey paramKey = key.OpenSubKey(instance + @"\Device Parameters"))
                {
                    if (instanceKey == null || paramKey == null)
                    {
                        continue;
                    }
                    string service = Convert.ToString(instanceKey.GetValue("Service"));
                    if (!string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string[] guids = paramKey.GetValue("DeviceInterfaceGUIDs") as string[];
                    if (guids == null || guids.Length == 0)
                    {
                        string single = Convert.ToString(paramKey.GetValue("DeviceInterfaceGUID"));
                        guids = string.IsNullOrWhiteSpace(single) ? new string[0] : new[] { single };
                    }
                    foreach (string guid in guids)
                    {
                        if (string.IsNullOrWhiteSpace(guid))
                        {
                            continue;
                        }
                        string path = @"\\?\USB#" + vidPid + "#" + instance + "#" + guid.ToLowerInvariant();
                        if (!paths.Contains(path))
                        {
                            paths.Add(path);
                        }
                    }
                }
            }
        }
    }

    private sealed class WinUsbDevice : IDisposable
    {
        private readonly IntPtr device;
        private readonly IntPtr winusb;
        private bool disposed;
        private bool skipLeadingUploadStatus;

        public WinUsbDevice(string path)
        {
            device = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (device == new IntPtr(-1))
            {
                throw Win32("CreateFile");
            }

            if (!WinUsb_Initialize(device, out winusb))
            {
                CloseHandle(device);
                throw Win32("WinUsb_Initialize");
            }

            uint timeout = 30000;
            SetPipeTimeout(PipeIn, timeout);
            SetPipeTimeout(PipeOut, timeout);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            WinUsb_Free(winusb);
            CloseHandle(device);
        }

        public void PrintIdentify()
        {
            byte[] identify = ControlIn(0x20, 0, 0, 8);
            Console.WriteLine("identify: {0}", Hex(identify, identify.Length));

            byte[] chip = ControlIn(0x02, 0xC801, 0x3C24, 12);
            Console.WriteLine("chip-id:  {0}", Hex(chip, chip.Length));
        }

        public string BulkCmd(string command)
        {
            return BulkCmd(command, false);
        }

        public string BulkCmd(string command, bool allowUploadStatusTimeout)
        {
            if (Encoding.ASCII.GetByteCount(command) >= 127)
            {
                throw new ArgumentException("bulk command must be shorter than 127 bytes");
            }
            Console.WriteLine("bulkcmd: {0}", command);
            byte[] payload = Encoding.ASCII.GetBytes(command + "\0");
            ControlOut(0x34, 0, 2, payload);
            byte[] reply;
            if (allowUploadStatusTimeout)
            {
                SetPipeTimeout(PipeIn, 2000);
            }
            try
            {
                reply = ReadPipe(BulkReplyLen);
            }
            catch (Win32Exception ex)
            {
                if (allowUploadStatusTimeout && ex.NativeErrorCode == 121 && command.StartsWith("upload ", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("bulkcmd reply timed out; continuing as pending upload");
                    return "";
                }
                throw;
            }
            finally
            {
                if (allowUploadStatusTimeout)
                {
                    SetPipeTimeout(PipeIn, 30000);
                }
            }
            string ascii = Ascii(reply);
            Console.WriteLine("bulkcmd reply: {0}", ascii);
            if (ascii.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) ||
                ascii.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("bulk command failed: " + ascii);
            }
            return ascii;
        }

        public void ResetPipes()
        {
            Console.WriteLine("resetting WinUSB pipes");
            if (!WinUsb_ResetPipe(winusb, PipeIn))
            {
                throw Win32("WinUsb_ResetPipe IN");
            }
            if (!WinUsb_ResetPipe(winusb, PipeOut))
            {
                throw Win32("WinUsb_ResetPipe OUT");
            }
        }

        public void Drain(long size)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            Console.WriteLine("draining {0} bytes from bulk IN", size);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = 0;
            byte[] buffer = new byte[PipeChunk];
            while (done < size)
            {
                int request = (int)Math.Min(buffer.Length, size - done);
                uint transferred;
                if (!WinUsb_ReadPipe(winusb, PipeIn, buffer, (uint)request, out transferred, IntPtr.Zero))
                {
                    throw Win32("WinUsb_ReadPipe drain");
                }
                if (transferred == 0)
                {
                    throw new IOException("zero-byte drain read");
                }
                done += transferred;
                if (done == size || done % (4L * 1024L * 1024L) == 0)
                {
                    double rate = done / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    Console.WriteLine("{0:N1} MiB drained ({1:N1} MiB/s)", done / 1048576.0, rate);
                }
            }
        }

        public void ReadBulkStatus()
        {
            Console.WriteLine("reading one bulk status packet");
            SetPipeTimeout(PipeIn, 2000);
            try
            {
                byte[] status = ReadPipe(BulkReplyLen);
                Console.WriteLine("bulk status ascii: {0}", Ascii(status));
                Console.WriteLine("bulk status hex: {0}", Hex(status, Math.Min(status.Length, 64)));
            }
            finally
            {
                SetPipeTimeout(PipeIn, 30000);
            }
        }

        public void RunRamWriteRoundTrip(int size)
        {
            byte[] expected = new byte[size];
            FillTestPattern(expected);

            Console.WriteLine("experimental RAM write test");
            Console.WriteLine("address: 0x{0:x}", StoreReadAddress);
            Console.WriteLine("size:    {0} bytes", size);

            DownloadMemory(StoreReadAddress, expected, expected.Length, 0);
            byte[] actual = UploadMemory(StoreReadAddress, expected.Length);

            if (!BuffersEqual(expected, actual, expected.Length))
            {
                throw new InvalidOperationException("RAM round-trip compare failed");
            }

            Console.WriteLine("RAM round-trip compare OK");
        }

        public void FlashKnownPartition(string partition, string imagePath, string expectedCurrentImagePath, bool verifyBeforeWrite, bool verifyAfterWrite)
        {
            long partitionSize = KnownPartitions[partition];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = 0;
            int sequence = 0;

            Console.WriteLine("EXPERIMENTAL FLASH START");
            Console.WriteLine("partition: {0}", partition);
            Console.WriteLine("image:     {0}", imagePath);
            Console.WriteLine("expect:    {0}", string.IsNullOrEmpty(expectedCurrentImagePath) ? "target image" : expectedCurrentImagePath);
            Console.WriteLine("verify:    before={0}, after={1}", verifyBeforeWrite ? "yes" : "no", verifyAfterWrite ? "yes" : "no");
            ReportProgress(0, "Preparing to flash " + DisplayPartition(partition));

            using (FileStream fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream expectedFs = string.IsNullOrEmpty(expectedCurrentImagePath) ? null : new FileStream(expectedCurrentImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (verifyBeforeWrite && expectedFs != null)
                {
                    Console.WriteLine("preflight verify expected-current image before writing");
                    VerifyPartitionAgainstImage(partition, expectedFs, partitionSize);
                    expectedFs.Seek(0, SeekOrigin.Begin);
                    Console.WriteLine("preflight verify OK");
                }

                ReportProgress(0, "Flashing " + DisplayPartition(partition));
                while (done < partitionSize)
                {
                    int chunk = (int)Math.Min(StoreChunk, partitionSize - done);
                    byte[] data = new byte[chunk];
                    ReadExact(fs, data, chunk);

                    Console.WriteLine("chunk {0}: offset 0x{1:x}, size 0x{2:x}", sequence, done, chunk);
                    if (verifyBeforeWrite)
                    {
                        Console.WriteLine("pre-write verify chunk {0}", sequence);
                        byte[] expectedBefore = data;
                        if (expectedFs != null)
                        {
                            expectedBefore = new byte[chunk];
                            ReadExact(expectedFs, expectedBefore, chunk);
                        }
                        byte[] before = ReadPartitionChunk(partition, done, chunk);
                        if (!BuffersEqual(expectedBefore, before, chunk))
                        {
                            string expectedLabel = expectedFs == null ? "target image" : "expected-current image";
                            throw new InvalidOperationException("pre-write verification failed at offset 0x" + done.ToString("x") + "; controller differs from " + expectedLabel + "; refusing to write this chunk");
                        }
                    }

                    DownloadMemory(StoreReadAddress, data, data.Length, 0);

                    string writeCommand = string.Format(
                        "store write {0} 0x{1:x} 0x{2:x} 0x{3:x}",
                        partition,
                        StoreReadAddress,
                        done,
                        chunk);
                    BulkCmd(writeCommand);

                    if (verifyAfterWrite)
                    {
                        Console.WriteLine("post-write verify chunk {0}", sequence);
                        byte[] actual = ReadPartitionChunk(partition, done, chunk);
                        if (!BuffersEqual(data, actual, chunk))
                        {
                            throw new InvalidOperationException("verification failed at offset 0x" + done.ToString("x"));
                        }
                    }

                    done += chunk;
                    sequence++;

                    double mb = done / 1048576.0;
                    double totalMb = partitionSize / 1048576.0;
                    double rate = done / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    Console.WriteLine("{0:N1}/{1:N1} MiB flashed ({2:N1} MiB/s)", mb, totalMb, rate);
                    ReportProgress(Percent(done, partitionSize), "Flashing " + DisplayPartition(partition));
                }
            }

            Console.WriteLine("EXPERIMENTAL FLASH COMPLETE");
            ReportProgress(100, DisplayPartition(partition) + " flash complete");
        }

        private void VerifyPartitionAgainstImage(string partition, FileStream expectedFs, long partitionSize)
        {
            long verified = 0;
            int sequence = 0;
            while (verified < partitionSize)
            {
                int chunk = (int)Math.Min(StoreChunk, partitionSize - verified);
                byte[] expected = new byte[chunk];
                ReadExact(expectedFs, expected, chunk);

                Console.WriteLine("preflight chunk {0}: offset 0x{1:x}, size 0x{2:x}", sequence, verified, chunk);
                byte[] actual = ReadPartitionChunk(partition, verified, chunk);
                if (!BuffersEqual(expected, actual, chunk))
                {
                    throw new InvalidOperationException("preflight verification failed at offset 0x" + verified.ToString("x") + "; controller differs from expected-current image; no write was attempted");
                }

                verified += chunk;
                sequence++;
                double mb = verified / 1048576.0;
                double totalMb = partitionSize / 1048576.0;
                Console.WriteLine("{0:N1}/{1:N1} MiB preflight verified", mb, totalMb);
                ReportProgress(Percent(verified, partitionSize), "Verifying " + DisplayPartition(partition) + " before flash");
            }
        }

        public void CanaryWritePartition(string partition, string imagePath, long offset, int size)
        {
            Console.WriteLine("EXPERIMENTAL CANARY WRITE START");
            Console.WriteLine("partition: {0}", partition);
            Console.WriteLine("offset:    0x{0:x}", offset);
            Console.WriteLine("size:      0x{0:x} ({0} bytes)", size);

            byte[] expected = ReadFileChunk(imagePath, offset, size);

            Console.WriteLine("pre-write verify: reading controller chunk");
            byte[] before = ReadPartitionChunk(partition, offset, size);
            if (!BuffersEqual(expected, before, size))
            {
                throw new InvalidOperationException("pre-write verification failed; controller chunk differs from image, refusing to write");
            }
            Console.WriteLine("pre-write verify OK; writing the same bytes back");

            DownloadMemory(StoreReadAddress, expected, expected.Length, 0);
            string writeCommand = string.Format(
                "store write {0} 0x{1:x} 0x{2:x} 0x{3:x}",
                partition,
                StoreReadAddress,
                offset,
                size);
            BulkCmd(writeCommand);

            Console.WriteLine("post-write verify: reading controller chunk");
            byte[] after = ReadPartitionChunk(partition, offset, size);
            if (!BuffersEqual(expected, after, size))
            {
                throw new InvalidOperationException("post-write verification failed at offset 0x" + offset.ToString("x"));
            }

            Console.WriteLine("EXPERIMENTAL CANARY WRITE COMPLETE");
        }

        public void BackupKnownPartition(string partition, string folder)
        {
            BackupKnownPartition(partition, folder, null);
        }

        public void BackupKnownPartition(string partition, string folder, OperationProgress progress)
        {
            long size;
            if (!KnownPartitions.TryGetValue(partition, out size))
            {
                throw new ArgumentException("unknown partition: " + partition);
            }
            Directory.CreateDirectory(folder);
            string outPath = Path.Combine(folder, FileNameForPartition(partition));
            ReadPartitionViaStore(partition, size, outPath, progress);
        }

        public void ReadPartition(string partition, long size, string outPath)
        {
            BeginPartitionRead(partition, size, outPath, false);
        }

        public void ReadPartitionViaStore(string partition, long size, string outPath)
        {
            ReadPartitionViaStore(partition, size, outPath, null);
        }

        public void ReadPartitionViaStore(string partition, long size, string outPath, OperationProgress progress)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = outPath + ".tmp";
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            Console.WriteLine("reading {0} bytes from {1} via store-read chunks to {2}", size, partition, outPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = 0;
            if (progress != null)
            {
                progress.Report(0);
            }
            using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                while (done < size)
                {
                    int chunk = (int)Math.Min(StoreChunk, size - done);
                    string storeCommand = string.Format("store read {0} 0x{1:x} 0x{2:x} 0x{3:x}", partition, StoreReadAddress, done, chunk);
                    BulkCmd(storeCommand);
                    string uploadCommand = string.Format("upload mem 0x{0:x} normal 0x{1:x}", StoreReadAddress, chunk);
                    BulkCmd(uploadCommand);

                    long chunkEnd = done + chunk;
                    while (done < chunkEnd)
                    {
                        int request = (int)Math.Min(PipeChunk, chunkEnd - done);
                        done = ReadMediaChunkToFile(fs, done, size, request, sw);
                        if (progress != null)
                        {
                            progress.Report(done);
                        }
                    }
                }
            }

            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
            File.Move(tmp, outPath);
            Console.WriteLine("complete: {0}", outPath);
        }

        private void DownloadMemory(uint address, byte[] data, int length, int sequence)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException("length");
            }
            string command = string.Format("download mem 0x{0:x} normal 0x{1:x}", address, length);
            BulkCmd(command);
            WriteMedia(data, length, sequence);
        }

        private byte[] UploadMemory(uint address, int size)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            string command = string.Format("upload mem 0x{0:x} normal 0x{1:x}", address, size);
            BulkCmd(command);
            return ReadCurrentUploadBytes(size);
        }

        private byte[] ReadPartitionChunk(string partition, long offset, int size)
        {
            string readCommand = string.Format(
                "store read {0} 0x{1:x} 0x{2:x} 0x{3:x}",
                partition,
                StoreReadAddress,
                offset,
                size);
            BulkCmd(readCommand);
            return UploadMemory(StoreReadAddress, size);
        }

        public void ReadPartitionOneShot(string partition, long size, string outPath)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            string command = string.Format("upload store {0} normal 0x{1:x}", partition, size);
            BulkCmd(command);
            ReadCurrentUploadOneShot(size, outPath);
        }

        public void ReadPartitionNoStatus(string partition, long size, string outPath)
        {
            BeginPartitionRead(partition, size, outPath, true);
        }

        private void BeginPartitionRead(string partition, long size, string outPath, bool noStatus)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string command = string.Format("upload store {0} normal 0x{1:x}", partition, size);
            if (noStatus)
            {
                BulkCmdNoStatus(command);
            }
            else
            {
                BulkCmd(command);
            }
            ReadCurrentUpload(size, outPath);
        }

        public void BulkCmdNoStatus(string command)
        {
            if (Encoding.ASCII.GetByteCount(command) >= 127)
            {
                throw new ArgumentException("bulk command must be shorter than 127 bytes");
            }
            Console.WriteLine("bulkcmd no-status: {0}", command);
            byte[] payload = Encoding.ASCII.GetBytes(command + "\0");
            ControlOut(0x34, 0, 2, payload);
            if (command.StartsWith("upload ", StringComparison.OrdinalIgnoreCase))
            {
                skipLeadingUploadStatus = true;
            }
        }

        public void ReadCurrentUpload(long size, string outPath)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = outPath + ".tmp";
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            Console.WriteLine("reading {0} bytes from current upload to {1}", size, outPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = 0;
            using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                while (done < size)
                {
                    int request = (int)Math.Min(PipeChunk, size - done);
                    done = ReadMediaChunkToFile(fs, done, size, request, sw);
                }
            }

            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
            File.Move(tmp, outPath);
            Console.WriteLine("complete: {0}", outPath);
        }

        public void ReadCurrentUploadOneShot(long size, string outPath)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            long blocksLong = (size + MediaBlockLen - 1) / MediaBlockLen;
            if (blocksLong > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException("size", "one-shot read is limited to 65535 media blocks");
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = outPath + ".tmp";
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            Console.WriteLine("reading {0} bytes from current upload to {1} with one READ_MEDIA request", size, outPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                ushort blocks = (ushort)blocksLong;
                byte[] ack = ControlIn(0x33, MediaBlockLen, blocks, 16);
                if (ack.Length > 0 && ack[0] != 0)
                {
                    Console.WriteLine("read-media ack: {0}", Hex(ack, ack.Length));
                    if (ack.Length >= 8)
                    {
                        int ackBytes = BitConverter.ToInt32(ack, 4);
                        if (ackBytes > 0 && ackBytes != size)
                        {
                            Console.WriteLine("read-media ack length {0} differs from requested {1}; reading requested size", ackBytes, size);
                        }
                    }
                }
                ReadBulkDataToFile(fs, 0, size, size, sw);
            }

            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
            File.Move(tmp, outPath);
            Console.WriteLine("complete: {0}", outPath);
        }

        private byte[] ReadCurrentUploadBytes(int size)
        {
            byte[] result = new byte[size];
            int done = 0;
            while (done < size)
            {
                int request = Math.Min(PipeChunk, size - done);
                done = ReadMediaChunkToBuffer(result, done, size, request);
            }
            return result;
        }

        private int ReadMediaChunkToBuffer(byte[] output, int done, int totalSize, int request)
        {
            if (request % MediaBlockLen != 0 && done + request < totalSize)
            {
                throw new IOException("media request must be a multiple of 4 KiB except at end of transfer");
            }
            int blocks = (request + MediaBlockLen - 1) / MediaBlockLen;
            byte[] ack = ControlIn(0x33, MediaBlockLen, (ushort)blocks, 16);
            int mediaBytes = request;
            if (ack.Length > 0 && ack[0] != 0)
            {
                if (ack.Length >= 8)
                {
                    mediaBytes = BitConverter.ToInt32(ack, 4);
                }
                if (mediaBytes != request)
                {
                    Console.WriteLine("read-media ack: {0}", Hex(ack, ack.Length));
                }
            }
            if (mediaBytes <= 0)
            {
                mediaBytes = request;
            }
            if (mediaBytes > totalSize - done)
            {
                throw new IOException("media-read ack requested " + mediaBytes + " bytes beyond the expected transfer size");
            }

            return ReadBulkDataToBuffer(output, done, mediaBytes);
        }

        private int ReadBulkDataToBuffer(byte[] output, int done, int bytesToRead)
        {
            byte[] buffer = new byte[PipeChunk];
            int chunkDone = 0;
            while (chunkDone < bytesToRead)
            {
                int pipeRequest = Math.Min(buffer.Length, bytesToRead - chunkDone);
                uint transferred;
                if (!WinUsb_ReadPipe(winusb, PipeIn, buffer, (uint)pipeRequest, out transferred, IntPtr.Zero))
                {
                    throw Win32("WinUsb_ReadPipe buffer data");
                }
                if (transferred == 0)
                {
                    throw new IOException("zero-byte read from media pipe");
                }

                if (done + transferred > output.Length)
                {
                    throw new IOException("read beyond output buffer");
                }

                Buffer.BlockCopy(buffer, 0, output, done, (int)transferred);
                done += (int)transferred;
                chunkDone += (int)transferred;
            }
            return done;
        }

        private void WriteMedia(byte[] data, int length, int sequence)
        {
            if (length <= 0 || length > data.Length)
            {
                throw new ArgumentOutOfRangeException("length");
            }

            byte[] controlData = new byte[0x20];
            WriteUInt32(controlData, 0, 0);
            WriteUInt32(controlData, 4, (uint)length);
            WriteUInt32(controlData, 8, (uint)sequence);
            WriteUInt32(controlData, 12, AmlsChecksum(data, length));
            WriteUInt16(controlData, 16, WriteMediaChecksumAlgAddSum);
            WriteUInt16(controlData, 18, WriteMediaAckLen);

            ControlOut(RequestWriteMedia, 1, 0xffff, controlData);
            WriteBulkData(data, 0, length);

            byte[] ack = ReadPipe(WriteMediaAckLen);
            string ascii = Ascii(ack);
            if (!ascii.StartsWith("OK!!", StringComparison.Ordinal))
            {
                Console.WriteLine("write-media ack: {0}", ascii);
                throw new InvalidOperationException("write-media failed: " + ascii);
            }
            Console.WriteLine("write-media ack: OK!!");
        }

        private void WriteBulkData(byte[] data, int offset, int length)
        {
            byte[] buffer = new byte[PipeChunk];
            int done = 0;
            while (done < length)
            {
                int request = Math.Min(buffer.Length, length - done);
                Buffer.BlockCopy(data, offset, buffer, 0, request);
                uint transferred;
                if (!WinUsb_WritePipe(winusb, PipeOut, buffer, (uint)request, out transferred, IntPtr.Zero))
                {
                    throw Win32("WinUsb_WritePipe data");
                }
                if (transferred == 0)
                {
                    throw new IOException("zero-byte write to media pipe");
                }
                offset += (int)transferred;
                done += (int)transferred;
            }
        }

        private long ReadMediaChunkToFile(FileStream fs, long done, long totalSize, int request, System.Diagnostics.Stopwatch sw)
        {
            if (request % MediaBlockLen != 0 && done + request < totalSize)
            {
                throw new IOException("media request must be a multiple of 4 KiB except at end of partition");
            }
            int blocks = (request + MediaBlockLen - 1) / MediaBlockLen;
            byte[] ack = ControlIn(0x33, MediaBlockLen, (ushort)blocks, 16);
            int mediaBytes = request;
            if (ack.Length > 0 && ack[0] != 0)
            {
                if (ack.Length >= 8)
                {
                    mediaBytes = BitConverter.ToInt32(ack, 4);
                }
                if (mediaBytes != request)
                {
                    Console.WriteLine("read-media ack: {0}", Hex(ack, ack.Length));
                }
            }
            if (mediaBytes <= 0)
            {
                mediaBytes = request;
            }
            if (mediaBytes > totalSize - done)
            {
                throw new IOException("media-read ack requested " + mediaBytes + " bytes beyond the expected partition size");
            }

            return ReadBulkDataToFile(fs, done, totalSize, mediaBytes, sw);
        }

        private long ReadBulkDataToFile(FileStream fs, long done, long totalSize, long bytesToRead, System.Diagnostics.Stopwatch sw)
        {
            byte[] buffer = new byte[PipeChunk];
            long chunkDone = 0;
            while (chunkDone < bytesToRead)
            {
                int pipeRequest = (int)Math.Min(buffer.Length, bytesToRead - chunkDone);
                uint transferred;
                if (!WinUsb_ReadPipe(winusb, PipeIn, buffer, (uint)pipeRequest, out transferred, IntPtr.Zero))
                {
                    throw Win32("WinUsb_ReadPipe data");
                }
                if (transferred == 0)
                {
                    throw new IOException("zero-byte read from media pipe");
                }
                if (skipLeadingUploadStatus)
                {
                    skipLeadingUploadStatus = false;
                    if (LooksLikeStatusPacket(buffer, (int)transferred))
                    {
                        Console.WriteLine("discarded leading upload status: {0}", Ascii(buffer));
                        continue;
                    }
                }
                fs.Write(buffer, 0, (int)transferred);
                done += transferred;
                chunkDone += transferred;

                if (done == totalSize || done % (4L * 1024L * 1024L) == 0)
                {
                    double mb = done / 1048576.0;
                    double totalMb = totalSize / 1048576.0;
                    double rate = done / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    Console.WriteLine("{0:N1}/{1:N1} MiB ({2:N1} MiB/s)", mb, totalMb, rate);
                }
            }
            return done;
        }

        private byte[] ControlIn(byte request, ushort value, ushort index, ushort length)
        {
            var setup = new WINUSB_SETUP_PACKET
            {
                RequestType = 0xC0,
                Request = request,
                Value = value,
                Index = index,
                Length = length,
            };
            byte[] buffer = new byte[length];
            uint transferred;
            if (!WinUsb_ControlTransfer(winusb, setup, buffer, (uint)buffer.Length, out transferred, IntPtr.Zero))
            {
                throw Win32("WinUsb_ControlTransfer IN 0x" + request.ToString("X2"));
            }
            if (transferred == buffer.Length)
            {
                return buffer;
            }
            byte[] result = new byte[transferred];
            Array.Copy(buffer, result, result.Length);
            return result;
        }

        private void SetPipeTimeout(byte pipeId, uint milliseconds)
        {
            if (!WinUsb_SetPipePolicy(winusb, pipeId, PIPE_TRANSFER_TIMEOUT, 4, ref milliseconds))
            {
                throw Win32("WinUsb_SetPipePolicy timeout");
            }
        }

        private void ControlOut(byte request, ushort value, ushort index, byte[] data)
        {
            var setup = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x40,
                Request = request,
                Value = value,
                Index = index,
                Length = (ushort)data.Length,
            };
            uint transferred;
            if (!WinUsb_ControlTransfer(winusb, setup, data, (uint)data.Length, out transferred, IntPtr.Zero))
            {
                throw Win32("WinUsb_ControlTransfer OUT 0x" + request.ToString("X2"));
            }
            if (transferred != data.Length)
            {
                throw new IOException("short control write: " + transferred + " of " + data.Length);
            }
        }

        private byte[] ReadPipe(int size)
        {
            byte[] buffer = new byte[size];
            uint transferred;
            if (!WinUsb_ReadPipe(winusb, PipeIn, buffer, (uint)buffer.Length, out transferred, IntPtr.Zero))
            {
                throw Win32("WinUsb_ReadPipe");
            }
            if (transferred == buffer.Length)
            {
                return buffer;
            }
            byte[] result = new byte[transferred];
            Array.Copy(buffer, result, result.Length);
            return result;
        }

    }

    private static void FillTestPattern(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 37 + 0x5a) & 0xff);
        }
    }

    private static bool BuffersEqual(byte[] expected, byte[] actual, int length)
    {
        if (expected.Length < length || actual.Length < length)
        {
            return false;
        }
        for (int i = 0; i < length; i++)
        {
            if (expected[i] != actual[i])
            {
                Console.WriteLine("first mismatch at byte 0x{0:x}: expected 0x{1:x2}, got 0x{2:x2}", i, expected[i], actual[i]);
                return false;
            }
        }
        return true;
    }

    private static void ReadExact(Stream stream, byte[] buffer, int length)
    {
        int done = 0;
        while (done < length)
        {
            int read = stream.Read(buffer, done, length - done);
            if (read == 0)
            {
                throw new EndOfStreamException("unexpected end of image file");
            }
            done += read;
        }
    }

    private static byte[] ReadFileChunk(string path, long offset, int size)
    {
        byte[] buffer = new byte[size];
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            ReadExact(fs, buffer, size);
        }
        return buffer;
    }

    private static uint AmlsChecksum(byte[] data, int length)
    {
        ulong checksum = 0;
        for (int offset = 0; offset < length; offset += 4)
        {
            uint value = 0;
            int remaining = Math.Min(4, length - offset);
            for (int i = 0; i < remaining; i++)
            {
                value |= (uint)data[offset + i] << (8 * i);
            }
            checksum = (checksum + value) & 0xffffffffUL;
        }
        return (uint)checksum;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        buffer[offset + 2] = (byte)((value >> 16) & 0xff);
        buffer[offset + 3] = (byte)((value >> 24) & 0xff);
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset + 0] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static string FileNameForPartition(string partition)
    {
        switch (partition.ToLowerInvariant())
        {
            case "bootloader":
                return "bootloaderBU.img";
            case "boot":
                return "bootBU.img";
            case "recovery":
                return "recoveryBU.img";
            case "system":
                return "systemBU.img";
            case "data":
                return "dataBU.img";
            case "gopro":
                return "goproBU.img";
            default:
                return partition + "BU.img";
        }
    }

    private static bool LooksLikeStatusPacket(byte[] data, int length)
    {
        if (length <= 0 || length > 512)
        {
            return false;
        }
        string text = Ascii(data);
        return text == "success" ||
            text.StartsWith("OKAY", StringComparison.Ordinal) ||
            text.StartsWith("FAIL", StringComparison.Ordinal);
    }

    private static Exception Win32(string op)
    {
        int err = Marshal.GetLastWin32Error();
        Win32Exception inner = new Win32Exception(err);
        return new Win32Exception(err, op + " failed: " + err + " (" + inner.Message + ")");
    }

    private static string Hex(byte[] data, int length)
    {
        string[] parts = new string[length];
        for (int i = 0; i < length; i++)
        {
            parts[i] = data[i].ToString("X2");
        }
        return string.Join(" ", parts);
    }

    private static string Ascii(byte[] data)
    {
        int length = Array.IndexOf(data, (byte)0);
        if (length < 0)
        {
            length = data.Length;
        }
        return Encoding.ASCII.GetString(data, 0, length).TrimEnd();
    }
}
