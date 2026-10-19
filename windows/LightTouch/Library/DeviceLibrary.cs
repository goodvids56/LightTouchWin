// What the app keeps, under %LOCALAPPDATA%\LightTouch (LTM_STATE_DIR moves it):
//
//   Assets\                   the SecureROMs (bootrom_240_4, bootrom_s5l8900), from Light Touch for Mac
//   Devices\<id>\device.json  the record: name, board, version
//   Devices\<id>\base\        the prepared base (firmwarekit create's output, or the unpacked built-in iPod)
//   Devices\<id>\data\        the device's own state: the NAND overlay or live NAND, the writable NOR
//   Devices\<id>\logs\        emulator.log and serial.log of the last boot
//   settings.json             app-wide choices
//
// Erase removes data\, so the next boot starts from the base again.

using System.Text.Json;
using System.Text.Json.Serialization;
using LightTouch.Device;

namespace LightTouch.Library;

internal sealed class DeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Board { get; init; }
    public string? Version { get; init; }
    public string? Build { get; init; }
    public DateTime Created { get; init; } = DateTime.UtcNow;
    public bool Network { get; set; } = true;

    [JsonIgnore] public string Directory { get; set; } = "";
    [JsonIgnore] public string Base => Path.Combine(Directory, "base");
    [JsonIgnore] public string Data => Path.Combine(Directory, "data");
    [JsonIgnore] public string Overlay => Path.Combine(Data, "overlay");
    [JsonIgnore] public string WritableNor => Path.Combine(Data, "nor.bin");
    [JsonIgnore] public string Logs => Path.Combine(Directory, "logs");
    [JsonIgnore] public string Lease => Path.Combine(Directory, "running.lock");
    /// The running device's usbmuxd client address (USBMUXD_SOCKET_ADDRESS), while it runs.
    [JsonIgnore] public string UsbMuxAddress => Path.Combine(Directory, "usbmux-address");
    [JsonIgnore] public Board? BoardFacts => Device.Board.Find(Board);

    [JsonIgnore]
    public string Subtitle => string.Join(" · ",
        new[] { BoardFacts?.MarketingName ?? Board, Version is null ? null : $"iOS {Version}", Build }.Where(s => s != null));
}

internal sealed class AppSettings
{
    public bool Audio { get; set; } = true;
    public bool VerboseBoot { get; set; }
    public bool LocalNetwork { get; set; }
    public int Scale { get; set; } = 100;
}

internal sealed class DeviceLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Root { get; }
    public string Assets => Path.Combine(Root, "Assets");
    public string Devices => Path.Combine(Root, "Devices");
    private string SettingsPath => Path.Combine(Root, "settings.json");

    public DeviceLibrary(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("LTM_STATE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightTouch");
        System.IO.Directory.CreateDirectory(Assets);
        System.IO.Directory.CreateDirectory(Devices);
    }

    public List<DeviceRecord> Load()
    {
        var list = new List<DeviceRecord>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Devices))
        {
            if (Read(dir) is { } record) list.Add(record);
        }
        return list.OrderBy(r => r.Created).ToList();
    }

    public static DeviceRecord? Read(string dir)
    {
        var path = Path.Combine(dir, "device.json");
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<DeviceRecord>(File.ReadAllText(path), Json);
            if (record != null) record.Directory = dir;
            return record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(DeviceRecord record)
    {
        var path = Path.Combine(record.Directory, "device.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public AppSettings Settings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));

    /// The SecureROM `board` boots, under Assets.
    public string BootRom(Board board) => Path.Combine(Assets, board.BootRom);

    /// Whether the device's helper process holds its lease (it is running).
    public static bool IsRunning(DeviceRecord record)
    {
        if (!File.Exists(record.Lease)) return false;
        try
        {
            using var probe = new FileStream(record.Lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // MARK: - Adding devices

    /// A new device from a prepared base folder (firmwarekit create's output, or a device's base\ copied from a
    /// Mac): its files copied in, the NAND chip files sparse.
    public DeviceRecord ImportBase(string source, Action<double>? progress = null)
    {
        var lockFile = DeviceLock.Read(source)
            ?? throw new InvalidDataException("That folder has no device.lock.json: it isn't a prepared device.");
        var board = Board.Find(lockFile.Board)
            ?? throw new InvalidDataException($"Light Touch doesn't know the board {lockFile.Board ?? "(none)"}.");
        var record = NewRecord(board, lockFile);
        try
        {
            var total = System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            long done = 0;
            SparseFile.CopyTree(source, record.Base, n => progress?.Invoke((double)(done += n) / Math.Max(total, 1)));
            Save(record);
            return record;
        }
        catch
        {
            SparseFile.DeleteTree(record.Directory);
            throw;
        }
    }

    /// A new device from the packed base `name` in `parent` (the built-in iPod), with an identity of its own.
    public DeviceRecord ImportPacked(IFileSource parent, string name, Action<double>? progress = null,
        CancellationToken cancel = default)
    {
        var staging = Path.Combine(Devices, $".import-{Guid.NewGuid():N}");
        try
        {
            PackedBase.Unpack(parent, name, staging, progress, cancel);
            var seed = Guid.NewGuid().ToString().ToUpperInvariant();
            PackedBase.Reseed(staging, seed);
            var lockFile = DeviceLock.Read(staging) ?? throw new InvalidDataException("The packed device has no lock.");
            var board = Board.Find(lockFile.Board) ?? throw new InvalidDataException($"Unknown board {lockFile.Board}.");
            var record = NewRecord(board, lockFile);
            System.IO.Directory.Move(staging, record.Base);
            Save(record);
            return record;
        }
        finally
        {
            SparseFile.DeleteTree(staging);
        }
    }

    private DeviceRecord NewRecord(Board board, DeviceLock lockFile)
    {
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Devices, id);
        System.IO.Directory.CreateDirectory(dir);
        var siblings = Load().Count(r => r.Board == board.Id);
        return new DeviceRecord
        {
            Id = id,
            Name = siblings == 0 ? board.MarketingName : $"{board.MarketingName} {siblings + 1}",
            Board = board.Id,
            Version = lockFile.ProductVersion,
            Build = lockFile.Build,
            Directory = dir,
        };
    }

    /// Light Touch for Mac's resources (a release zip, or the Light Touch.app folder): the SecureROMs into Assets,
    /// and the built-in iPod as a new device. Returns the device, or null when the app had none.
    public DeviceRecord? ImportMacApp(string path, Action<string, double>? progress = null, CancellationToken cancel = default)
    {
        using var source = OpenMacResources(path, out var resources);
        using var device = source.Sub(resources);
        var copied = 0;
        foreach (var rom in new[] { "bootrom_240_4", "bootrom_s5l8900" })
        {
            foreach (var candidate in new[] { rom, $"ipod1g/{rom}" })
            {
                if (!device.Exists(candidate)) continue;
                using var input = device.Open(candidate);
                using var output = File.Create(Path.Combine(Assets, rom));
                input.CopyTo(output);
                copied++;
                break;
            }
        }
        const string builtIn = "n72ap-7E18.itbase";
        if (!device.IsFile(builtIn) && !device.Exists($"{builtIn}/index.json"))
        {
            if (copied == 0) throw new InvalidDataException("That Light Touch for Mac has no SecureROMs or built-in iPod.");
            return null;
        }
        return ImportPacked(device, builtIn, f => progress?.Invoke("Unpacking the built-in iPod", f), cancel);
    }

    /// `path`'s Contents/Resources/Device, as a source and the folder in it.
    private static IFileSource OpenMacResources(string path, out string resources)
    {
        const string suffix = "Contents/Resources/Device/";
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var zip = new ZipSource(path);
            resources = zip.Find(suffix) ?? throw new InvalidDataException("That zip doesn't hold Light Touch for Mac.");
            return zip;
        }
        if (System.IO.Directory.Exists(path))
        {
            foreach (var candidate in new[] { Path.Combine(path, "Contents", "Resources", "Device"), path })
            {
                if (System.IO.Directory.Exists(candidate))
                {
                    resources = "";
                    return new FolderSource(candidate);
                }
            }
        }
        throw new InvalidDataException("Choose LightTouchMac-universal.zip or the Light Touch.app folder.");
    }

    /// Removes the device's own state; the next boot starts from its base.
    public static void Erase(DeviceRecord record) => SparseFile.DeleteTree(record.Data);

    public static void Delete(DeviceRecord record) => SparseFile.DeleteTree(record.Directory);
}
