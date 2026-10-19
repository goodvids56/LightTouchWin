// device.lock.json: what firmwarekit made a prepared base from and how it boots (HostRuntime's DeviceLock.swift).
// Read only, except that a reseeded built-in iPod rewrites its identity, machine and NOR hash.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LightTouch.Device;

internal sealed class DeviceLock
{
    public const string FileName = "device.lock.json";

    private readonly JsonObject root;

    private DeviceLock(JsonObject root) => this.root = root;

    public string? Board => (string?)root["board"];
    public string? Build => (string?)root["build"];
    public string? ProductVersion => (string?)root["product_version"];

    /// "iboot", "kboot", "bootrom"; null: the board's default (Board.RequiredFiles). Present, it must be a string.
    public string? BootStrategy
    {
        get
        {
            if (!root.ContainsKey("boot_strategy")) return null;
            return root["boot_strategy"] is JsonValue v && v.TryGetValue(out string? s)
                ? s
                : throw new InvalidDataException("device.lock.json: boot_strategy is not a string");
        }
    }

    /// Prepared past Setup Assistant (the Skip Setup option): it boots to the Home screen.
    public bool SkippedSetup => root["entry"]?["content"]?["recipe"]?["options"]?["skip_setup"] is JsonValue v
        && v.TryGetValue(out bool skip) && skip;

    public string? EntryId => (string?)root["entry"]?["id"];

    /// The lock at `base`, or null for a base without one (a legacy base); a malformed one throws.
    public static DeviceLock? Read(string baseDir)
    {
        var path = Path.Combine(baseDir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return new DeviceLock(JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException("not an object"));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid boot lock at {path}: {e.Message}", e);
        }
    }

    /// A scalar as a -machine option's text (JSONValue.optionText).
    private static string? OptionText(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out bool b)) return b ? "on" : "off";
        if (v.TryGetValue(out string? s)) return s;
        if (v.TryGetValue(out long l)) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (v.TryGetValue(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    /// The machine options a boot of `base` passes, with what older bases lack derived from their identity.json: the
    /// N72/K48 unit MACs, the N72's ECID and a kboot iPhone's IMEI.
    public Dictionary<string, string> MachineOptions(string baseDir)
    {
        var options = new Dictionary<string, string>();
        if (root["machine"] is JsonObject machine)
        {
            foreach (var (key, value) in machine)
            {
                if (OptionText(value) is { } text) options[key] = text;
            }
        }
        var board = Device.Board.Find(Board);
        var upgrades = board == Device.Board.N72 || board == Device.Board.K48 || board?.IsKBootPhone == true;
        var identityPath = Path.Combine(baseDir, "identity.json");
        if (!upgrades || !File.Exists(identityPath)) return options;
        JsonObject? identity;
        try { identity = JsonNode.Parse(File.ReadAllText(identityPath)) as JsonObject; }
        catch (System.Text.Json.JsonException) { return options; }
        if (identity == null) return options;
        string? Field(string key) => identity[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        if (board == Device.Board.N72 || board == Device.Board.K48)
        {
            // The iPad machine has wifi-mac only; its unit identity otherwise comes from die-id.
            foreach (var key in board == Device.Board.K48 ? ["wifi-mac"] : new[] { "wifi-mac", "bt-mac" })
            {
                if (!options.ContainsKey(key) && Field(key) is { Length: > 0 } mac) options[key] = mac;
            }
        }
        if (board == Device.Board.N72 && !options.ContainsKey("ecid"))
        {
            if (Field("unique-chip-id") is { } ecid) options["ecid"] = ecid;
            else if (Field("seed") is { } seed) options["ecid"] = UnitIdentity.EcidText(seed);
        }
        if (board?.IsKBootPhone == true && !options.ContainsKey("imei"))
        {
            var imei = Field("imei") ?? (Field("seed") is { } seed ? UnitIdentity.Imei(seed) : null);
            if (imei != null && Field("serial-number") != null && Field("wifi-mac") != null && Field("bt-mac") != null)
            {
                options["imei"] = imei;
            }
        }
        return options;
    }

    // MARK: - Reseeding (PreparedBase.reseed)

    /// Records a new unit identity: the identity's seed, UDID and file hash, its MACs and ECID as machine options,
    /// and the rewritten NOR's hash.
    public void Reidentify(UnitIdentity identity, string identityPath, byte[] nor)
    {
        root["identity"] = new JsonObject
        {
            ["seed"] = identity["seed"],
            ["udid"] = identity["udid"] ?? "",
            ["sha256"] = Hex(SHA256.HashData(File.ReadAllBytes(identityPath))),
        };
        var machine = root["machine"] as JsonObject ?? [];
        machine["wifi-mac"] = identity["wifi-mac"];
        machine["bt-mac"] = identity["bt-mac"];
        machine["ecid"] = identity["unique-chip-id"];
        root["machine"] = machine;
        if (root["outputs"]?["nor"] is JsonObject record) record["sha256"] = Hex(SHA256.HashData(nor));
    }

    /// The lock as FirmwareKit writes it: sorted keys, pretty-printed, slashes as they are.
    public void Write(string baseDir)
    {
        var path = Path.Combine(baseDir, FileName);
        var staged = path + ".tmp";
        File.WriteAllText(staged, Sorted(root)!.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        File.Move(staged, path, overwrite: true);
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
