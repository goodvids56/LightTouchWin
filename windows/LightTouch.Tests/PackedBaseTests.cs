using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightTouch.Device;
using LightTouch.Library;
using Xunit;

namespace LightTouch.Tests;

public sealed class PackedBaseTests : IDisposable
{
    private const int Chunk = 1 << 20;
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "ltw-tests-" + Guid.NewGuid().ToString("N"));

    public PackedBaseTests() => Directory.CreateDirectory(tmp);

    public void Dispose() => Directory.Delete(tmp, recursive: true);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// A packed base the way PreparedBase.pack lays one out: files whole, the image's non-zero chunks zlib'd.
    private string Pack(Dictionary<string, byte[]> files, string imageName, byte[] image, Action<string>? tamper = null)
    {
        var dir = Path.Combine(tmp, "base.itbase");
        var fileEntries = new JsonArray();
        foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, "files", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            fileEntries.Add(new JsonObject { ["name"] = name, ["size"] = data.Length, ["mode"] = 420, ["sha256"] = Sha(data) });
        }
        var chunks = new JsonObject();
        var stem = imageName[..^".pages".Length];
        for (var i = 0; i * Chunk < image.Length; i++)
        {
            var raw = image.AsSpan(i * Chunk, Math.Min(Chunk, image.Length - i * Chunk)).ToArray();
            if (!raw.Any(b => b != 0)) continue;
            var number = i.ToString("D6");
            var path = Path.Combine(dir, stem, number + ".z");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = File.Create(path))
            using (var z = new ZLibStream(output, CompressionLevel.Optimal))
            {
                z.Write(raw);
            }
            chunks[number] = Sha(raw);
        }
        var index = new JsonObject
        {
            ["format"] = 2,
            ["chunkBytes"] = Chunk,
            ["files"] = fileEntries,
            ["images"] = new JsonArray(new JsonObject
            {
                ["name"] = imageName, ["size"] = image.Length, ["mode"] = 420, ["chunks"] = chunks,
            }),
        };
        File.WriteAllText(Path.Combine(dir, "index.json"), index.ToJsonString());
        tamper?.Invoke(dir);
        return dir;
    }

    private static byte[] Image()
    {
        var image = new byte[3 * Chunk + 4160];
        image[Chunk + 5] = 7;
        image[^1] = 9;
        return image;
    }

    [Fact]
    public void UnpackRestoresEveryFileAndTheImage()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["device.lock.json"] = Encoding.UTF8.GetBytes("{\"board\": \"n72ap\"}"),
            ["nand/geometry.json"] = Encoding.UTF8.GetBytes("{\"storage_format\": \"nand-xor-blank-v2\"}"),
            ["empty"] = [],
        };
        var image = Image();
        var packed = Pack(files, "nand/cs0.pages", image);
        var fractions = new List<double>();
        var output = Path.Combine(tmp, "out");
        PackedBase.Unpack(new FolderSource(packed), output, fractions.Add);
        foreach (var (name, data) in files) Assert.Equal(data, File.ReadAllBytes(Path.Combine(output, name)));
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(output, "nand", "cs0.pages")));
        Assert.Equal(1, fractions[^1]);
        Assert.Equal(fractions.OrderBy(f => f), fractions);
        Assert.True(PreparedDevice.IsImageStore(Path.Combine(output, "nand")));
    }

    [Fact]
    public void UnpackReadsAZipInPlace()
    {
        var packed = Pack(new() { ["device.lock.json"] = "{}"u8.ToArray() }, "nand/cs0.pages", Image());
        var zip = Path.Combine(tmp, "LightTouchMac-universal.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(packed, "*", SearchOption.AllDirectories))
            {
                archive.CreateEntryFromFile(file,
                    "Light Touch.app/Contents/Resources/Device/n72ap-7E18.itbase/"
                    + Path.GetRelativePath(packed, file).Replace('\\', '/'));
            }
        }
        using var source = new ZipSource(zip);
        var resources = source.Find("Contents/Resources/Device/");
        Assert.Equal("Light Touch.app/Contents/Resources/Device/", resources);
        using var device = source.Sub(resources!);
        Assert.True(device.Exists("n72ap-7E18.itbase/index.json"));
        var output = Path.Combine(tmp, "zipped");
        PackedBase.Unpack(device, "n72ap-7E18.itbase", output);
        Assert.Equal(Image(), File.ReadAllBytes(Path.Combine(output, "nand", "cs0.pages")));
    }

    [Fact]
    public void SingleStreamFormatUnpacksInIndexOrder()
    {
        var entries = new (string Name, byte[] Data)[]
        {
            ("device.lock.json", "{\"board\": \"n72ap\"}"u8.ToArray()),
            ("nand/cs0/0000001", Enumerable.Range(0, 4160).Select(i => (byte)i).ToArray()),
            ("empty", []),
            ("nor.bin", new byte[3 * Chunk]),
        };
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            entries = entries.Select(e => new { name = e.Name, size = e.Data.Length, mode = 420 }),
        });
        var blob = Path.Combine(tmp, "n72ap-7E18.itbase");
        using (var output = File.Create(blob))
        {
            output.Write("ITPACK01"u8);
            output.Write(BitConverter.GetBytes(index.Length));
            output.Write(index);
            using var z = new ZLibStream(output, CompressionLevel.Fastest);
            foreach (var e in entries) z.Write(e.Data);
        }
        var fractions = new List<double>();
        var into = Path.Combine(tmp, "out");
        PackedBase.Unpack(new FolderSource(tmp), "n72ap-7E18.itbase", into, fractions.Add);
        foreach (var (name, data) in entries) Assert.Equal(data, File.ReadAllBytes(Path.Combine(into, name)));
        Assert.Equal(1, fractions[^1]);
        Assert.Equal(fractions.OrderBy(f => f), fractions);
    }

    /// LTW_MAC_RELEASE=path/to/LightTouchMac-universal.zip imports the real built-in iPod; LTW_IMPORT_ROOT keeps the
    /// library it made. Without them there is nothing to check.
    [Fact]
    public void ImportsTheMacReleasesBuiltInIPod()
    {
        var release = Environment.GetEnvironmentVariable("LTW_MAC_RELEASE");
        if (release == null) return;
        var root = Environment.GetEnvironmentVariable("LTW_IMPORT_ROOT") ?? Path.Combine(tmp, "library");
        var library = new DeviceLibrary(root);
        var record = library.ImportMacApp(release);
        Assert.NotNull(record);
        Assert.Equal("n72ap", record.Board);
        Assert.True(File.Exists(library.BootRom(Board.N72)));
        Assert.True(File.Exists(Path.Combine(record.Base, DeviceLock.FileName)));
        var device = PreparedDevice.Prepare(Board.N72, record.Base, record.Overlay, record.WritableNor, library.BootRom(Board.N72));
        Assert.True(File.Exists(record.WritableNor));
        var identity = UnitIdentity.Load(Path.Combine(record.Base, "identity.json"));
        Assert.NotEqual("", identity["udid"]);
        Assert.Contains(",ecid=" + identity["unique-chip-id"], device.Machine.Select(m => $",{m.Key}={m.Value}"));
    }

    [Fact]
    public void DamagedChunkIsRefused()
    {
        var packed = Pack(new(), "nand/cs0.pages", Image(), dir =>
        {
            var chunk = Path.Combine(dir, "nand", "cs0", "000001.z");
            using var output = File.Create(chunk);
            using var z = new ZLibStream(output, CompressionLevel.Optimal);
            z.Write(new byte[Chunk]);
        });
        Assert.Throws<InvalidDataException>(() => PackedBase.Unpack(new FolderSource(packed), Path.Combine(tmp, "out")));
    }

    [Fact]
    public void EntryLeavingTheFolderIsRefused()
    {
        var packed = Pack(new() { ["../escape"] = [1] }, "nand/cs0.pages", Image());
        Assert.Throws<InvalidDataException>(() => PackedBase.Unpack(new FolderSource(packed), Path.Combine(tmp, "out")));
        Assert.False(File.Exists(Path.Combine(tmp, "escape")));
    }

    [Fact]
    public void ReseedGivesTheBaseANewIdentity()
    {
        var dir = Path.Combine(tmp, "base");
        Directory.CreateDirectory(dir);
        var template = UnitIdentity.SynthesizeIPod("template", "MB528", "LL/A");
        File.WriteAllBytes(Path.Combine(dir, "identity.json"), template.Json());
        var nor = new byte[0x100000];
        Encoding.ASCII.GetBytes("2GMI").CopyTo(nor, 0);
        nor[0x8000] = 0x33;
        File.WriteAllBytes(Path.Combine(dir, "nor.bin"), nor);
        File.WriteAllText(Path.Combine(dir, "device.lock.json"), new JsonObject
        {
            ["board"] = "n72ap",
            ["boot_strategy"] = "iboot",
            ["machine"] = new JsonObject { ["aes-uid"] = "engine", ["wifi-mac"] = template["wifi-mac"] },
            ["outputs"] = new JsonObject { ["nor"] = new JsonObject { ["path"] = "nor.bin", ["sha256"] = "x" } },
        }.ToJsonString());

        PackedBase.Reseed(dir, "ipod2g-8C148-default");

        var want = UnitIdentity.SynthesizeIPod("ipod2g-8C148-default", "MB528", "LL/A");
        Assert.Equal(want.Json(), File.ReadAllBytes(Path.Combine(dir, "identity.json")));
        var reseeded = File.ReadAllBytes(Path.Combine(dir, "nor.bin"));
        Assert.Equal(0x33, reseeded[0x8000]);
        var sysCfg = Encoding.ASCII.GetString(reseeded, 0x4000, 0x100);
        Assert.Contains(want["serial-number"]!, sysCfg);
        Assert.Contains(want["battery-serial"]!, sysCfg);
        var nvram = Encoding.ASCII.GetString(reseeded, 0xFC000, 0x200);
        Assert.Contains("wifiaddr=" + want["wifi-mac"]!.ToUpperInvariant(), nvram);
        Assert.Contains("btaddr=" + want["bt-mac"]!.ToUpperInvariant(), nvram);
        var lockJson = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "device.lock.json")))!;
        Assert.Equal(want["unique-chip-id"], (string?)lockJson["machine"]!["ecid"]);
        Assert.Equal(want["bt-mac"], (string?)lockJson["machine"]!["bt-mac"]);
        Assert.Equal("engine", (string?)lockJson["machine"]!["aes-uid"]);
        Assert.Equal(Sha(reseeded), (string?)lockJson["outputs"]!["nor"]!["sha256"]);
        Assert.Equal(want["udid"], (string?)lockJson["identity"]!["udid"]);
        Assert.Equal(Sha(want.Json()), (string?)lockJson["identity"]!["sha256"]);
        // Sorted keys, as FirmwareKit writes it.
        var keys = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "device.lock.json"))).RootElement
            .EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
    }
}
