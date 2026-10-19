// The boards Light Touch runs and the facts only the app has about them (HostRuntime's Board.swift): names, the SoC
// family that decides how a prepared base boots, and the boot files. What the hardware is (the -M machine, screen,
// modem, USB host) is the emulator's: Qemu.DeviceInfo, by board.

namespace LightTouch.Device;

internal enum SoC { S5L8900, S5L8720, S5L8920, S5L8930 }

/// A shell image `Width` × `Height` pixels, the upright screen at `Cutout` in it, and the Home button: `HomeDiameter`
/// across, centered `HomeBottomInset` above the bottom edge plus its radius.
internal sealed record BoardArt(string Image, int Width, int Height, System.Drawing.Rectangle Cutout, int HomeDiameter, int HomeBottomInset)
{
    public System.Drawing.PointF HomeCenter => new(Width / 2f, Height - HomeBottomInset - HomeDiameter / 2f);
}

internal sealed record Board(string Id, SoC SoC, string Kind, string ProductType, string MarketingName)
{
    public static readonly Board N45 = new("n45ap", SoC.S5L8900, "iPod touch", "iPod1,1", "iPod touch");
    public static readonly Board M68 = new("m68ap", SoC.S5L8900, "iPhone", "iPhone1,1", "iPhone");
    public static readonly Board N72 = new("n72ap", SoC.S5L8720, "iPod touch", "iPod2,1", "iPod touch (2nd generation)");
    public static readonly Board N18 = new("n18ap", SoC.S5L8920, "iPod touch", "iPod3,1", "iPod touch (3rd generation)");
    public static readonly Board N88 = new("n88ap", SoC.S5L8920, "iPhone", "iPhone2,1", "iPhone 3GS");
    public static readonly Board K48 = new("k48ap", SoC.S5L8930, "iPad", "iPad1,1", "iPad");
    public static readonly Board N81 = new("n81ap", SoC.S5L8930, "iPod touch", "iPod4,1", "iPod touch (4th generation)");
    public static readonly Board N90 = new("n90ap", SoC.S5L8930, "iPhone", "iPhone3,1", "iPhone 4");

    public static readonly IReadOnlyList<Board> All = [N45, M68, N72, N18, N88, K48, N81, N90];

    public static Board? Find(string? id) => All.FirstOrDefault(b => b.Id == id);

    public bool IsPhone => Kind == "iPhone";

    /// The flat device art around the screen (Board.swift's art: the Mac app's shell images, in the app's resources).
    public BoardArt Art => Id switch
    {
        "n45ap" => new("shell-1g", 734, 1311, new(70, 211, 594, 891), 112, 59),
        "m68ap" or "n88ap" => new("shell-iphone2g", 383, 729, new(33, 130, 320, 480), 70, 34),
        "k48ap" => new("ipad-frame", 852, 1108, new(42, 42, 768, 1024), 31, 9),
        "n81ap" => new("shell-4g", 696, 1310, new(54, 213, 590, 886), 119, 49),
        "n90ap" => new("shell-iphone4", 730, 1426, new(48, 236, 640, 960), 140, 52),
        _ => new("shell", 737, 1318, new(74, 213, 594, 891), 122, 54),
    };

    /// The S5L8920/S5L8930 boards: they boot kboot (iBoot too on K48) through BootRecipe.IPad.
    public bool IsKBoot => SoC is SoC.S5L8920 or SoC.S5L8930;

    /// iPhones prepared through the kboot pipeline: their identity carries the modem's IMEI.
    public bool IsKBootPhone => IsKBoot && IsPhone;

    /// The SecureROM image the machine boots, under the device assets.
    public string BootRom => SoC == SoC.S5L8900 ? "bootrom_s5l8900" : "bootrom_240_4";

    /// What the device is called in titles and messages ("iPod").
    public string ShortName => Kind == "iPod touch" ? "iPod" : Kind;

    /// The emulator globals for a panel refreshing at `hz`; none at the devices' 60.
    public IReadOnlyList<string> RefreshGlobals(int hz)
    {
        if (hz == 60) return [];
        var panel = SoC is SoC.S5L8900 or SoC.S5L8720 ? "ipodtouch.lcd" : "s5l8930.display";
        var globals = new List<string> { "-global", $"driver={panel},property=refresh-hz,value={hz}" };
        if (SoC != SoC.S5L8900)
        {
            globals.AddRange(["-global", $"driver=ipodtouch.multitouch,property=report-hz,value={hz}"]);
        }
        return globals;
    }

    /// A prepared base's boot file and the other files its boots need besides nand/, by the lock's boot_strategy.
    public (string Boot, string[] Files) RequiredFiles(string? strategy) => (SoC, strategy) switch
    {
        (SoC.S5L8900, null or "iboot") => ("iBoot.bin", ["nor.bin"]),
        (SoC.S5L8720, null or "iboot") => ("iBoot.bin", ["nor.bin", "gid-blobs.bin"]),
        (SoC.S5L8720, "bootrom") => ("nor.bin", ["gid-blobs.bin"]),
        (_, null or "kboot") when this == K48 => ("kboot.bin", []),
        (_, "iboot") when this == K48 => ("iBoot.bin", ["nor.bin", "gid-blobs.bin"]),
        (_, "bootrom") when this == K48 => ("SecureROM.bin", ["nor.bin", "gid-blobs.bin"]),
        (SoC.S5L8920 or SoC.S5L8930, null or "kboot") => ("kboot.bin", ["nor.bin"]),
        _ => throw new InvalidDataException($"Unknown boot strategy: {strategy ?? "none"}"),
    };
}
