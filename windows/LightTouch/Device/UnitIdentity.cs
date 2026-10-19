// A synthetic unit identity as a pure function of a seed (FirmwareKit's UnitIdentity and HostRuntime's
// IPhoneIdentity/UnitSeed), for giving the built-in iPod an identity of its own. Same seed, same bytes as the Mac's.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LightTouch.Device;

internal sealed class UnitIdentity
{
    private static readonly char[] SerialChars = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();

    /// Fields in the order Python's json.dump wrote them, which is what makes Json() match FirmwareKit's.
    private readonly List<(string Key, string Value)> fields = [];

    public string? this[string key] => fields.FirstOrDefault(f => f.Key == key).Value;

    /// The 40-bit ECID: SHA-256(seed) bytes 20..<25, odd.
    public static ulong Ecid(string seed)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        ulong ecid = 0;
        for (var i = 20; i < 25; i++) ecid = ecid << 8 | h[i];
        return ecid | 1;
    }

    public static string EcidText(string seed) => "0x" + Ecid(seed).ToString("x10", CultureInfo.InvariantCulture);

    /// TAC 00000000 (assigned to no manufacturer), six digits from the seed and the Luhn digit.
    public static string Imei(string seed)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("imei:" + seed));
        var body = "00000000" + string.Concat(h.Take(6).Select(b => (b % 10).ToString(CultureInfo.InvariantCulture)));
        return body + Luhn(body).ToString(CultureInfo.InvariantCulture);
    }

    public static int Luhn(string digits)
    {
        var sum = 0;
        var offset = 0;
        foreach (var c in digits.Reverse())
        {
            var d = c - '0';
            sum += offset % 2 == 0 ? (d * 2 > 9 ? d * 2 - 9 : d * 2) : d;
            offset++;
        }
        return (10 - sum % 10) % 10;
    }

    private static string Udid(string serial, string wifiMac, string btMac) => DeviceLock.Hex(
        SHA1.HashData(Encoding.UTF8.GetBytes(serial + wifiMac.ToLowerInvariant() + btMac.ToLowerInvariant())));

    private static string Mac(IEnumerable<byte> bytes) => string.Join(":", bytes.Select(b => b.ToString("x2")));

    /// The iPod touch 2G identity (synthesizeIPod over synthesize): the iPad generator's serial, MACs and ECID, a
    /// 12-digit battery serial, and the recipe's model and region.
    public static UnitIdentity SynthesizeIPod(string seed, string modelNumber, string regionInfo)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var serial = new string(h.Skip(0).Take(11).Select(b => SerialChars[b % SerialChars.Length]).ToArray());
        // even last byte: BT = +1 never carries
        byte[] wifi = [0x02, h[28], h[29], h[30], h[31], (byte)(h[27] & 0xFE)];
        byte[] bt = [.. wifi[..5], (byte)(wifi[5] + 1)];
        var battery = SHA256.HashData(Encoding.UTF8.GetBytes("battery:" + seed));
        var id = new UnitIdentity();
        id.fields.Add(("serial-number", serial));
        id.fields.Add(("wifi-mac", Mac(wifi)));
        id.fields.Add(("bt-mac", Mac(bt)));
        id.fields.Add(("battery-serial", string.Concat(battery.Take(12).Select(b => (b % 10).ToString()))));
        id.fields.Add(("model-number", modelNumber));
        id.fields.Add(("region-info", regionInfo));
        id.fields.Add(("seed", seed));
        id.fields.Add(("unique-chip-id", EcidText(seed)));
        id.fields.Add(("udid", Udid(serial, Mac(wifi), Mac(bt))));
        return id;
    }

    /// The string fields of an identity.json.
    public static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>();
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) throw new InvalidDataException(path);
        foreach (var (key, value) in o)
        {
            if (value is JsonValue v && v.TryGetValue(out string? s)) result[key] = s;
        }
        return result;
    }

    /// Python's json.dump(obj, f, indent=1) (ensure_ascii, no trailing newline).
    public byte[] Json()
    {
        static string Quote(string s)
        {
            var o = new StringBuilder("\"");
            foreach (var u in s)
            {
                o.Append(u switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    >= ' ' and <= '~' => u.ToString(),
                    _ => $"\\u{(int)u:x4}",
                });
            }
            return o.Append('"').ToString();
        }
        var body = string.Join(",\n", fields.Select(f => $" {Quote(f.Key)}: {Quote(f.Value)}"));
        return Encoding.UTF8.GetBytes(fields.Count == 0 ? "{}" : "{\n" + body + "\n}");
    }
}
