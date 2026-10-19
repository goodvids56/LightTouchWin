// A prepared base made ready to boot (HostRuntime's PreparedDeviceBoot): its boot files checked, the device's writable
// NOR copied from the base on first boot, and an image store's live NAND made as a sparse copy of the base's.

using System.Text.Json.Nodes;

namespace LightTouch.Device;

internal sealed class PreparedDevice
{
    public required Board Board { get; init; }
    public required string BootFile { get; init; }
    public required string Nand { get; init; }
    /// The NAND overlay the emulator writes; null when it writes `Nand` itself (an image store's live copy).
    public string? NandOverlay { get; init; }
    public required string BaseNor { get; init; }
    public string? WritableNor { get; init; }
    public required string Overlay { get; init; }
    public required string BootRom { get; init; }
    public string? Strategy { get; init; }
    public string? GidBlobs { get; init; }
    public string? DieId { get; init; }
    public required Dictionary<string, string> Machine { get; init; }
    public DeviceLock? Lock { get; init; }

    /// `progress` reports bytes copied while a first boot makes the live NAND.
    public static PreparedDevice Prepare(Board board, string baseDir, string overlay, string? writableNor,
        string bootRom, Action<long, long>? progress = null)
    {
        var lockFile = DeviceLock.Read(baseDir);
        var strategy = lockFile?.BootStrategy;
        var (boot, also) = board.RequiredFiles(strategy);
        var bootFile = Path.Combine(baseDir, boot);
        var nand = Path.Combine(baseDir, "nand");
        foreach (var file in new[] { bootFile, nand }.Concat(also.Select(f => Path.Combine(baseDir, f))))
        {
            if (!File.Exists(file) && !Directory.Exists(file))
            {
                throw new FileNotFoundException($"The prepared device is missing {Path.GetFileName(file)}.", file);
            }
        }
        Directory.CreateDirectory(overlay);
        if (writableNor != null && !File.Exists(writableNor))
        {
            var staged = writableNor + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(writableNor)!);
            File.Copy(Path.Combine(baseDir, "nor.bin"), staged, overwrite: true);
            File.SetAttributes(staged, FileAttributes.Normal);
            File.Move(staged, writableNor);
        }
        if (!board.IsKBoot && writableNor == null) throw new FileNotFoundException("writable NOR");
        if (!File.Exists(bootRom) && (board.SoC == SoC.S5L8900 || board.SoC == SoC.S5L8720))
        {
            throw new FileNotFoundException(
                $"The {board.ShortName}'s SecureROM ({Path.GetFileName(bootRom)}) isn't installed. Import it from "
                + "Light Touch for Mac (File > Import from Light Touch for Mac) or copy it into the assets folder.",
                bootRom);
        }
        if (board.SoC == SoC.S5L8920 && boot == "kboot.bin") bootFile = AudioClockBoot(bootFile, overlay);
        string? nandOverlay = overlay;
        if (IsImageStore(nand))
        {
            nand = LiveNand(nand, overlay, progress);
            nandOverlay = null;
        }
        string? dieId = null;
        var identityPath = Path.Combine(baseDir, "identity.json");
        if (File.Exists(identityPath) && JsonNode.Parse(File.ReadAllText(identityPath))?["die-id"] is JsonArray words)
        {
            dieId = string.Join(":", words.Select(w => (string?)w));
        }
        return new PreparedDevice
        {
            Board = board,
            BootFile = bootFile,
            Nand = nand,
            NandOverlay = nandOverlay,
            BaseNor = Path.Combine(baseDir, "nor.bin"),
            WritableNor = writableNor,
            Overlay = overlay,
            BootRom = bootRom,
            Strategy = strategy,
            GidBlobs = board.SoC == SoC.S5L8900 ? null : Path.Combine(baseDir, "gid-blobs.bin"),
            DieId = dieId,
            Machine = lockFile?.MachineOptions(baseDir) ?? [],
            Lock = lockFile,
        };
    }

    /// /arm-io/audio-complex ncoref-frequency (S5L8920/8922): iBoot fills it with PLL1's 162 MHz, which the kernel's
    /// audio complex divides to find each I2S port's MCLK; left 0, iPhone OS 3's mediaserverd finds no audio device.
    public const uint NcorefHz = 162_000_000;

    /// The kboot.bin a 3GS or iPod touch 3G boots. One prepared before FirmwareKit filled ncoref-frequency has the
    /// IPSW's 0: the first boot writes a copy with the value set into the overlay, read back before it's used, and later
    /// boots use that copy. The base stays as it was; Erase empties the overlay and the next boot writes it again.
    public static string AudioClockBoot(string kboot, string overlay)
    {
        var repaired = Path.Combine(overlay, "kboot.bin");
        if (File.Exists(repaired)) return repaired;
        var image = File.ReadAllBytes(kboot);
        if (NcorefOffset(image) is not { } at || image.AsSpan(at, 4).ContainsAnyExcept((byte)0)) return kboot;
        BitConverter.TryWriteBytes(image.AsSpan(at, 4), NcorefHz);
        var staged = Path.Combine(overlay, $".kboot-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(staged, image);
            if (!File.ReadAllBytes(staged).AsSpan().SequenceEqual(image)) throw new IOException($"Couldn't write {staged}");
            File.Move(staged, repaired);
        }
        finally
        {
            File.Delete(staged);
        }
        Console.Error.WriteLine($"LightTouch: kboot.bin's ncoref-frequency was 0; booting {repaired}");
        return repaired;
    }

    /// Where the device tree property's 4-byte value is (a 32-byte name, a length of 4, the value), when it occurs
    /// exactly once.
    public static int? NcorefOffset(byte[] image)
    {
        var key = new byte[36];
        "ncoref-frequency"u8.CopyTo(key);
        key[32] = 4;
        var first = image.AsSpan().IndexOf(key);
        if (first < 0) return null;
        var end = first + key.Length;
        if (image.AsSpan(end).IndexOf(key) >= 0 || end + 4 > image.Length) return null;
        return end;
    }

    /// An image store (geometry.json nand-xor-ff-v2 or nand-xor-blank-v2) needs no page overlay: the device's NAND is
    /// a copy of the base's, `overlay\nand`, which the emulator writes in place. Erase removes it.
    public static bool IsImageStore(string nand)
    {
        var geometry = Path.Combine(nand, "geometry.json");
        if (!File.Exists(geometry)) return false;
        var format = (string?)JsonNode.Parse(File.ReadAllText(geometry))?["storage_format"];
        return format is "nand-xor-ff-v2" or "nand-xor-blank-v2";
    }

    public static string LiveNand(string overlay) => Path.Combine(overlay, "nand");

    /// The device's live NAND, copied (sparse) from the base's image store on the first boot after import or Erase.
    private static string LiveNand(string baseNand, string overlay, Action<long, long>? progress)
    {
        var live = LiveNand(overlay);
        // Page files an older build wrote over this base: never booted as an image, and never silently dropped.
        foreach (var name in new[] { "bus0-ce0.pages", "bus0-ce0.dirty", "cs0", "bank0" })
        {
            if (File.Exists(Path.Combine(overlay, name)) || Directory.Exists(Path.Combine(overlay, name)))
            {
                throw new InvalidDataException("This device's data was written in an older format over a newer system image.");
            }
        }
        if (Directory.Exists(live)) return live;
        var staged = Path.Combine(overlay, $".nand-{Guid.NewGuid():N}.tmp");
        try
        {
            var total = Directory.EnumerateFiles(baseNand).Sum(f => new FileInfo(f).Length);
            long done = 0;
            SparseFile.CopyTree(baseNand, staged, n => progress?.Invoke(done += n, total));
            Directory.Move(staged, live);
        }
        finally
        {
            SparseFile.DeleteTree(staged);
        }
        return live;
    }

    /// The boot's argv: the board's recipe, then audio, the serial log, the network and the panel's refresh.
    public BootConfig Configuration(Native.Qemu.DeviceInfo hardware, BootOptions o, string serialLog)
    {
        var restricted = o.Network && ExpectsSetup;
        var netdev = o.Network ? BootRecipe.WifiNetdev(restricted, o.LocalNetwork) : null;
        List<string> argv;
        var environment = new Dictionary<string, string>();
        switch (Board.SoC)
        {
            case SoC.S5L8900:
                argv = BootRecipe.IPod1G(this, hardware.MachineName, hardware.HasCellular, o, netdev);
                break;
            case SoC.S5L8720:
                argv = BootRecipe.IPod(this, hardware.MachineName, o);
                // The setting 3.1.3 will not boot without.
                environment["IT_TVOUT_READY"] = "1";
                break;
            default:
                argv = BootRecipe.IPad(this, hardware.MachineName, hardware.HasCellular, hardware.HasUsbHost, o, netdev);
                break;
        }
        // A 186 ms buffer rides out a busy emulator thread, as the Mac's 16 CoreAudio buffers do.
        argv.AddRange(["-audio", o.Audio ? "driver=dsound,out.buffer-length=186000" : "driver=none"]);
        argv.AddRange(["-serial", $"file:{serialLog}"]);
        if (netdev != null) argv.AddRange(["-netdev", netdev]);
        // The SIM keeps its PIN, PUK and tries left with the device's own state.
        if (hardware.HasCellular) argv.AddRange(["-global", $"ios-baseband.sim-file={Path.Combine(Overlay, "sim")}"]);
        argv.AddRange(Board.RefreshGlobals(o.RefreshHz));
        return new BootConfig(argv, environment, hardware.MachineName);
    }

    /// The overlay has been through Setup: the person opened the network after it. Erase removes the mark.
    public string SetupDoneMark => Path.Combine(Overlay, ".setup-done");

    /// This boot ends in Setup: iOS 5 or later, prepared without Skip Setup, on an overlay that hasn't finished it.
    public bool ExpectsSetup =>
        BootRecipe.SetupPhonesHome(Lock?.ProductVersion) && Lock?.SkippedSetup != true && !File.Exists(SetupDoneMark);
}
