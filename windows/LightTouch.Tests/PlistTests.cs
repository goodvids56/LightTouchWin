using System.Text;
using LightTouch.Native;
using Xunit;

namespace LightTouch.Tests;

public class PlistTests
{
    [Fact]
    public void RoundTripsEveryType()
    {
        var value = new Dictionary<string, object>
        {
            ["ApplicationType"] = "User",
            ["Count"] = 42L,
            ["Ratio"] = 0.5,
            ["On"] = true,
            ["Off"] = false,
            ["Data"] = new byte[] { 1, 2, 3 },
            ["List"] = new List<object> { "a", 2L },
            ["Nested"] = new Dictionary<string, object> { ["k"] = "v" },
        };
        var back = Assert.IsType<Dictionary<string, object>>(Plist.Read(Plist.Write(value)));
        Assert.Equal("User", back["ApplicationType"]);
        Assert.Equal(42L, back["Count"]);
        Assert.Equal(0.5, back["Ratio"]);
        Assert.Equal(true, back["On"]);
        Assert.Equal(false, back["Off"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, back["Data"]);
        Assert.Equal(new List<object> { "a", 2L }, back["List"]);
        Assert.Equal("v", ((Dictionary<string, object>)back["Nested"])["k"]);
    }

    [Fact]
    public void ReadsInstallationProxysBrowseResult()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><array><dict>
              <key>CFBundleIdentifier</key><string>com.example.game</string>
              <key>CFBundleDisplayName</key><string>Game</string>
              <key>UIDeviceFamily</key><array><integer>1</integer></array>
            </dict></array></plist>
            """;
        var apps = Assert.IsType<List<object>>(Plist.Read(Encoding.UTF8.GetBytes(xml)));
        var app = Assert.IsType<Dictionary<string, object>>(Assert.Single(apps));
        Assert.Equal("com.example.game", app["CFBundleIdentifier"]);
        Assert.Equal(new List<object> { 1L }, app["UIDeviceFamily"]);
    }
}
