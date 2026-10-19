using System.Security.Cryptography;
using System.Text;
using LightTouch.Device;
using Xunit;

namespace LightTouch.Tests;

/// The expected values are FirmwareKit's own (Packages/FirmwareKit/Tests/FirmwareKitTests/IdentityTests.swift), so a
/// device made here has the identity the Mac would make from the same seed.
public class IdentityTests
{
    [Fact]
    public void IPodIdentityMatchesFirmwareKit()
    {
        var id = UnitIdentity.SynthesizeIPod("ipod2g-8C148-default", "MC086", "LL/A");
        Assert.Equal("142503116299", id["battery-serial"]);
        Assert.Equal("0129500823c3921495fbea0555169adc11312b63", id["udid"]);
        // The legacy identity (before unique-chip-id) is ipod2g_device.identity's json.dump, byte for byte.
        var json = Encoding.UTF8.GetString(id.Json());
        var legacy = json.Replace($"\n \"unique-chip-id\": \"{id["unique-chip-id"]}\",", "");
        Assert.Equal("cdd30a95a8297be0b8cd336e28311f604e64409d60849323ced09695165c34d0",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacy))).ToLowerInvariant());
    }

    [Fact]
    public void IPodSharesTheIPadGeneratorsSerialMacsAndEcid()
    {
        // UnitIdentity.synthesize(seed: "ipad1-7B500-default")'s recorded fields.
        var id = UnitIdentity.SynthesizeIPod("ipad1-7B500-default", "MB528", "LL/A");
        Assert.Equal("0Y2ZETGRCJB", id["serial-number"]);
        Assert.Equal("02:ea:75:42:31:de", id["wifi-mac"]);
        Assert.Equal("02:ea:75:42:31:df", id["bt-mac"]);
        Assert.Equal("0x6bb6bf76e7", id["unique-chip-id"]);
        Assert.Equal("144707f35769dad502241e7df06a757d8eeea130", id["udid"]);
        Assert.Equal("0x6bb6bf76e7", UnitIdentity.EcidText("ipad1-7B500-default"));
    }

    [Fact]
    public void ImeiIsSyntheticTacAndLuhn()
    {
        Assert.Equal(3, UnitIdentity.Luhn("7992739871"));
        var imei = UnitIdentity.Imei("seed");
        Assert.Equal(15, imei.Length);
        Assert.StartsWith("00000000", imei);
        Assert.Equal(imei[14] - '0', UnitIdentity.Luhn(imei[..14]));
    }

    [Fact]
    public void JsonIsPythonsIndentOne()
    {
        var id = UnitIdentity.SynthesizeIPod("x", "MC086", "LL/A");
        var json = Encoding.UTF8.GetString(id.Json());
        Assert.StartsWith("{\n \"serial-number\": \"", json);
        Assert.EndsWith("\"\n}", json);
        Assert.DoesNotContain("\n\n", json);
    }
}
