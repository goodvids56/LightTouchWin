// The web proxy on one running device, as LightTouchCore's WebProxySetup and GuestServices do it on the Mac: the
// routing file and preferences the device keeps, the CA (WebProxyCA), and, through the guest agent, the guest's route
// to the proxy (an image without the baked PAC gets itproxy's configd setting) and its trust in the CA (ittrust, which
// adds it to the guest's own trust store as a profile install would). Both are idempotent and kept across boots.
// itproxy and ittrust come from the built-in iPod's guest package (Guest/guest.aar's guest-tools/armv6.itpack), the
// Mac's bundledGuestTool; a guest that runs a newer package uses its own copies. Guests without the agent (iPhone OS
// 1.x, which the Mac trusts by editing the stopped device or with a profile over lockdown) aren't set up here.

using System.Text;
using System.Text.Json;
using LightTouch.Library;
using LightTouch.Services;

namespace LightTouch.Proxy;

/// The device's web-proxy choice: through the proxy to the live web (the default), to the Wayback Machine at a date,
/// or straight through.
internal sealed class WebProxySettings
{
    public string Mode { get; set; } = "direct";
    public string ArchiveDate { get; set; } = "20090909";

    public static string Config(DeviceRecord record) => Path.Combine(record.Directory, "web-proxy.conf");
    private static string File(DeviceRecord record) => Path.Combine(record.Directory, "web-proxy.json");

    public static WebProxySettings Load(DeviceRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<WebProxySettings>(System.IO.File.ReadAllText(File(record))) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public static bool ValidDate(string date) =>
        date.Length == 8 && DateTime.TryParseExact(date, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    /// The preferences and the routing the proxy reads per connection (no restart needed).
    public void Save(DeviceRecord record)
    {
        if (Mode == "archive" && !ValidDate(ArchiveDate)) throw new ArgumentException("Choose a valid archive date.");
        System.IO.File.WriteAllText(File(record), JsonSerializer.Serialize(this));
        System.IO.File.WriteAllText(Config(record), Mode == "archive" ? $"archive\n{ArchiveDate}\n" : $"{Mode}\n");
    }
}

internal sealed class WebProxySetup(GuestAgent agent, string config, string guestTools)
{
    public const string ProxyPac = "/usr/share/ltm/proxy.pac";
    /// Where earlier preparations baked it.
    public const string LegacyProxyPac = "/usr/local/share/ltm/proxy.pac";
    public const string PackageBin = "/usr/local/lighttouch/current/bin";

    /// Routes the guest through the proxy and trusts this device's CA in it.
    public async Task Configure()
    {
        var der = (await Task.Run(() => WebProxyCA.Prepare(config))).Certificate.RawData;
        await RouteThroughProxy();
        await TrustCertificate(der);
    }

    /// The image's baked PAC routes every request to the proxy; an image without one gets itproxy's setting on the
    /// Wi-Fi service's HTTP and HTTPS proxies.
    private async Task RouteThroughProxy()
    {
        foreach (var pac in new[] { ProxyPac, LegacyProxyPac })
        {
            if (await agent.Get(pac) != null) return;
        }
        var output = await RunTool("itproxy", "on");
        if (!output.Contains("Proxy enabled", StringComparison.Ordinal))
        {
            throw new DeviceServiceException($"The device didn't accept the proxy setting: {output.Trim()}");
        }
    }

    private async Task TrustCertificate(byte[] der)
    {
        var certificate = $"/tmp/ltm-ca-{Guid.NewGuid():N}.der";
        await agent.Put(certificate, Convert.ToInt32("644", 8), der);
        try
        {
            var output = await RunTool("ittrust", "add", certificate);
            if (!output.Contains("Guest trust add: 0", StringComparison.Ordinal))
            {
                throw new DeviceServiceException($"The device didn't accept the certificate: {output.Trim()}");
            }
        }
        finally
        {
            try { await agent.Unlink(certificate); } catch (Exception e) when (e is GuestAgentException or DeviceServiceException) { }
        }
    }

    /// A guest tool's output: the installed package's copy, else the app's uploaded to /tmp for the one run.
    private async Task<string> RunTool(string name, params string[] arguments)
    {
        try
        {
            return Encoding.UTF8.GetString(await agent.Spawn([$"{PackageBin}/{name}", .. arguments]));
        }
        catch (GuestAgentException error) when (error.Status == GuestAgentException.NotFound)
        {
        }
        var itpack = Path.Combine(guestTools, "guest-tools", "armv6.itpack");
        var tool = File.Exists(itpack) ? GuestPack.Payload(itpack, "n72ap", "7E18", $"bin/{name}") : null;
        if (tool == null)
        {
            throw new DeviceServiceException($"The guest tool {name} is missing. In the library, Add ▸ Import from Light Touch for Mac… again adds it.");
        }
        var executable = $"/tmp/ltm-{name}-{Guid.NewGuid():N}";
        await agent.Put(executable, Convert.ToInt32("755", 8), tool);
        try
        {
            return Encoding.UTF8.GetString(await agent.Spawn([executable, .. arguments]));
        }
        finally
        {
            try { await agent.Unlink(executable); } catch (Exception e) when (e is GuestAgentException or DeviceServiceException) { }
        }
    }
}
