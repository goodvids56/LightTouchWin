// The .itpack guest packages (HostRuntime's GuestPack.swift; qemu-ios contrib/guest-package/mkpkg.py packs them):
// "ITPACK01", a little-endian u32 index length, the JSON index, then one zlib stream holding the entries in index
// order. Light Touch for Mac ships them in Guest/guest.aar's guest-tools/; the Windows app reads guest tools out of
// them (the web proxy's itproxy and ittrust).

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace LightTouch.Library;

internal static class GuestPack
{
    public static List<(string Name, byte[] Data)> Read(string path)
    {
        var blob = File.ReadAllBytes(path);
        if (blob.Length < 12 || !blob.AsSpan(0, 8).SequenceEqual("ITPACK01"u8)) throw new InvalidDataException($"{Path.GetFileName(path)} isn't an .itpack.");
        var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
        if (n < 0 || blob.Length < 12 + n) throw new InvalidDataException($"{Path.GetFileName(path)} is truncated.");
        using var index = JsonDocument.Parse(blob.AsMemory(12, n));
        using var stream = new ZLibStream(new MemoryStream(blob, 12 + n, blob.Length - 12 - n), CompressionMode.Decompress);
        var entries = new List<(string, byte[])>();
        foreach (var entry in index.RootElement.GetProperty("entries").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString() ?? "";
            var size = entry.GetProperty("size").GetInt32();
            if (name.StartsWith('/') || name.Split('/').Contains("..") || size < 0) throw new InvalidDataException($"Bad entry {name}.");
            var data = new byte[size];
            stream.ReadExactly(data);
            entries.Add((name, data));
        }
        return entries;
    }

    /// mkpkg's requires.builds: an exact build id, or "<major>*" for every build of that iOS major.
    public static bool BuildMatches(IEnumerable<string> builds, string build)
    {
        var major = new string(build.TakeWhile(char.IsAsciiDigit).ToArray());
        return builds.Any(b => b == build || (b.EndsWith('*') && b[..^1] == major));
    }

    /// The payload at `file` (a package path such as bin/ittrust) of the package for `board` and `build`, or null.
    public static byte[]? Payload(string itpack, string board, string build, string file)
    {
        var entries = Read(itpack);
        foreach (var (name, data) in entries.Where(e => e.Name.EndsWith("/manifest.json", StringComparison.Ordinal)))
        {
            using var manifest = JsonDocument.Parse(data);
            var requires = manifest.RootElement.GetProperty("requires");
            var boards = requires.GetProperty("boards").EnumerateArray().Select(b => b.GetString() ?? "");
            var builds = requires.GetProperty("builds").EnumerateArray().Select(b => b.GetString() ?? "");
            var stub = manifest.RootElement.TryGetProperty("stub", out var s) && s.ValueKind == JsonValueKind.True;
            if (stub || !boards.Contains(board) || !BuildMatches(builds, build)) continue;
            var prefix = name[..^"manifest.json".Length];
            var payload = entries.FirstOrDefault(e => e.Name == prefix + file);
            if (payload.Data != null) return payload.Data;
        }
        return null;
    }
}
