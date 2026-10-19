// The web proxy for one device, in its process (LightTouchDevice's WebProxy.swift on the Mac). The guest's PAC or
// itproxy setting sends every request to 10.0.2.100:3128; that guestfwd (windows/patches/qemu-ios/0002 and libslirp/
// 0001) connects to this listener on loopback, once per guest connection.
//
// Routing is CONFIG (web-proxy.conf), read per connection: `direct`, `off`, or `archive` and a yyyyMMdd date.
// Upstream is HttpClient (HTTP/2, Windows' trust store and proxy settings); the guest side speaks what a 2009-2010
// client does: HTTP/1.0 replies with Connection: close, and TLS 1.0 (guesttls.dll, OpenSSL) terminated with a leaf
// per host from the device's own CA (WebProxyCA).
//
// direct   HTTP and HTTPS (CONNECT, terminated); the adapters (WebProxyAdapters): Weather, Wi-Fi location, 410 for
//          retired hosts.
// archive  GET/HEAD replayed from the Wayback Machine's closest capture, redirects resolved here, text links kept on
//          http, no guest cookies or credentials sent, one fetch a second, a Retry-After cooldown, a day's cache.
// off      Pass-through while the guest switches its proxy off: CONNECT is a raw tunnel (the origin's own certificate),
//          HTTP forwarded.
// The location answer (origin-form /clls/wloc) is served in every mode. Unlike the Mac's URLSession, nothing in direct
// mode is cached here.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightTouch.Proxy;

internal sealed class WebProxy : IDisposable
{
    private const int HeadMax = 65536, BodyMax = 8 << 20, ArchiveBodyMax = 32 << 20;
    private static readonly bool Trace = Environment.GetEnvironmentVariable("LTM_WEB_PROXY_TRACE") != null;

    private readonly string config;
    private readonly string archiveOrigin;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient client;
    private readonly object leafLock = new();
    private RSA? leafKey;
    private readonly Dictionary<string, ((string Certificate, string Key) Leaf, DateTime Made)> leaves = [];
    private readonly SemaphoreSlim archiveGate = new(1, 1);
    private DateTime archiveNext = DateTime.MinValue, cooldownUntil = DateTime.MinValue;

    /// Follows wifi0's slirp restrict (Setup runs offline): the image's PAC routes every public host here, and slirp's
    /// restrict lets guestfwd traffic through, so while this is set each connection is closed without a byte. Any
    /// reply, even a 503, answers iOS's captive-network probe and Setup shows a "Log In" sheet; a dead proxy reads as
    /// no internet, as a real unit offline.
    public bool Offline { get => offline; set => offline = value; }
    private volatile bool offline;
    /// The device's local-network setting, as slirp's lan= has it: off, a destination on the PC's local networks gets
    /// a 502.
    public bool LocalNetwork { get => localNetwork; set => localNetwork = value; }
    private volatile bool localNetwork = true;

    public WebProxy(string config, string archiveOrigin = "https://web.archive.org")
    {
        this.config = config;
        this.archiveOrigin = archiveOrigin;
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// The loopback address the guestfwd connects to ("127.0.0.1:PORT").
    public string Address => $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

    /// The netdev option that sends the guest's 10.0.2.100:3128 here.
    public string GuestForward => $",guestfwd=tcp:10.0.2.100:3128-unix:{Address}";

    /// Serves on a thread of its own; one thread per connection.
    public void Start()
    {
        listener.Start(64);
        var accepting = new Thread(() =>
        {
            while (true)
            {
                Socket socket;
                try { socket = listener.AcceptSocket(); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                new Thread(() => Serve(socket)) { IsBackground = true, Name = "web-proxy connection" }.Start();
            }
        }) { IsBackground = true, Name = "web-proxy" };
        accepting.Start();
    }

    public void Dispose()
    {
        listener.Stop();
        client.Dispose();
    }

    // MARK: - One connection

    /// A reply that ends the connection: the status and a one-line explanation.
    private sealed class Reply(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private abstract record Mode
    {
        public sealed record Off : Mode;
        public sealed record Direct : Mode;
        public sealed record Archive(string Date) : Mode;
    }

    /// Fails closed: a missing or unreadable file is "disabled", anything unknown invalid.
    private Mode ReadMode()
    {
        string text;
        try { text = File.ReadAllText(config); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Reply(503, "Proxy is disabled"); }
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.FirstOrDefault() switch
        {
            "direct" => new Mode.Direct(),
            "off" => new Mode.Off(),
            "archive" when words.Length == 2 && words[1].Length == 8 && words[1].All(char.IsAsciiDigit) => new Mode.Archive(words[1]),
            "archive" => throw new Reply(503, "Invalid archive date"),
            _ => throw new Reply(503, "Invalid proxy configuration"),
        };
    }

    private void Serve(Socket socket)
    {
        socket.ReceiveTimeout = 60_000;
        socket.SendTimeout = 30_000;
        using var guest = new GuestConnection(socket);
        if (Offline) return;
        try
        {
            Handle(guest, ReadMode());
        }
        catch (Reply reply)
        {
            guest.Write($"HTTP/1.0 {reply.Status} {reply.Message}\r\nConnection: close\r\nContent-Type: text/plain\r\n\r\n{reply.Message}\n");
        }
        catch (Exception error) when (error is IOException or SocketException or HttpRequestException or TaskCanceledException
            or CryptographicException or InvalidOperationException)
        {
            if (Trace) Console.Error.WriteLine($"web-proxy: {error.Message}");
            guest.Write("HTTP/1.0 502 Destination unavailable\r\nConnection: close\r\nContent-Type: text/plain\r\n\r\nDestination unavailable\n");
        }
    }

    private void Handle(GuestConnection guest, Mode mode)
    {
        string? tunnel = null;
        while (true)
        {
            var (method, target, headers) = ReadHead(guest);
            if (Trace) Console.Error.WriteLine($"web-proxy: {method} {(tunnel != null ? "https://" + tunnel : "")}{target}");
            if (method == "CONNECT")
            {
                if (tunnel != null) throw new Reply(400, "Nested TLS tunnels are unsupported");
                if (target.Length >= 300) throw new Reply(400, "Tunnel destination too long");
                var colon = target.LastIndexOf(':');
                if (colon <= 0) throw new Reply(400, "Invalid tunnel destination");
                if (!int.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                {
                    throw new Reply(400, "Invalid tunnel port");
                }
                var host = target[..colon];
                if (host.Length > 2 && host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
                if (mode is Mode.Direct && WebProxyAdapters.Retired(host)) throw new Reply(410, "This online service has been retired");
                if (!LocalNetwork && IsLocalNetwork(host)) throw new Reply(502, "Local network access is off");
                if (mode is not Mode.Off && File.Exists(config + ".ca.pem"))
                {
                    (string Certificate, string Key) leaf;
                    try { leaf = Leaf(host); }
                    catch (Exception e) when (e is CryptographicException or IOException) { throw new Reply(503, "Local TLS certificate unavailable"); }
                    if (!guest.Write("HTTP/1.0 200 Connection established\r\n\r\n") || !guest.StartTls(leaf.Certificate, leaf.Key)) return;
                    tunnel = target;
                    continue;
                }
                if (mode is Mode.Archive) throw new Reply(405, "Dated HTTPS needs the local TLS bridge");
                using var remote = Connect(host, port) ?? throw new Reply(502, "Destination unavailable");
                if (guest.Write("HTTP/1.0 200 Connection established\r\n\r\n")) Relay(guest, remote);
                return;
            }
            // iOS 3.2's locationd, pointed straight at the proxy's address, sends an origin-form request; it is answered
            // in every mode (location is not browsing, and the guest's preference is baked).
            var localLocation = tunnel == null && target == "/clls/wloc";
            var url = target;
            if (mode is Mode.Archive && !localLocation && method is not ("GET" or "HEAD"))
            {
                throw new Reply(405, "Archive browsing supports HTTP GET and HEAD");
            }
            if (tunnel != null)
            {
                if (!target.StartsWith('/')) throw new Reply(400, "TLS request needs an origin-form path");
                url = $"https://{tunnel}{target}";
            }
            if (!method.All(c => c is >= 'A' and <= 'Z')) throw new Reply(400, "Invalid method");
            if (localLocation) url = "http://iphone-services.apple.com/clls/wloc";
            if (tunnel == null && !url.StartsWith("http://", StringComparison.Ordinal)) throw new Reply(400, "An absolute HTTP URL is required");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) throw new Reply(400, "Invalid URL");
            if (mode is Mode.Direct && WebProxyAdapters.Retired(uri.Host)) throw new Reply(410, "This online service has been retired");
            if (!localLocation && !LocalNetwork && IsLocalNetwork(uri.Host)) throw new Reply(502, "Local network access is off");
            if (mode is Mode.Archive archive && !localLocation)
            {
                if (uri.UserInfo.Length > 0) throw new Reply(400, "Archive URLs cannot contain credentials");
                guest.Write(Archived(url, archive.Date, method == "HEAD"));
                return;
            }
            var forwarded = new List<(string, string)>();
            int? length = null;
            foreach (var (name, value) in headers)
            {
                switch (name.ToLowerInvariant())
                {
                    case "transfer-encoding": throw new Reply(501, "Chunked request bodies are unsupported");
                    case "expect": throw new Reply(417, "Expect is unsupported");
                    case "content-length":
                        if (length != null || value.Length == 0 || !value.All(char.IsAsciiDigit)) throw new Reply(400, "Invalid content length");
                        if (!int.TryParse(value, out var n) || n > BodyMax) throw new Reply(413, "Invalid or oversized body");
                        length = n;
                        break;
                    // Hop-by-hop, Host (the URL's), and Accept-Encoding: HttpClient asks for and decodes gzip/deflate/br itself.
                    case "connection" or "proxy-connection" or "keep-alive" or "te" or "trailer" or "upgrade" or "proxy-authorization"
                        or "proxy-authenticate" or "host" or "accept-encoding":
                        break;
                    default:
                        forwarded.Add((name, value));
                        break;
                }
            }
            var body = new byte[length ?? 0];
            for (var read = 0; read < body.Length;)
            {
                var n = guest.Read(body, read, body.Length - read);
                if (n == 0) throw new Reply(400, "Incomplete body");
                read += n;
            }
            if ((localLocation || mode is Mode.Direct)
                && WebProxyAdapters.Location(url, method, body, WebProxyAdapters.Position(config + ".location")) is (int status, byte[] answer))
            {
                if (status != 200) throw new Reply(status, "Invalid location request");
                guest.Write(Head(200, "OK", ["Content-Type: application/x-protobuf", $"Content-Length: {answer.Length}"]).Concat(answer).ToArray());
                return;
            }
            if (mode is Mode.Direct && WebProxyAdapters.Weather(url, method, body, FetchJson) is (int weatherStatus, byte[] weather))
            {
                if (weatherStatus != 200)
                {
                    throw new Reply(weatherStatus, weatherStatus == 422 ? "Please remove this old Weather city and add it again" : "Weather service unavailable");
                }
                guest.Write(Head(200, "OK", ["Content-Type: text/xml; charset=utf-8", $"Content-Length: {weather.Length}"]).Concat(weather).ToArray());
                return;
            }
            var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (length != null) request.Content = new ByteArrayContent(body);
            foreach (var (name, value) in forwarded)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            Stream(request, guest);
            return;
        }
    }

    /// Request line and headers, a byte at a time (nothing past the blank line is consumed: TLS may follow a CONNECT).
    private static (string Method, string Target, List<(string, string)> Headers) ReadHead(GuestConnection guest)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == 13 && head[^3] == 10 && head[^2] == 13 && head[^1] == 10))
        {
            if (head.Count >= HeadMax - 1) throw new Reply(431, "Request headers too large");
            if (guest.Read(one, 0, 1) == 0) throw new Reply(400, "Incomplete request");
            if (one[0] == 0) throw new Reply(400, "Invalid request byte");
            head.Add(one[0]);
        }
        var lines = Encoding.UTF8.GetString(head.ToArray(), 0, head.Count - 4).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length is 0 or >= 32 || parts[1].Length >= 16384 || parts[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {
            throw new Reply(400, "Invalid request");
        }
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || line.StartsWith(' ') || line.StartsWith('\t')) throw new Reply(400, "Invalid header");
            headers.Add((line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
        }
        return (parts[0], parts[1], headers);
    }

    private static byte[] Head(int status, string reason, IEnumerable<string> lines) =>
        Encoding.UTF8.GetBytes($"HTTP/1.0 {status} {reason}\r\n" + string.Concat(lines.Select(l => l + "\r\n")) + "Connection: close\r\n\r\n");

    // MARK: - Upstream (HttpClient)

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "proxy-connection", "keep-alive", "transfer-encoding", "te", "trailer", "upgrade", "proxy-authorization",
        "proxy-authenticate",
    };

    /// The response's header lines for the guest: as they were, less the hop-by-hop ones and what HttpClient consumed
    /// (a Content-Encoding it decoded, with that length), one line per Set-Cookie.
    private static List<string> HeaderLines(HttpResponseMessage response, ICollection<string>? dropping = null)
    {
        var lines = new List<string>();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (HopByHop.Contains(name) || dropping?.Contains(name.ToLowerInvariant()) == true) continue;
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                lines.AddRange(values.Select(v => $"{name}: {v}"));
                continue;
            }
            lines.Add($"{name}: {string.Join(", ", values)}");
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content", 206 => "Partial Content", 301 => "Moved Permanently", 302 => "Found",
        303 => "See Other", 304 => "Not Modified", 307 => "Temporary Redirect", 308 => "Permanent Redirect", 400 => "Bad Request",
        401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 410 => "Gone",
        429 => "Too Many Requests", 500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable",
        _ => "Status",
    };

    /// The origin's response, streamed as it arrives.
    private void Stream(HttpRequestMessage request, GuestConnection guest)
    {
        HttpResponseMessage response;
        using (var headers = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new Reply(502, "Destination unavailable");
            }
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (!guest.Write(Head(status, Reason(status), HeaderLines(response)))) return;
            using var body = response.Content.ReadAsStream();
            var buffer = new byte[65536];
            int n;
            while ((n = body.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (!guest.Write(buffer.AsSpan(0, n))) return;
            }
        }
    }

    /// A whole response, at most `limit` bytes (null past it or on an error).
    private (HttpResponseMessage Response, byte[] Body)? Fetch(HttpRequestMessage request, int limit, TimeSpan timeout)
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).GetAwaiter().GetResult();
            using var body = response.Content.ReadAsStream(cancel.Token);
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int n;
            while ((n = body.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > limit)
                {
                    response.Dispose();
                    return null;
                }
            }
            return (response, output.ToArray());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private JsonElement? FetchJson(string host, string path, Dictionary<string, string> query)
    {
        var url = $"https://{host}{path}?" + string.Join('&', query.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "LightTouch/1.0 (Weather)");
        if (Fetch(request, 1 << 20, TimeSpan.FromSeconds(20)) is not ({ } response, { } body)) return null;
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK) return null;
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    // MARK: - Archive

    /// The capture closest to `date` of `target`: the archive's own and the original site's redirects followed here
    /// (an http-to-https one stays in dated browsing), no guest header sent; a day's cache beside CONFIG.
    private byte[] Archived(string target, string date, bool head)
    {
        var archiveUrl = $"{archiveOrigin}/web/{date}id_/{target}";
        var cacheDirectory = config + ".cache";
        var cached = Path.Combine(cacheDirectory, "archive-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(archiveUrl))).ToLowerInvariant());
        if (!head && File.Exists(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < TimeSpan.FromDays(1))
        {
            try { return File.ReadAllBytes(cached); } catch (IOException) { }
        }
        archiveGate.Wait();
        try
        {
            var url = archiveUrl;
            for (var hop = 0; hop < 8; hop++)
            {
                var wait = cooldownUntil - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) return ArchiveLimited((int)Math.Ceiling(wait.TotalSeconds));
                var pause = archiveNext - DateTime.UtcNow;
                if (pause > TimeSpan.Zero) Thread.Sleep(pause);
                archiveNext = DateTime.UtcNow.AddSeconds(1);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address)) throw new Reply(502, "The archive returned an invalid address");
                using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, address);
                request.Headers.TryAddWithoutValidation("User-Agent", "LightTouch/1.0 (archive replay)");
                if (Fetch(request, ArchiveBodyMax, TimeSpan.FromSeconds(60)) is not ({ } response, { } fetched))
                {
                    throw new Reply(502, "The archive could not be reached or its response was too large");
                }
                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status is 429 or 503)
                    {
                        var seconds = RetryAfter(response.Headers.RetryAfter);
                        cooldownUntil = DateTime.UtcNow.AddSeconds(seconds);
                        return ArchiveLimited(seconds);
                    }
                    var location = response.Headers.Location?.OriginalString ?? "";
                    if (status is >= 300 and < 400 && location.Length > 0)
                    {
                        if (location.StartsWith(archiveOrigin + "/", StringComparison.Ordinal))
                        {
                            url = location;
                            continue;
                        }
                        if (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal))
                        {
                            url = archiveOrigin + location;
                            continue;
                        }
                        if (location.StartsWith("http://", StringComparison.Ordinal) || location.StartsWith("https://", StringComparison.Ordinal))
                        {
                            url = $"{archiveOrigin}/web/{date}id_/{location}";
                            continue;
                        }
                    }
                    var body = WebProxyAdapters.Textual(response.Content.Headers.ContentType?.ToString()) ? WebProxyAdapters.HttpLinks(fetched) : fetched;
                    var lines = HeaderLines(response, ["content-length", "content-encoding", "etag", "content-md5", "set-cookie"]);
                    if (!head) lines.Add($"Content-Length: {body.Length}");
                    var reply = Head(status, "Archive response", lines).Concat(head ? [] : body).ToArray();
                    if (!head && status == 200)
                    {
                        try
                        {
                            Directory.CreateDirectory(cacheDirectory);
                            File.WriteAllBytes(cached, reply);
                        }
                        catch (IOException) { }
                    }
                    return reply;
                }
            }
            throw new Reply(502, "The archive returned too many redirects");
        }
        finally
        {
            archiveGate.Release();
        }
    }

    private static int RetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? value)
    {
        var seconds = value?.Delta is { } delta ? (int)delta.TotalSeconds
            : value?.Date is { } when ? (int)(when - DateTimeOffset.UtcNow).TotalSeconds
            : 60;
        return seconds is > 0 and < 31_536_000 ? seconds : 60;
    }

    private static byte[] ArchiveLimited(int seconds) =>
        Head(429, "Too Many Requests", ["Content-Type: text/html; charset=utf-8", $"Retry-After: {seconds}", "Cache-Control: no-store"])
            .Concat(Encoding.UTF8.GetBytes("<html><head><title>Archive temporarily busy</title></head><body><h2>Wayback Machine is temporarily limiting requests</h2>"
                + $"<p>Light Touch has paused archive requests. Please wait {seconds} seconds, then reload this page.</p></body></html>")).ToArray();

    // MARK: - TLS toward the guest

    /// A week-long leaf for `host`, one key for them all; made again after six days.
    private (string Certificate, string Key) Leaf(string host)
    {
        lock (leafLock)
        {
            if (leaves.TryGetValue(host, out var cached) && DateTime.UtcNow - cached.Made < TimeSpan.FromDays(6)) return cached.Leaf;
            leafKey ??= RSA.Create(2048);
            var leaf = WebProxyCA.Load(config).Leaf(host, leafKey);
            leaves[host] = (leaf, DateTime.UtcNow);
            return leaf;
        }
    }

    /// A .local name, or a host any of whose addresses is private, link-local or multicast: the ranges slirp's lan=off
    /// refuses.
    private static bool IsLocalNetwork(string host)
    {
        var name = host.ToLowerInvariant();
        if (name.EndsWith(".local", StringComparison.Ordinal) || name.EndsWith(".local.", StringComparison.Ordinal)) return true;
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var literal) ? [literal] : Dns.GetHostAddresses(host); }
        catch (SocketException) { return false; }
        foreach (var address in addresses)
        {
            var b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var ip = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
                if (ip >> 24 == 10 || ip >> 20 == 0xac1 || ip >> 16 == 0xc0a8 || ip >> 16 == 0xa9fe || ip >> 28 == 0xe || ip == uint.MaxValue) return true;
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if ((b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) || b[0] == 0xff) return true;
            }
        }
        return false;
    }

    // MARK: - Raw tunnel (off, or no CA)

    private static Socket? Connect(string host, int port)
    {
        try
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { SendTimeout = 10_000 };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            socket.ConnectAsync(host, port, cancel.Token).AsTask().GetAwaiter().GetResult();
            return socket;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// Both directions until the remote closes; the guest's end of input is passed on as a half-close.
    private static void Relay(GuestConnection guest, Socket remote)
    {
        var upstream = new Thread(() =>
        {
            var buffer = new byte[16384];
            try
            {
                int n;
                while ((n = guest.Read(buffer, 0, buffer.Length)) > 0) remote.Send(buffer, 0, n, SocketFlags.None);
                remote.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }) { IsBackground = true };
        upstream.Start();
        var down = new byte[16384];
        remote.ReceiveTimeout = 60_000;
        try
        {
            int n;
            while ((n = remote.Receive(down)) > 0)
            {
                if (!guest.Write(down.AsSpan(0, n))) break;
            }
        }
        catch (SocketException) { }
    }
}

/// The guest's end: plain, or TLS 1.0+ once a CONNECT is accepted (guesttls.dll over the socket).
internal sealed partial class GuestConnection(Socket socket) : IDisposable
{
    private IntPtr tls;

    [LibraryImport("guesttls.dll", EntryPoint = "gtls_accept", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr TlsAccept(IntPtr socket, string certificate, string key);

    [LibraryImport("guesttls.dll", EntryPoint = "gtls_read")]
    private static unsafe partial int TlsRead(IntPtr tls, byte* buffer, int max);

    [LibraryImport("guesttls.dll", EntryPoint = "gtls_write")]
    private static unsafe partial int TlsWrite(IntPtr tls, byte* bytes, int count);

    [LibraryImport("guesttls.dll", EntryPoint = "gtls_close")]
    private static partial void TlsClose(IntPtr tls);

    public bool StartTls(string certificate, string key)
    {
        // OpenSSL reads and writes the socket itself, with the timeouts set on it.
        socket.Blocking = true;
        tls = TlsAccept(socket.Handle, certificate, key);
        return tls != IntPtr.Zero;
    }

    /// Up to `max` bytes; 0 at the end of input or on an error.
    public unsafe int Read(byte[] buffer, int offset, int max)
    {
        if (tls != IntPtr.Zero)
        {
            fixed (byte* p = buffer) return TlsRead(tls, p + offset, max);
        }
        try { return socket.Receive(buffer, offset, max, SocketFlags.None); }
        catch (SocketException) { return 0; }
    }

    public bool Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public unsafe bool Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return true;
        if (tls != IntPtr.Zero)
        {
            fixed (byte* p = data) return TlsWrite(tls, p, data.Length) != 0;
        }
        try
        {
            while (!data.IsEmpty)
            {
                var n = socket.Send(data, SocketFlags.None);
                if (n <= 0) return false;
                data = data[n..];
            }
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// A graceful close: the reply's end sent, then what the guest still sends drained for a moment, so a reply that
    /// came before the request was read (an error) isn't lost to a reset.
    public void Dispose()
    {
        if (tls != IntPtr.Zero) TlsClose(tls);
        tls = IntPtr.Zero;
        try
        {
            socket.Shutdown(SocketShutdown.Send);
            socket.ReceiveTimeout = 1000;
            var buffer = new byte[16384];
            for (var total = 0; total < 1 << 20;)
            {
                var n = socket.Receive(buffer);
                if (n <= 0) break;
                total += n;
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        socket.Dispose();
    }
}
