// The web proxy's answers for services that no longer speak to a 2009-2010 guest (LightTouchDevice's
// WebProxyAdapters.swift, which WebProxy dispatches to): the stock Weather gateway on Open-Meteo, the Wi-Fi location
// server, the retired API hosts; plus the archive's link rewrite.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace LightTouch.Proxy;

internal static class WebProxyAdapters
{
    // MARK: - Retired services

    /// Exact retired API hosts only (never a vendor's whole domain); a DNS trailing dot and case are normalized.
    /// OpenFeint (shut down 2012) and the YouTube Data API v2 (retired 2015): HTTP 410 without contacting them.
    public static bool Retired(string host)
    {
        var name = host.ToLowerInvariant().TrimEnd('.');
        return name is "api.openfeint.com" or "gdata.youtube.com";
    }

    // MARK: - Archive rewrites

    /// Dated browsing is a read-only HTTP presentation: absolute https:// links in a text resource become http:// so
    /// old WebKit never opens a tunnel it can't finish. Binary resources are never passed here.
    public static byte[] HttpLinks(byte[] body)
    {
        var pattern = "https://"u8;
        var output = new List<byte>(body.Length);
        for (var i = 0; i < body.Length;)
        {
            var match = i + 8 <= body.Length;
            for (var j = 0; match && j < 8; j++) match = (body[i + j] | 0x20) == (pattern[j] | 0x20);
            if (match)
            {
                output.AddRange("http://"u8.ToArray());
                i += 8;
            }
            else
            {
                output.Add(body[i++]);
            }
        }
        return [.. output];
    }

    public static bool Textual(string? contentType)
    {
        var type = (contentType ?? "").ToLowerInvariant();
        return new[] { "text/html", "text/css", "text/javascript", "application/javascript", "application/xhtml+xml" }
            .Any(type.StartsWith);
    }

    // MARK: - Wi-Fi location (docs/ipad1/location.md in qemu-ios)

    /// iOS 3.2's locationd POSTs the BSSIDs it sees to AppleLocationServer (baked to http://10.0.2.100:3128/clls/wloc)
    /// and gets a position per BSSID back; every BSSID is placed at `position`. Wire format (PBRequester,
    /// ProtocolBuffer.framework 3.2), big-endian: request u16 version=1, three u16-length strings (locale, app id, OS
    /// build), u32 type=1, u32 length, ALSLocationRequest { 2: ALSWirelessAP { 1: macID } }; response u16 1, u32 type,
    /// u32 length, ALSLocationResponse { 2: ALSWirelessAP { 1: macID, 2: ALSLocation { 1: lat, 2: lon, 3: accuracy } } },
    /// degrees as int64 × 1e8, accuracy in meters. Null: not a location request; 400: malformed.
    public static (int Status, byte[] Body)? Location(string target, string method, byte[] body, (double Lat, double Lon, double Accuracy) position)
    {
        var path = target;
        var scheme = target.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var slash = target.IndexOf('/', scheme + 3);
            if (slash < 0) return null;
            path = target[slash..];
        }
        if (path != "/clls/wloc" || method != "POST") return null;
        var c = new Protobuf(body);
        if (c.BigEndian(2) != 1) return (400, []);
        for (var i = 0; i < 3; i++)
        {
            if (c.BigEndian(2) is not { } n || !c.Skip((int)n)) return (400, []);
        }
        if (c.BigEndian(4) is not { } type || c.BigEndian(4) is not { } size || c.Take((int)size) is not { } request) return (400, []);
        var macs = new List<byte[]>();
        var fields = new Protobuf(request);
        while (!fields.AtEnd)
        {
            if (fields.Field() is not var (number, ap)) return (400, []);
            if (number != 2 || ap == null) continue;
            var inner = new Protobuf(ap);
            while (!inner.AtEnd)
            {
                if (inner.Field() is not var (innerNumber, mac)) return (400, []);
                if (innerNumber == 1 && mac != null && macs.Count < 64) macs.Add(mac);
            }
        }
        var message = new Protobuf.Writer();
        foreach (var mac in macs)
        {
            var place = new Protobuf.Writer();
            place.Int(1, (long)Math.Round(position.Lat * 1e8));
            place.Int(2, (long)Math.Round(position.Lon * 1e8));
            place.Int(3, (long)Math.Round(position.Accuracy));
            var entry = new Protobuf.Writer();
            entry.Bytes(1, mac);
            entry.Bytes(2, place.Output);
            message.Bytes(2, entry.Output);
        }
        var output = message.Output;
        byte[] head = [0, 1, (byte)(type >> 24), (byte)(type >> 16), (byte)(type >> 8), (byte)type,
            (byte)(output.Length >> 24), (byte)(output.Length >> 16), (byte)(output.Length >> 8), (byte)output.Length];
        return (200, [.. head, .. output]);
    }

    /// CONFIG.location, "LAT LON [ACCURACY]", replaceable while the guest runs; else Apple Park, 30 m.
    public static (double, double, double) Position(string file)
    {
        string text;
        try { text = File.ReadAllText(file); } catch (IOException) { text = ""; } catch (UnauthorizedAccessException) { text = ""; }
        var values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null).ToList();
        if (values.Count < 2 || values[0] is not { } lat || values[1] is not { } lon || !double.IsFinite(lat) || !double.IsFinite(lon)
            || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
        {
            return (37.33490, -122.00898, 30);
        }
        var accuracy = values.Count == 3 && values[2] is { } a && double.IsFinite(a) && a > 0 && a < 100_000 ? a : 30;
        return (lat, lon, accuracy);
    }

    private sealed class Protobuf(byte[] bytes)
    {
        private int position;
        public bool AtEnd => position >= bytes.Length;

        public uint? BigEndian(int count)
        {
            if (Take(count) is not { } b) return null;
            uint value = 0;
            foreach (var x in b) value = value << 8 | x;
            return value;
        }

        public byte[]? Take(int count)
        {
            if (count < 0 || count > bytes.Length - position) return null;
            var result = bytes[position..(position + count)];
            position += count;
            return result;
        }

        public bool Skip(int count) => Take(count) != null;

        public ulong? Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (AtEnd) return null;
                var b = bytes[position++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return value;
            }
            return null;
        }

        /// The next field: its number, and its bytes when it is length-delimited.
        public (ulong, byte[]?)? Field()
        {
            if (Varint() is not { } key) return null;
            switch (key & 7)
            {
                case 0: return Varint() is null ? null : (key >> 3, null);
                case 1: return Take(8) is null ? null : (key >> 3, null);
                case 5: return Take(4) is null ? null : (key >> 3, null);
                case 2:
                    if (Varint() is not { } n || n > (ulong)(bytes.Length - position)) return null;
                    return (key >> 3, Take((int)n));
                default: return null;
            }
        }

        public sealed class Writer
        {
            private readonly List<byte> output = [];
            public byte[] Output => [.. output];

            private void Varint(ulong value)
            {
                do
                {
                    output.Add((byte)((value & 0x7f) | (value > 0x7f ? 0x80u : 0)));
                    value >>= 7;
                } while (value > 0);
            }

            public void Int(ulong number, long value)
            {
                Varint(number << 3);
                Varint((ulong)value);
            }

            public void Bytes(ulong number, byte[] value)
            {
                Varint(number << 3 | 2);
                Varint((ulong)value.Length);
                output.AddRange(value);
            }
        }
    }

    // MARK: - Weather (the stock 7E18 gateway on Open-Meteo)

    /// iphone-wu.apple.com/dgw?apptype=weather, answered from Open-Meteo's geocoding and forecast APIs (CC BY 4.0;
    /// noncommercial free tier). City searches return opaque "ltm:" ids; the two default Yahoo ids (Cupertino, New York)
    /// work too; other old ids get 422 rather than guessed coordinates. Bad provider data fails the update (the guest
    /// keeps its forecast); nothing is made up. Null: another service. `fetch(host, path, query)` is an HTTPS GET
    /// returning parsed JSON, or null.
    public static (int Status, byte[] Body)? Weather(string target, string method, byte[] body,
        Func<string, string, Dictionary<string, string>, JsonElement?> fetch)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || !url.Host.Equals("iphone-wu.apple.com", StringComparison.OrdinalIgnoreCase)
            || url.AbsolutePath != "/dgw")
        {
            return null;
        }
        var services = Query(url).Where(q => q.Key == "apptype").ToList();
        if (services.Count > 1) return (400, []);
        if (services.FirstOrDefault().Value != "weather") return null;
        if (method != "POST" || url.UserInfo.Length > 0 || body.Length == 0 || body.Length > 65536 || body.Contains((byte)0)) return (400, []);
        XElement root;
        try
        {
            var xml = new UTF8Encoding(false, true).GetString(body);
            if (xml.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || xml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) return (400, []);
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XDocument.Load(reader).Root ?? throw new XmlException();
        }
        catch (Exception e) when (e is XmlException or DecoderFallbackException)
        {
            return (400, []);
        }
        if (root.Name != "request" || root.Elements("query").Count() != 1) return (400, []);
        var query = root.Element("query")!;
        var response = new XElement("response");
        var list = Child(Child(response, "result"), "list");
        switch (query.Attribute("type")?.Value)
        {
            case "getlocationid":
                var phrases = query.Elements("phrase").ToList();
                if (phrases.Count != 1 || Text(phrases[0].Value, 200) is not { } phrase) return (400, []);
                if (phrase.Length >= 2)
                {
                    var result = fetch("geocoding-api.open-meteo.com", "/v1/search",
                        new() { ["name"] = phrase, ["count"] = "10", ["language"] = "en", ["format"] = "json" });
                    if (result is not { ValueKind: JsonValueKind.Object } found) return (502, []);
                    var places = found.TryGetProperty("results", out var r) ? r : default;
                    if (places.ValueKind == JsonValueKind.Undefined) break;
                    if (places.ValueKind != JsonValueKind.Array || places.GetArrayLength() > 10) return (502, []);
                    foreach (var entry in places.EnumerateArray())
                    {
                        if (ReadPlace(entry) is not { } place) return (502, []);
                        var item = Child(list, "item");
                        Child(item, "id", Identifier(place));
                        Child(item, "city", place.Name);
                        Child(item, "region", Text(entry, "admin1", 200) ?? "");
                        Child(item, "regionname", Text(entry, "admin1", 200) ?? "");
                        Child(item, "country", Text(entry, "country_code", 8) ?? "");
                        Child(item, "countryname", Text(entry, "country", 200) ?? "");
                    }
                }
                break;
            case "getforecastbylocationid":
                var identifiers = query.Elements("list").Elements("id").Select(e => e.Value).ToList();
                var units = query.Elements("unit").ToList();
                if (identifiers.Count is < 1 or > 20 || units.Count != 1 || units[0].Value is not ("c" or "f")) return (400, []);
                var celsius = units[0].Value == "c";
                var wanted = new List<Place>();
                foreach (var id in identifiers)
                {
                    if (Decode(id) is not { } place) return (422, []); // an unknown retired Yahoo id: never guess
                    wanted.Add(place);
                }
                var forecast = fetch("api.open-meteo.com", "/v1/forecast", new()
                {
                    ["latitude"] = string.Join(',', wanted.Select(p => p.Latitude.ToString("R", CultureInfo.InvariantCulture))),
                    ["longitude"] = string.Join(',', wanted.Select(p => p.Longitude.ToString("R", CultureInfo.InvariantCulture))),
                    ["current"] = "temperature_2m,weather_code,is_day",
                    ["daily"] = "weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset",
                    ["temperature_unit"] = celsius ? "celsius" : "fahrenheit",
                    ["timezone"] = "auto",
                    ["forecast_days"] = "6",
                });
                if (forecast is not { } data) return (502, []);
                var forecasts = data.ValueKind == JsonValueKind.Object ? [data]
                    : data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : [];
                if (forecasts.Count != wanted.Count) return (502, []);
                for (var i = 0; i < wanted.Count; i++)
                {
                    if (Forecast(forecasts[i], identifiers[i], wanted[i], celsius) is not { } item) return (502, []);
                    list.Add(item);
                }
                break;
            default:
                return (400, []);
        }
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), response).Save(writer);
        }
        return (200, stream.ToArray());
    }

    internal sealed record Place(double Latitude, double Longitude, string Name);

    private static IEnumerable<KeyValuePair<string, string>> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var parts = pair.Split('=', 2);
            return new KeyValuePair<string, string>(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        });

    private static double? Number(JsonElement value, double low, double high) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= low && n <= high ? n : null;

    private static double? Number(JsonElement parent, string key, double low, double high) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? Number(v, low, high) : null;

    private static string? Text(string? s, int maximum) =>
        s != null && s.Length <= maximum && !s.Any(char.IsControl) ? s : null;

    private static string? Text(JsonElement parent, string key, int maximum) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? Text(v, maximum) : null;

    private static string? Text(JsonElement value, int maximum) =>
        value.ValueKind == JsonValueKind.String ? Text(value.GetString(), maximum) : null;

    internal static Place? ReadPlace(JsonElement value) =>
        Number(value, "latitude", -90, 90) is { } lat && Number(value, "longitude", -180, 180) is { } lon
            && Text(value, "name", 200) is { Length: > 0 } name
            ? new Place(lat, lon, name)
            : null;

    /// "ltm:" and the place as JSON with sorted keys, base64.
    internal static string Identifier(Place place)
    {
        var json = JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["latitude"] = place.Latitude,
            ["longitude"] = place.Longitude,
            ["name"] = place.Name,
        });
        return "ltm:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static Place? Decode(string id)
    {
        if (id == "USCA0273|12797509") return new Place(37.323, -122.032, "Cupertino");
        if (id == "USNY0996|2459115") return new Place(40.7143, -74.006, "New York");
        if (id.Length > 2048 || !id.StartsWith("ltm:", StringComparison.Ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(id[4..]));
            return ReadPlace(document.RootElement);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    internal static string? Icon(JsonElement code, bool daylight)
    {
        if (Number(code, 0, 99) is not { } value || value != Math.Round(value)) return null;
        return (int)value switch
        {
            0 => daylight ? "32" : "31",
            1 => daylight ? "34" : "33",
            2 => daylight ? "30" : "29",
            3 => "26",
            45 or 48 => "20",
            51 or 53 or 55 => "9",
            56 or 57 => "8",
            61 or 63 or 65 => "12",
            66 or 67 => "10",
            71 or 73 or 75 or 77 => "16",
            80 or 81 or 82 => "11",
            85 or 86 => "14",
            95 => "4",
            96 or 99 => "3",
            _ => null,
        };
    }

    /// Strict: the text must format back to itself (no lenient Feb 30).
    private static DateTime? Date(JsonElement value, string format) =>
        Text(value, 32) is { } text && DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            && date.ToString(format, CultureInfo.InvariantCulture) == text
            ? date
            : null;

    private static string? ClockTime(JsonElement value) =>
        Date(value, "yyyy-MM-dd'T'HH:mm") != null ? value.GetString()![11..] : null;

    /// NASA's 2000-01-06 18:15 UTC new moon; USNO mean synodic month 29.53059 days.
    internal static Dictionary<string, string> Moon(double timestamp)
    {
        var cycle = (timestamp - 947182500.0) / (29.53059 * 86400.0);
        cycle -= Math.Floor(cycle);
        return new()
        {
            ["moonphase"] = ((int)Math.Floor(cycle * 8 + 0.5) % 8).ToString(CultureInfo.InvariantCulture),
            ["moonfacevisible"] = (50 * (1 - Math.Cos(2 * Math.PI * cycle))).ToString("F3", CultureInfo.InvariantCulture),
        };
    }

    private static string Whole(double value) => Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);

    internal static XElement? Forecast(JsonElement data, string id, Place place, bool celsius)
    {
        if (data.ValueKind != JsonValueKind.Object || Number(data, "utc_offset_seconds", -50400, 50400) is not { } offset
            || !data.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object
            || Number(current, "temperature_2m", -200, 200) is not { } temperature
            || Number(current, "is_day", 0, 1) is not { } isDay || isDay != Math.Round(isDay)
            || !current.TryGetProperty("time", out var nowText) || ClockTime(nowText) is not { } time
            || Date(nowText, "yyyy-MM-dd'T'HH:mm") is not { } now
            || !current.TryGetProperty("weather_code", out var code) || Icon(code, isDay == 1) is not { } icon)
        {
            return null;
        }
        List<JsonElement>? Column(string key) =>
            daily.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 6 ? [.. c.EnumerateArray()] : null;
        if (Column("time") is not { } times || Column("temperature_2m_max") is not { } highs || Column("temperature_2m_min") is not { } lows
            || Column("weather_code") is not { } codes || Column("sunrise") is not { } sunrises || Column("sunset") is not { } sunsets
            || ClockTime(sunrises[0]) is not { } sunrise || ClockTime(sunsets[0]) is not { } sunset)
        {
            return null;
        }
        var item = new XElement("item");
        Attributes(Child(item, "location"), new() { ["id"] = id, ["city"] = place.Name });
        Attributes(Child(item, "units"), new() { ["temperature"] = celsius ? "C" : "F" });
        var astronomy = new Dictionary<string, string> { ["sunrise"] = sunrise, ["sunset"] = sunset };
        var epoch = (now - DateTime.UnixEpoch).TotalSeconds - offset;
        foreach (var (k, v) in Moon(epoch)) astronomy[k] = v;
        Attributes(Child(item, "astronomy"), astronomy);
        Attributes(Child(item, "condition"), new() { ["time"] = time, ["temp"] = Whole(temperature), ["code"] = icon });
        for (var day = 0; day < 6; day++)
        {
            if (Date(times[day], "yyyy-MM-dd") is not { } when || Icon(codes[day], daylight: true) is not { } condition
                || Number(highs[day], -200, 200) is not { } high || Number(lows[day], -200, 200) is not { } low || high < low)
            {
                return null;
            }
            Attributes(Child(item, "forecast"), new()
            {
                ["high"] = Whole(high),
                ["low"] = Whole(low),
                ["code"] = condition,
                ["dayofweek"] = ((int)when.DayOfWeek + 1).ToString(CultureInfo.InvariantCulture),
            });
        }
        Child(item, "link", "http://open-meteo.com/");
        return item;
    }

    private static XElement Child(XElement parent, string name, string text = "")
    {
        var node = new XElement(name, text);
        parent.Add(node);
        return node;
    }

    private static void Attributes(XElement node, Dictionary<string, string> values)
    {
        foreach (var (key, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal)) node.SetAttributeValue(key, value);
    }
}
