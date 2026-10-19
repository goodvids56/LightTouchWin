// A packed device (FirmwareKit's PreparedBase): the built-in iPod Light Touch for Mac ships as
// Contents/Resources/Device/n72ap-7E18.itbase, a directory of
//
//   index.json          format 2: every file (name, size, mode, sha256) and every NAND image's chunks (the sha256 of
//                       each 1 MiB of the raw image that is not all zero)
//   files/<name>        each file outside a NAND image, whole
//   <image>/NNNNNN.z    chunk N of an image (nand/csK.pages -> nand/csK/), zlib-compressed on its own; an all-zero
//                       chunk (blank NAND) has no file
//
// Releases before that layout (up to v1.0-2026.10.17) ship it as one file, the guest package's format: "ITPACK01", a
// little-endian u32 index length, {"entries": [{"name", "size", "mode"}]} in stream order, then one zlib stream of
// the files' bytes (its NAND as page files). Both unpack the same way into a base; the directory's files and chunks are
// checked against their sha256 and its chip files written sparse. Reseed gives the result a unit identity of its own,
// as the Mac app does on first launch.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LightTouch.Device;

namespace LightTouch.Library;

internal static class PackedBase
{
    private const int Chunk = 1 << 20;

    private sealed record FileEntry(string Name, long Size, int Mode, string Sha256);
    private sealed record ImageEntry(string Name, long Size, int Mode, Dictionary<string, string> Chunks);
    private sealed record Index(int Format, int ChunkBytes, List<FileEntry> Files, List<ImageEntry> Images);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Index ReadIndex(IFileSource source)
    {
        Index? index;
        try
        {
            using var stream = source.Open("index.json");
            index = JsonSerializer.Deserialize<Index>(stream, Json);
        }
        catch (Exception e) when (e is JsonException or FileNotFoundException)
        {
            throw new InvalidDataException("Not a packed device (no readable index.json).", e);
        }
        if (index is not { Format: 2, ChunkBytes: Chunk }) throw new InvalidDataException("Not a packed device.");
        foreach (var name in index.Files.Select(f => f.Name).Concat(index.Images.Select(i => i.Name)))
        {
            var parts = name.Split('/');
            if (name.Length == 0 || name.StartsWith('/') || parts.Any(p => p is "" or "." or ".."))
            {
                throw new InvalidDataException($"Packed device has a bad entry: {name}");
            }
        }
        return index;
    }

    /// Unpacks the packed device `name` in `parent`, in either format, into `directory` (created).
    public static void Unpack(IFileSource parent, string name, string directory, Action<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (parent.IsFile(name))
        {
            using var stream = parent.Open(name);
            UnpackStream(stream, directory, progress, cancel);
            return;
        }
        using var source = parent.Sub(name);
        Unpack(source, directory, progress, cancel);
    }

    private sealed record StreamEntry(string Name, long Size, int Mode);

    /// The single-stream format: every entry's bytes in index order out of one zlib stream.
    public static void UnpackStream(Stream input, string directory, Action<double>? progress = null,
        CancellationToken cancel = default)
    {
        var head = new byte[12];
        input.ReadExactly(head);
        if (!head.AsSpan(0, 8).SequenceEqual("ITPACK01"u8)) throw new InvalidDataException("Not a packed device.");
        var length = BitConverter.ToInt32(head, 8);
        if (length <= 0 || length > 64 << 20) throw new InvalidDataException("The packed device's index is damaged.");
        var indexBytes = new byte[length];
        input.ReadExactly(indexBytes);
        var entries = JsonSerializer.Deserialize<Dictionary<string, List<StreamEntry>>>(indexBytes, Json)?
            .GetValueOrDefault("entries") ?? throw new InvalidDataException("The packed device's index is damaged.");
        foreach (var e in entries)
        {
            var parts = e.Name.Split('/');
            if (e.Size < 0 || e.Name.Length == 0 || e.Name.StartsWith('/') || parts.Any(p => p is "" or "." or ".."))
            {
                throw new InvalidDataException($"Packed device has a bad entry: {e.Name}");
            }
        }
        Directory.CreateDirectory(directory);
        var total = Math.Max(entries.Sum(e => e.Size), 1);
        long written = 0;
        var reported = -1L;
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        var buffer = new byte[Chunk];
        foreach (var e in entries)
        {
            cancel.ThrowIfCancellationRequested();
            var path = Local(directory, e.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            for (var remaining = e.Size; remaining > 0;)
            {
                var got = inflate.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (got == 0) throw new InvalidDataException("The packed device is truncated.");
                output.Write(buffer, 0, got);
                remaining -= got;
                written += got;
            }
            if (written * 100 / total != reported)
            {
                reported = written * 100 / total;
                progress?.Invoke((double)written / total);
            }
        }
        if (inflate.Read(buffer, 0, 1) != 0) throw new InvalidDataException("The packed device has more data than its index names.");
        progress?.Invoke(1);
    }

    /// Unpacks the directory format `source` into `directory` (created), reporting the fraction done.
    public static void Unpack(IFileSource source, string directory, Action<double>? progress = null,
        CancellationToken cancel = default)
    {
        var index = ReadIndex(source);
        Directory.CreateDirectory(directory);
        double total = index.Files.Count + index.Images.Sum(i => i.Chunks.Count);
        double done = 0;
        foreach (var f in index.Files)
        {
            cancel.ThrowIfCancellationRequested();
            byte[] data;
            using (var stream = source.Open("files/" + f.Name))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                data = buffer.ToArray();
            }
            if (data.Length != f.Size || DeviceLock.Hex(SHA256.HashData(data)) != f.Sha256)
            {
                throw new InvalidDataException($"{f.Name} differs from the packed device's index.");
            }
            var path = Local(directory, f.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            progress?.Invoke(++done / Math.Max(total, 1));
        }
        foreach (var image in index.Images)
        {
            var path = Local(directory, image.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = SparseFile.Create(path, image.Size);
            var stem = image.Name[..^Path.GetExtension(image.Name).Length];
            var raw = new byte[Chunk];
            foreach (var (number, hash) in image.Chunks.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                if (!int.TryParse(number, out var i) || i < 0 || (long)i * Chunk >= image.Size)
                {
                    throw new InvalidDataException($"{image.Name} chunk {number} is outside the image.");
                }
                var length = (int)Math.Min(Chunk, image.Size - (long)i * Chunk);
                using (var packed = source.Open($"{stem}/{number}.z"))
                using (var inflate = new ZLibStream(packed, CompressionMode.Decompress))
                {
                    inflate.ReadExactly(raw, 0, length);
                    if (inflate.ReadByte() != -1) throw new InvalidDataException($"{image.Name} chunk {number} is damaged.");
                }
                if (DeviceLock.Hex(SHA256.HashData(raw.AsSpan(0, length))) != hash)
                {
                    throw new InvalidDataException($"{image.Name} chunk {number} is damaged.");
                }
                output.Position = (long)i * Chunk;
                output.Write(raw, 0, length);
                progress?.Invoke(++done / Math.Max(total, 1));
            }
            output.Flush(flushToDisk: true);
        }
        progress?.Invoke(1);
    }

    private static string Local(string directory, string name)
    {
        var path = Path.GetFullPath(Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Packed device entry leaves its folder: {name}");
        }
        return path;
    }

    /// Gives an unpacked n72 base the identity `firmwarekit create --seed` would: identity.json, the NOR's SysCfg and
    /// nvram, and the lock's identity, machine and NOR hash.
    public static void Reseed(string baseDir, string seed)
    {
        var lockFile = DeviceLock.Read(baseDir) ?? throw new InvalidDataException("The packed device has no lock.");
        if (lockFile.Board != Board.N72.Id)
        {
            throw new InvalidDataException($"Only an n72ap base takes a new identity, not {lockFile.Board}.");
        }
        var identityPath = Path.Combine(baseDir, "identity.json");
        var norPath = Path.Combine(baseDir, "nor.bin");
        var old = UnitIdentity.Load(identityPath);
        if (!old.TryGetValue("model-number", out var model) || !old.TryGetValue("region-info", out var region))
        {
            throw new InvalidDataException("identity.json has no model or region.");
        }
        var id = UnitIdentity.SynthesizeIPod(seed, model, region);
        File.WriteAllBytes(identityPath, id.Json());
        var nor = N72Nor.Reidentify(File.ReadAllBytes(norPath), id);
        File.WriteAllBytes(norPath, nor);
        lockFile.Reidentify(id, identityPath, nor);
        lockFile.Write(baseDir);
    }
}

/// Where a packed device or a Mac app's resources are read from: a folder, or a folder inside a zip.
internal interface IFileSource : IDisposable
{
    Stream Open(string relativePath);
    bool Exists(string relativePath);
    bool IsFile(string relativePath);
    IFileSource Sub(string relativePath);
}

internal sealed class FolderSource(string root) : IFileSource
{
    private string Path(string relative) => System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public Stream Open(string relativePath) =>
        File.Exists(Path(relativePath)) ? File.OpenRead(Path(relativePath)) : throw new FileNotFoundException(relativePath);

    public bool Exists(string relativePath) => File.Exists(Path(relativePath)) || Directory.Exists(Path(relativePath));
    public bool IsFile(string relativePath) => File.Exists(Path(relativePath));
    public IFileSource Sub(string relativePath) => new FolderSource(Path(relativePath));
    public void Dispose() { }
}

/// A zip (Light Touch for Mac's LightTouchMac-universal.zip) read in place, under `prefix`.
internal sealed class ZipSource : IFileSource
{
    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries;
    private readonly string prefix;
    private readonly bool owner;

    public ZipSource(string path) : this(ZipFile.OpenRead(path), "", owner: true) { }

    private ZipSource(ZipArchive archive, string prefix, bool owner)
    {
        this.archive = archive;
        this.prefix = prefix;
        this.owner = owner;
        entries = archive.Entries.GroupBy(e => e.FullName.Replace('\\', '/'))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public Stream Open(string relativePath) => entries.TryGetValue(prefix + relativePath, out var entry)
        ? entry.Open()
        : throw new FileNotFoundException(relativePath);

    public bool Exists(string relativePath)
    {
        var name = prefix + relativePath;
        return entries.ContainsKey(name) || entries.Keys.Any(k => k.StartsWith(name.TrimEnd('/') + "/", StringComparison.Ordinal));
    }

    public bool IsFile(string relativePath) =>
        entries.TryGetValue(prefix + relativePath, out var entry) && !entry.FullName.EndsWith('/');

    public IFileSource Sub(string relativePath) => new ZipSource(archive, prefix + relativePath.TrimEnd('/') + "/", owner: false);

    /// The first entry path ending in `suffix` (e.g. the app's Resources/Device/), for a zip whose top folder's
    /// name isn't known.
    public string? Find(string suffix) => entries.Keys
        .Select(k => k.IndexOf(suffix, StringComparison.Ordinal) is var i and >= 0 ? k[..(i + suffix.Length)] : null)
        .FirstOrDefault(k => k != null);

    public void Dispose()
    {
        if (owner) archive.Dispose();
    }
}
