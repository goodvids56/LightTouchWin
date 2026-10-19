using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using LightTouch.Device;
using LightTouch.Native;
using Xunit;

namespace LightTouch.Tests;

/// The argv the Mac's BootRecipe makes for the same base (HostRuntime's BootTests.swift), with Windows' audio.
public sealed class BootTests : IDisposable
{
    // A comma in the path: -M's values escape it as ",,".
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "ltw boot,tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IntPtr> strings = [];

    public BootTests() => Directory.CreateDirectory(tmp);

    public void Dispose()
    {
        foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        Directory.Delete(tmp, recursive: true);
    }

    private Qemu.DeviceInfo Hardware(string machine, string board, bool cellular = false, bool usbHost = false)
    {
        var m = Marshal.StringToCoTaskMemUTF8(machine);
        var b = Marshal.StringToCoTaskMemUTF8(board);
        strings.AddRange([m, b]);
        return new Qemu.DeviceInfo { Machine = m, Board = b, HasCellular = cellular, HasUsbHost = usbHost };
    }

    /// A prepared base of `board` with the files its strategy needs; `imageStore` makes nand/ an image store.
    private string Base(Board board, string? strategy, bool imageStore = false, string? version = "3.1.3")
    {
        var dir = Path.Combine(tmp, "base");
        Directory.CreateDirectory(Path.Combine(dir, "nand"));
        var (boot, also) = board.RequiredFiles(strategy);
        foreach (var name in new[] { boot }.Concat(also).Append("nor.bin"))
        {
            File.WriteAllBytes(Path.Combine(dir, name), Encoding.ASCII.GetBytes(name));
        }
        if (imageStore)
        {
            File.WriteAllText(Path.Combine(dir, "nand", "geometry.json"), "{\"storage_format\": \"nand-xor-blank-v2\"}");
            File.WriteAllBytes(Path.Combine(dir, "nand", "cs0.pages"), new byte[8192]);
        }
        File.WriteAllText(Path.Combine(dir, "identity.json"),
            "{\"seed\": \"s\", \"wifi-mac\": \"02:11:22:33:44:66\", \"bt-mac\": \"02:11:22:33:44:67\", "
            + "\"die-id\": [\"0x123\", \"0x456\"], \"serial-number\": \"X\"}");
        var lockJson = new JsonObject
        {
            ["board"] = board.Id,
            ["product_version"] = version,
            ["machine"] = new JsonObject { ["aes-uid"] = "engine", ["usb-charger"] = true },
        };
        if (strategy != null) lockJson["boot_strategy"] = strategy;
        File.WriteAllText(Path.Combine(dir, DeviceLock.FileName), lockJson.ToJsonString());
        File.WriteAllBytes(Path.Combine(tmp, board.BootRom), [1]);
        return dir;
    }

    private PreparedDevice Prepare(Board board, string baseDir) => PreparedDevice.Prepare(board, baseDir,
        Path.Combine(tmp, "data", "overlay"), Path.Combine(tmp, "data", "nor.bin"), Path.Combine(tmp, board.BootRom));

    private static readonly BootOptions Defaults = new(Network: true, LocalNetwork: false, Audio: false, VerboseBoot: false);

    [Fact]
    public void IPodTouch2GBootsDirectIBootUnderItsOverlay()
    {
        var device = Prepare(Board.N72, Base(Board.N72, "iboot"));
        var config = device.Configuration(Hardware("iPod-Touch", "n72ap"), Defaults, Path.Combine(tmp, "serial.log"));
        var machine = config.Argv[2];
        Assert.Equal(["LightTouch", "-M"], config.Argv.Take(2));
        Assert.StartsWith("iPod-Touch,boot-args=amfi_allow_any_signature=1 cs_enforcement_disable=1,direct-iboot=", machine);
        Assert.Contains(",direct-iboot=" + BootRecipe.Escape(Path.Combine(tmp, "base", "iBoot.bin")) + ",", machine);
        Assert.Contains(",nandrw=" + BootRecipe.Escape(Path.Combine(tmp, "data", "overlay")), machine);
        Assert.Contains(",nor-rw=" + BootRecipe.Escape(Path.Combine(tmp, "data", "nor.bin")), machine);
        Assert.Contains(",gid-blobs=", machine);
        Assert.Contains(",wifi=on", machine);
        Assert.Contains(",aes-uid=engine", machine);
        Assert.Contains(",usb-charger=on", machine);
        Assert.Contains(",wifi-mac=02:11:22:33:44:66", machine);
        // The ECID a legacy identity lacks comes from its seed.
        Assert.Contains(",ecid=" + UnitIdentity.EcidText("s"), machine);
        Assert.Contains(",,", machine);
        Assert.Equal("1", config.Environment["IT_TVOUT_READY"]);
        Assert.Contains("driver=none", config.Argv);
        Assert.Contains("user,id=wifi0,lan=off", config.Argv);
        Assert.Contains("file:" + Path.Combine(tmp, "serial.log"), config.Argv);
        Assert.Equal(Encoding.ASCII.GetBytes("nor.bin"), File.ReadAllBytes(Path.Combine(tmp, "data", "nor.bin")));
    }

    [Fact]
    public void ImageStoreWritesALiveCopyInPlace()
    {
        var device = Prepare(Board.N72, Base(Board.N72, "iboot", imageStore: true));
        Assert.Null(device.NandOverlay);
        Assert.Equal(PreparedDevice.LiveNand(Path.Combine(tmp, "data", "overlay")), device.Nand);
        Assert.True(File.Exists(Path.Combine(device.Nand, "cs0.pages")));
        var machine = device.Configuration(Hardware("iPod-Touch", "n72ap"), Defaults, "serial.log").Argv[2];
        Assert.DoesNotContain("nandrw=", machine);
        Assert.Contains(",nand=" + BootRecipe.Escape(device.Nand) + ",", machine);
    }

    [Fact]
    public void SecureROMChainHasNoDirectIBoot()
    {
        var device = Prepare(Board.N72, Base(Board.N72, "bootrom"));
        var machine = device.Configuration(Hardware("iPod-Touch", "n72ap"), Defaults, "serial.log").Argv[2];
        Assert.Contains(",direct-iboot=,direct-llb=,", machine);
    }

    [Theory]
    [InlineData("kboot", "kboot=")]
    [InlineData("iboot", "iboot=")]
    [InlineData("bootrom", "bootrom=")]
    public void IPadStrategies(string strategy, string option)
    {
        var device = Prepare(Board.K48, Base(Board.K48, strategy, version: "5.1.1"));
        var config = device.Configuration(Hardware("ipad1", "k48ap", usbHost: true), Defaults, "serial.log");
        var machine = config.Argv[2];
        Assert.StartsWith("ipad1," + option, machine);
        Assert.Contains(",die-id=0x123:0x456", machine);
        Assert.Contains(",wifi-mac=02:11:22:33:44:66", machine);
        Assert.Equal(strategy == "bootrom", machine.Contains("development-fuses=off"));
        Assert.Contains("usb-kbd,bus=usb-bus.0,max-power=20", config.Argv);
        // 5.x Setup runs offline until the person opens the network.
        Assert.Contains("user,id=wifi0,restrict=on,lan=off", config.Argv);
        Assert.True(device.ExpectsSetup);
        File.WriteAllText(device.SetupDoneMark, "");
        Assert.False(device.ExpectsSetup);
    }

    [Fact]
    public void RadioBoardsGetModemAndCellNetdev()
    {
        var device = Prepare(Board.N90, Base(Board.N90, null, version: "4.3.3"));
        var config = device.Configuration(Hardware("iPhone-4", "n90ap", cellular: true), Defaults, "serial.log");
        Assert.Contains(",baseband=on", config.Argv[2]);
        Assert.Contains(",imei=" + UnitIdentity.Imei("s"), config.Argv[2]);
        Assert.Contains("user,id=cell0,lan=off", config.Argv);
        Assert.Contains(config.Argv, a => a.StartsWith("ios-baseband.sim-file="));
    }

    [Fact]
    public void IPhoneOS1BootsIBootAndPflashNor()
    {
        var device = Prepare(Board.N45, Base(Board.N45, null, version: "1.1.5"));
        var config = device.Configuration(Hardware("iPod-Touch-1G", "n45ap"), Defaults with { Network = false }, "s.log");
        Assert.StartsWith("iPod-Touch-1G,bootrom=", config.Argv[2]);
        Assert.Contains(",wifi=off", config.Argv[2]);
        Assert.Contains(config.Argv, a => a.StartsWith("if=pflash,format=raw,file="));
        Assert.DoesNotContain(config.Argv, a => a.StartsWith("user,"));
    }

    [Fact]
    public void RefreshReachesThePanel()
    {
        Assert.Equal(["-global", "driver=ipodtouch.lcd,property=refresh-hz,value=120", "-global",
            "driver=ipodtouch.multitouch,property=report-hz,value=120"], Board.N72.RefreshGlobals(120));
        Assert.Equal(["-global", "driver=ipodtouch.lcd,property=refresh-hz,value=120"], Board.M68.RefreshGlobals(120));
        Assert.Empty(Board.K48.RefreshGlobals(60));
    }

    [Fact]
    public void MissingBootInputIsRefusedBeforeStorage()
    {
        var dir = Base(Board.N72, "iboot");
        File.Delete(Path.Combine(dir, "gid-blobs.bin"));
        var error = Assert.Throws<FileNotFoundException>(() => Prepare(Board.N72, dir));
        Assert.EndsWith("gid-blobs.bin", error.FileName);
        Assert.False(Directory.Exists(Path.Combine(tmp, "data")));
    }

    [Fact]
    public void UnknownStrategyIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Prepare(Board.N72, Base(Board.N72, "warp")));
    }

    [Fact]
    public void OlderPageFilesOverAnImageStoreAreRefused()
    {
        var dir = Base(Board.N72, "iboot", imageStore: true);
        Directory.CreateDirectory(Path.Combine(tmp, "data", "overlay", "cs0"));
        Assert.Throws<InvalidDataException>(() => Prepare(Board.N72, dir));
    }
}
