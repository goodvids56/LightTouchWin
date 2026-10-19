using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LightTouch.Library;
using LightTouch.Proxy;
using Xunit;
using WebProxy = LightTouch.Proxy.WebProxy;

namespace LightTouch.Tests;

public class ProxyTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ltw-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void RetiredHostsAreExact()
    {
        Assert.True(WebProxyAdapters.Retired("GData.YouTube.com."));
        Assert.True(WebProxyAdapters.Retired("api.openfeint.com"));
        Assert.False(WebProxyAdapters.Retired("www.youtube.com"));
    }

    [Fact]
    public void ArchiveTextKeepsLinksOnHttp()
    {
        var body = Encoding.ASCII.GetBytes("<a href=\"HTTPS://example.com/\">x</a> https://b");
        Assert.Equal("<a href=\"http://example.com/\">x</a> http://b", Encoding.ASCII.GetString(WebProxyAdapters.HttpLinks(body)));
        Assert.True(WebProxyAdapters.Textual("text/html; charset=utf-8"));
        Assert.False(WebProxyAdapters.Textual("image/png"));
    }

    [Fact]
    public void LocationAnswersEveryAccessPointAtThePosition()
    {
        // version 1, three empty strings, type 1, then ALSLocationRequest { 2: { 1: "AB" } }.
        byte[] inner = [0x0a, 2, (byte)'A', (byte)'B'];
        byte[] request = [0x12, (byte)inner.Length, .. inner];
        byte[] body = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, (byte)request.Length, .. request];
        var answer = WebProxyAdapters.Location("http://iphone-services.apple.com/clls/wloc", "POST", body, (1.5, -2.25, 30));
        Assert.NotNull(answer);
        Assert.Equal(200, answer.Value.Status);
        var reply = answer.Value.Body;
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 1 }, reply[..6]);
        Assert.Equal(reply.Length - 10, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(6)));
        // The entry carries the MAC back.
        Assert.True(reply.AsSpan(10).IndexOf("AB"u8) > 0);
        Assert.Null(WebProxyAdapters.Location("http://example.com/clls/wloc", "GET", [], (0, 0, 30)));
        Assert.Equal(400, WebProxyAdapters.Location("/clls/wloc", "POST", [9], (0, 0, 30))!.Value.Status);
    }

    [Fact]
    public void WeatherSearchesAndForecastsFromOpenMeteo()
    {
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        var search = Encoding.UTF8.GetBytes("<request><query type=\"getlocationid\"><phrase>Paris</phrase></query></request>");
        var found = WebProxyAdapters.Weather("http://iphone-wu.apple.com/dgw?apptype=weather", "POST", search, (host, path, query) =>
        {
            Assert.Equal("geocoding-api.open-meteo.com", host);
            Assert.Equal("Paris", query["name"]);
            return Json("""{"results": [{"latitude": 48.85, "longitude": 2.35, "name": "Paris", "admin1": "Île-de-France", "country_code": "FR", "country": "France"}]}""");
        });
        Assert.Equal(200, found!.Value.Status);
        var item = XDocument.Parse(Encoding.UTF8.GetString(found.Value.Body)).Descendants("item").Single();
        Assert.Equal("Paris", item.Element("city")!.Value);
        var id = item.Element("id")!.Value;
        Assert.StartsWith("ltm:", id);

        var days = string.Join(',', Enumerable.Range(0, 6).Select(d => $"\"2026-10-{19 + d:00}\""));
        var forecast = Encoding.UTF8.GetBytes($"<request><query type=\"getforecastbylocationid\"><list><id>{id}</id></list><unit>c</unit></query></request>");
        var answer = WebProxyAdapters.Weather("http://iphone-wu.apple.com/dgw?apptype=weather", "POST", forecast, (host, _, query) =>
        {
            Assert.Equal("api.open-meteo.com", host);
            Assert.Equal("48.85", query["latitude"]);
            return Json($$$"""
                {"utc_offset_seconds": 7200, "current": {"time": "2026-10-19T14:00", "temperature_2m": 17.6, "weather_code": 2, "is_day": 1},
                 "daily": {"time": [{{{days}}}], "temperature_2m_max": [18, 19, 20, 21, 22, 23], "temperature_2m_min": [8, 9, 10, 11, 12, 13],
                           "weather_code": [0, 1, 2, 3, 61, 95], "sunrise": ["2026-10-19T08:10", "", "", "", "", ""],
                           "sunset": ["2026-10-19T19:05", "", "", "", "", ""]}}
                """);
        });
        Assert.Equal(200, answer!.Value.Status);
        var weather = XDocument.Parse(Encoding.UTF8.GetString(answer.Value.Body)).Descendants("item").Single();
        Assert.Equal("18", weather.Element("condition")!.Attribute("temp")!.Value);
        Assert.Equal("30", weather.Element("condition")!.Attribute("code")!.Value);
        Assert.Equal("08:10", weather.Element("astronomy")!.Attribute("sunrise")!.Value);
        Assert.Equal(6, weather.Elements("forecast").Count());
        Assert.Equal("2", weather.Elements("forecast").First().Attribute("dayofweek")!.Value); // 2026-10-19 is a Monday

        var unknown = Encoding.UTF8.GetBytes("<request><query type=\"getforecastbylocationid\"><list><id>FRXX0076</id></list><unit>c</unit></query></request>");
        Assert.Equal(422, WebProxyAdapters.Weather("http://iphone-wu.apple.com/dgw?apptype=weather", "POST", unknown, (_, _, _) => null)!.Value.Status);
        Assert.Null(WebProxyAdapters.Weather("http://example.com/dgw?apptype=weather", "POST", search, (_, _, _) => null));
    }

    [Fact]
    public void TheCaSignsLeavesForHostsAndAddresses()
    {
        var directory = TempDirectory();
        try
        {
            var config = Path.Combine(directory, "web-proxy.conf");
            var ca = WebProxyCA.Prepare(config);
            Assert.True(File.Exists(config + ".ca.der"));
            Assert.Equal("1.2.840.113549.1.1.5", ca.Certificate.SignatureAlgorithm.Value); // sha1WithRSAEncryption
            Assert.Equal(ca.Certificate.Thumbprint, WebProxyCA.Prepare(config).Certificate.Thumbprint); // kept, not remade
            using var key = RSA.Create(2048);
            foreach (var host in new[] { "www.apple.com", "10.0.2.2" })
            {
                var (pem, _) = ca.Leaf(host, key);
                using var leaf = X509Certificate2.CreateFromPem(pem);
                Assert.Equal(ca.Certificate.Subject, leaf.Issuer);
                Assert.Equal(new DateTime(2000, 1, 1), leaf.NotBefore.ToUniversalTime().Date);
                var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
                Assert.Contains(host, IPAddress.TryParse(host, out _) ? names.EnumerateIPAddresses().Select(a => a.ToString()) : names.EnumerateDnsNames());
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca.Certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                Assert.True(chain.Build(leaf), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
            }
            Assert.Throws<CryptographicException>(() => ca.Leaf("bad host", key));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GuestPackFindsTheBoardsPayload()
    {
        var directory = TempDirectory();
        try
        {
            var manifest = Encoding.UTF8.GetBytes("""{"requires": {"boards": ["n72ap"], "builds": ["7C145", "7E18"]}}""");
            var other = Encoding.UTF8.GetBytes("""{"requires": {"boards": ["n72ap"], "builds": ["8*"]}}""");
            var entries = new (string Name, byte[] Data)[]
            {
                ("n72-ios4/manifest.json", other), ("n72-ios4/bin/ittrust", "four"u8.ToArray()),
                ("n72-ios3/manifest.json", manifest), ("n72-ios3/bin/ittrust", "three"u8.ToArray()),
            };
            var index = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { entries = entries.Select(e => new { name = e.Name, size = e.Data.Length }) }));
            using var file = new MemoryStream();
            file.Write("ITPACK01"u8);
            file.Write(BitConverter.GetBytes(index.Length));
            file.Write(index);
            using (var zlib = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true))
            {
                foreach (var e in entries) zlib.Write(e.Data);
            }
            var path = Path.Combine(directory, "armv6.itpack");
            File.WriteAllBytes(path, file.ToArray());
            Assert.Equal("three", Encoding.ASCII.GetString(GuestPack.Payload(path, "n72ap", "7E18", "bin/ittrust")!));
            Assert.Equal("four", Encoding.ASCII.GetString(GuestPack.Payload(path, "n72ap", "8C148", "bin/ittrust")!));
            Assert.Null(GuestPack.Payload(path, "n18ap", "7E18", "bin/ittrust"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// The built-in iPod's real package (LTW_GUEST_AAR, the Mac's guest.aar; lzfse on the library path).
    [Fact]
    public void RealGuestPackageHasTheProxyTools()
    {
        var aar = Environment.GetEnvironmentVariable("LTW_GUEST_AAR");
        if (aar == null) return;
        var directory = TempDirectory();
        try
        {
            using (var stream = File.OpenRead(aar)) GuestArchive.Extract(stream, Path.Combine(directory, "g"), p => p.StartsWith("guest-tools", StringComparison.Ordinal));
            var itpack = Path.Combine(directory, "g", "guest-tools", "armv6.itpack");
            Assert.NotNull(GuestPack.Payload(itpack, "n72ap", "7E18", "bin/itproxy"));
            Assert.NotNull(GuestPack.Payload(itpack, "n72ap", "7E18", "bin/ittrust"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string Exchange(string proxyAddress, string request)
    {
        var colon = proxyAddress.LastIndexOf(':');
        using var client = new TcpClient(proxyAddress[..colon], int.Parse(proxyAddress[(colon + 1)..]));
        var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// Dated browsing against a stand-in archive: its redirect followed, the original's absolute redirect re-dated,
    /// https links kept on http, a day's cache.
    [Fact]
    public async Task ArchiveModeReplaysTheClosestCapture()
    {
        var directory = TempDirectory();
        var archive = new TcpListener(IPAddress.Loopback, 0);
        archive.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)archive.LocalEndpoint).Port}";
        var requests = new List<string>();
        var server = Task.Run(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var socket = archive.AcceptSocket();
                var buffer = new byte[8192];
                var line = Encoding.ASCII.GetString(buffer, 0, socket.Receive(buffer)).Split("\r\n")[0];
                lock (requests) requests.Add(line);
                var reply = i switch
                {
                    0 => $"HTTP/1.1 302 Found\r\nLocation: {origin}/web/20090908120000id_/http://old.example/\r\nContent-Length: 0\r\n\r\n",
                    1 => "HTTP/1.1 301 Moved\r\nLocation: http://old.example/home\r\nContent-Length: 0\r\n\r\n",
                    _ => "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 36\r\nSet-Cookie: x=1\r\n\r\n<a href=\"https://old.example/\">a</a>",
                };
                socket.Send(Encoding.ASCII.GetBytes(reply));
            }
        });
        try
        {
            var config = Path.Combine(directory, "web-proxy.conf");
            File.WriteAllText(config, "archive\n20090909\n");
            using var proxy = new WebProxy(config, origin);
            proxy.Start();
            var reply = Exchange(proxy.Address, "GET http://old.example/ HTTP/1.1\r\nCookie: secret=1\r\n\r\n");
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reply.StartsWith("HTTP/1.0 200 Archive response\r\n") && reply.EndsWith("<a href=\"http://old.example/\">a</a>"), reply);
            Assert.EndsWith("<a href=\"http://old.example/\">a</a>", reply);
            Assert.Contains("Content-Length: 35\r\n", reply);
            Assert.DoesNotContain("Set-Cookie", reply);
            Assert.Equal("GET /web/20090909id_/http://old.example/ HTTP/1.1", requests[0]);
            Assert.Equal("GET /web/20090909id_/http://old.example/home HTTP/1.1", requests[2]);
            // The second visit is the day's cache: the stand-in archive has stopped answering.
            Assert.Equal(reply, Exchange(proxy.Address, "GET http://old.example/ HTTP/1.0\r\n\r\n"));
            Assert.StartsWith("HTTP/1.0 405 ", Exchange(proxy.Address, "POST http://old.example/ HTTP/1.0\r\nContent-Length: 0\r\n\r\n"));
        }
        finally
        {
            archive.Stop();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DirectModeForwardsHttpAndRepliesAsHttp10()
    {
        var directory = TempDirectory();
        var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        string? seen = null;
        var server = Task.Run(() =>
        {
            using var socket = origin.AcceptSocket();
            var buffer = new byte[8192];
            var n = socket.Receive(buffer);
            seen = Encoding.ASCII.GetString(buffer, 0, n);
            socket.Send(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\n\r\nhello"));
        });
        try
        {
            var config = Path.Combine(directory, "web-proxy.conf");
            File.WriteAllText(config, "direct\n");
            using var proxy = new WebProxy(config);
            proxy.Start();
            Assert.Equal($",guestfwd=tcp:10.0.2.100:3128-unix:{proxy.Address}", proxy.GuestForward);
            var reply = Exchange(proxy.Address, $"GET http://127.0.0.1:{port}/page HTTP/1.1\r\nHost: x\r\nProxy-Connection: keep-alive\r\nAccept-Encoding: gzip\r\n\r\n");
            Assert.StartsWith("HTTP/1.0 200 OK\r\n", reply);
            Assert.Contains("Connection: close\r\n", reply);
            Assert.Contains("Set-Cookie: a=1\r\n", reply);
            Assert.Contains("Set-Cookie: b=2\r\n", reply);
            Assert.EndsWith("\r\n\r\nhello", reply);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("GET /page HTTP/1.1", seen);
            Assert.DoesNotContain("Proxy-Connection", seen);

            Assert.StartsWith("HTTP/1.0 410 ", Exchange(proxy.Address, "GET http://gdata.youtube.com/feeds HTTP/1.0\r\n\r\n"));
            Assert.StartsWith("HTTP/1.0 400 ", Exchange(proxy.Address, "GET /relative HTTP/1.0\r\n\r\n"));
            File.WriteAllText(config, "archive\n2009\n");
            Assert.StartsWith("HTTP/1.0 503 Invalid archive date", Exchange(proxy.Address, "GET http://example.com/ HTTP/1.0\r\n\r\n"));
            // Offline: closed without a byte (unread, so the close can arrive as a reset).
            proxy.Offline = true;
            try { Assert.Equal("", Exchange(proxy.Address, "GET http://example.com/ HTTP/1.0\r\n\r\n")); }
            catch (IOException) { }
        }
        finally
        {
            origin.Stop();
            Directory.Delete(directory, true);
        }
    }
}
