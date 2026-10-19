// argv for one boot, from paths alone: HostRuntime's BootRecipe.swift and PreparedDeviceBoot.configuration, with
// Windows' audio (DirectSound) in place of CoreAudio and no web proxy or USB bridge yet.

namespace LightTouch.Device;

internal sealed record BootConfig(List<string> Argv, Dictionary<string, string> Environment, string Machine);

internal static class BootRecipe
{
    public static string Escape(string value) => value.Replace(",", ",,");

    /// Early iBoot handoff arguments (DeviceOptions.bootArgs); `verbose` adds -v.
    public static string BootArgs(bool verbose) =>
        "amfi_allow_any_signature=1 cs_enforcement_disable=1" + (verbose ? " -v" : "");

    /// The wifi0 user netdev: restrict=on while Setup runs offline; lan=off keeps the guest off the PC's local
    /// networks (DNS and the internet still work).
    public static string WifiNetdev(bool restricted, bool localNetwork) =>
        "user,id=wifi0" + (restricted ? ",restrict=on" : "") + (localNetwork ? "" : ",lan=off");

    /// 5.x Setup phones home: with live internet its Apple-ID page ignores "Skip This Step" for minutes, so a boot
    /// that ends in Setup runs it offline until the person opens the network.
    public static bool SetupPhonesHome(string? iosVersion) =>
        iosVersion != null && Version.TryParse(Pad(iosVersion), out var v) && v >= new Version(5, 0);

    private static string Pad(string version) => version.Contains('.') ? version : version + ".0";

    public static string Options(IReadOnlyDictionary<string, string> machine) =>
        string.Concat(machine.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $",{p.Key}={Escape(p.Value)}"));

    /// The iPod touch 2G (-M iPod-Touch): direct iBoot or the SecureROM chain, the NAND (an image store written in
    /// place, or a base under a page overlay), the base NOR and its private writable copy.
    public static List<string> IPod(PreparedDevice d, string machineName, BootOptions o)
    {
        var machine =
            $"{machineName},boot-args={Escape(BootArgs(o.VerboseBoot))}"
            + $",direct-iboot={Escape(d.Strategy == "bootrom" ? "" : d.BootFile)},direct-llb="
            + $",bootrom={Escape(d.BootRom)},nand={Escape(d.Nand)},nor={Escape(d.BaseNor)}"
            + $",nor-rw={Escape(d.WritableNor ?? "")}" + (d.NandOverlay is { } overlay ? $",nandrw={Escape(overlay)}" : "");
        // No USB bridge on Windows yet: osk types host keys on the guest's own keyboard.
        machine += ",osk=on";
        if (o.Network) machine += ",wifi=on";
        if (d.GidBlobs is { } blobs) machine += $",gid-blobs={Escape(blobs)}";
        machine += Options(d.Machine);
        var argv = new List<string> { "LightTouch", "-M", machine, "-m", "128M", "-display", "none", "-no-shutdown" };
        return argv;
    }

    /// The iPod touch 1G and the original iPhone (-M iPod-Touch-1G / iPhone-2G): the S5L8900 bootrom and iBoot,
    /// the NAND under a page overlay and the NOR as a pflash drive on the writable copy.
    public static List<string> IPod1G(PreparedDevice d, string machineName, bool hasCellular, BootOptions o, string? netdev)
    {
        var machine =
            $"{machineName},bootrom={Escape(d.BootRom)},iboot={Escape(d.BootFile)}"
            + $",nand={Escape(d.Nand)},nand-overlay={Escape(d.Overlay)}"
            + (o.Network ? "" : ",wifi=off") + Options(d.Machine);
        var argv = new List<string>
        {
            "LightTouch", "-M", machine, "-drive", $"if=pflash,format=raw,file={Escape(d.WritableNor ?? "")}",
            "-display", "none", "-no-shutdown",
        };
        // The iPhone's EDGE data goes to its own slirp, cell0, off the LAN like Wi-Fi's.
        if (hasCellular) argv.AddRange(["-netdev", netdev?.Replace("id=wifi0", "id=cell0") ?? "user,id=cell0,lan=off"]);
        return argv;
    }

    /// The S5L8920/S5L8930 boards (-M ipad1, iPod-Touch-4G, iPhone-4, n88, n18): kboot, iBoot or the SecureROM, the
    /// NAND, the die ID, the modem on the radio boards and the USB keyboard where there is a USB host.
    public static List<string> IPad(PreparedDevice d, string machineName, bool hasCellular, bool hasUsbHost, BootOptions o,
        string? netdev)
    {
        var machine = d.Strategy switch
        {
            null or "kboot" => $"{machineName},kboot={Escape(d.BootFile)}"
                + (d.WritableNor is { } nor ? $",nor-rw={Escape(nor)}" : ""),
            "iboot" => $"{machineName},iboot={Escape(d.BootFile)},nor-rw={Escape(d.WritableNor ?? "")}"
                + $",gid-blobs={Escape(d.GidBlobs ?? "")}",
            "bootrom" => $"{machineName},bootrom={Escape(d.BootFile)},nor-rw={Escape(d.WritableNor ?? "")}"
                + $",gid-blobs={Escape(d.GidBlobs ?? "")},development-fuses=off",
            _ => throw new InvalidDataException($"Unknown iPad boot strategy: {d.Strategy}"),
        };
        machine += $",nand={Escape(d.Nand)}" + (d.NandOverlay is { } overlay ? $",nand-overlay={Escape(overlay)}" : "");
        if (d.DieId is { } dieId) machine += $",die-id={Escape(dieId)}";
        if (!o.Network) machine += ",wifi=off";
        if (hasCellular) machine += ",baseband=on";
        machine += Options(d.Machine);
        var argv = new List<string> { "LightTouch", "-M", machine, "-display", "none", "-no-shutdown" };
        // usb-kbd on the always-on EHCI; 20 mA, which 4.x's dock power budget accepts.
        if (hasUsbHost) argv.AddRange(["-device", "usb-kbd,bus=usb-bus.0,max-power=20"]);
        if (hasCellular && netdev != null) argv.AddRange(["-netdev", netdev.Replace("id=wifi0", "id=cell0")]);
        return argv;
    }
}

/// What the person chose for this boot.
internal sealed record BootOptions(bool Network, bool LocalNetwork, bool Audio, bool VerboseBoot, int RefreshHz = 60);
