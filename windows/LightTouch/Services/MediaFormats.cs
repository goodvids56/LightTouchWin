// The portable half of MediaPreparation: what a file is, what the device plays, the tags and durations ffprobe
// reports, and the content-derived ids (MediaIdentity.swift), apart from the System.Drawing and ffmpeg work, so the
// tests compile it on any OS.

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace LightTouch.Services;

internal enum MediaKind { Song, Photo, Video }

internal sealed record PreparedMedia(MediaKind Kind, string Id, string Directory, string File, string Title,
    string? Metadata = null, string? Artwork = null)
{
    /// The library it goes to: what the catalog's `media` lists.
    public string Destination => DestinationOf(Kind);

    public static string DestinationOf(MediaKind kind) => kind switch
    {
        MediaKind.Song => "Music",
        MediaKind.Photo => "Photos",
        _ => "Videos",
    };

    public void Delete()
    {
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class MediaException(string message) : Exception(message);

internal static partial class MediaPreparation
{
    public static readonly string[] SongExtensions = ["mp3", "m4a", "aac", "wav"];
    public static readonly string[] PhotoExtensions = ["jpg", "jpeg", "png", "heic", "heif"];
    public static readonly string[] VideoExtensions = ["mp4", "m4v", "mov"];
    private const long Gigabyte = 1L << 30;

    public static MediaKind? Kind(string path)
    {
        var suffix = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return SongExtensions.Contains(suffix) ? MediaKind.Song
            : PhotoExtensions.Contains(suffix) ? MediaKind.Photo
            : VideoExtensions.Contains(suffix) ? MediaKind.Video
            : null;
    }

    /// The tags the device's Music library keeps, under the metadata plist's keys (itmedia maps them onto
    /// MusicLibrary's item properties), from the container's tags as ffprobe reports them.
    internal static Dictionary<string, object> Tags(JsonElement probe)
    {
        var properties = new Dictionary<string, object>();
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement element)
        {
            if (!element.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Object) return;
            foreach (var p in t.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && !tags.ContainsKey(p.Name)) tags[p.Name] = p.Value.GetString()!;
            }
        }
        if (probe.TryGetProperty("format", out var format)) Collect(format);
        foreach (var stream in Streams(probe, "audio")) Collect(stream.Element);
        string? Text(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (tags.TryGetValue(key, out var value) && value.Trim() is { Length: > 0 } trimmed
                    && System.Text.Encoding.UTF8.GetByteCount(trimmed) <= 4096)
                {
                    return trimmed;
                }
            }
            return null;
        }
        void Set(string key, object? value)
        {
            if (value != null) properties.TryAdd(key, value);
        }
        void Pair(string numberKey, string countKey, string? value, string? separateCount = null)
        {
            if (value == null) return;
            var parts = value.Split('/', 2);
            if (int.TryParse(parts[0].Trim(), out var number) && number is >= 1 and <= 65535) Set(numberKey, number);
            var count = parts.Length > 1 ? parts[1] : separateCount;
            if (int.TryParse(count?.Trim(), out var total) && total is >= 1 and <= 65535) Set(countKey, total);
        }
        Set("title", Text("title"));
        Set("artist", Text("artist"));
        Set("album", Text("album"));
        Set("album_artist", Text("album_artist", "album artist", "TPE2"));
        Set("composer", Text("composer"));
        if (Text("genre") is { } genre) Set("genre", GenreName(genre));
        Pair("track_number", "track_count", Text("track"), Text("tracktotal", "totaltracks"));
        Pair("disc_number", "disc_count", Text("disc"), Text("disctotal", "totaldiscs"));
        if (Text("date", "year", "TDRC", "TYER", "TORY") is { Length: >= 4 } date
            && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1 and <= 9999)
        {
            Set("year", year);
        }
        if (Text("compilation", "TCMP", "cpil") is { } compilation && int.TryParse(compilation, out var flag))
        {
            Set("compilation", flag != 0);
        }
        return properties;
    }

    /// ID3 genres may be "(13)", "13" or "(13)Refinement"; the ID3v1 index resolves to its name.
    private static string GenreName(string value)
    {
        var digits = value.StartsWith('(') ? new string(value[1..].TakeWhile(c => c != ')').ToArray()) : value;
        return int.TryParse(digits, out var index) && index >= 0 && index < Id3Genres.Length ? Id3Genres[index] : value;
    }

    private static readonly string[] Id3Genres =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal",
        "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock", "Techno", "Industrial",
        "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal",
        "Jazz+Funk", "Fusion", "Trance", "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel",
        "Noise", "AlternRock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop", "Instrumental Rock",
        "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", "Dream",
        "Southern Rock", "Comedy", "Cult", "Gangsta", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
        "Native American", "Cabaret", "New Wave", "Psychadelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal",
        "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
    ];

    /// What the device plays as is (the iPod preset's ceiling: H.264 Baseline up to level 3.0 at 640×480, 30 fps;
    /// Main up to 3.1 at 1280×720 on the A4 devices; AAC-LC audio) is remuxed; anything else is converted.
    internal static bool Playable(ProbeStream video, ProbeStream? audio, bool hd)
    {
        var profile = video.Str("profile") ?? "";
        var width = video.Int("width") ?? 0;
        var height = video.Int("height") ?? 0;
        var fps = Rate(video.Str("avg_frame_rate")) ?? Rate(video.Str("r_frame_rate")) ?? 0;
        var playsVideo = video.Str("codec_name") == "h264"
            && (profile is "Baseline" or "Constrained Baseline" || (hd && profile == "Main"))
            && video.Int("level") is { } level && level > 0 && level <= (hd ? 31 : 30)
            && (video.Str("pix_fmt") ?? "yuv420p") == "yuv420p"
            && Math.Max(width, height) <= (hd ? 1280 : 640) && Math.Min(width, height) <= (hd ? 720 : 480)
            && fps is > 0 and <= 30.01
            && Rotation(video) == 0;
        var playsAudio = audio == null
            || (audio.Str("codec_name") == "aac" && audio.Str("profile") == "LC"
                && audio.Int("channels") is 1 or 2
                && int.TryParse(audio.Str("sample_rate"), out var rate) && rate <= 48000);
        return playsVideo && playsAudio;
    }

    private static double? Rate(string? fraction)
    {
        var parts = fraction?.Split('/');
        return parts is [var n, var d] && double.TryParse(n, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(d, CultureInfo.InvariantCulture, out var den) && den > 0
            ? num / den
            : null;
    }

    private static int Rotation(ProbeStream video)
    {
        if (video.Element.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var degrees) && degrees != 0) return (int)degrees;
            }
        }
        return video.Element.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate)
            && int.TryParse(rotate.GetString(), out var value) ? value : 0;
    }

    /// A JPEG's EXIF orientation (1 to 8; 1 when it has none), read from its APP1 segment's first IFD.
    internal static int ExifOrientation(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8) return 1;
        for (var p = 2; p + 4 <= jpeg.Length && jpeg[p] == 0xff;)
        {
            var marker = jpeg[p + 1];
            if (marker is 0xd9 or 0xda) break;
            var length = jpeg[p + 2] << 8 | jpeg[p + 3];
            if (length < 2 || p + 2 + length > jpeg.Length) break;
            var segment = jpeg.Slice(p + 4, length - 2);
            if (marker == 0xe1 && segment.Length >= 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                var tiff = segment[6..];
                var little = tiff[0] == 'I';
                int U16(ReadOnlySpan<byte> b, int at) => little ? b[at] | b[at + 1] << 8 : b[at] << 8 | b[at + 1];
                long U32(ReadOnlySpan<byte> b, int at) => little
                    ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..])
                    : BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
                var ifd = U32(tiff, 4);
                if (ifd < 8 || ifd + 2 > tiff.Length) return 1;
                var entries = U16(tiff, (int)ifd);
                for (var i = 0; i < entries; i++)
                {
                    var entry = (int)ifd + 2 + i * 12;
                    if (entry + 12 > tiff.Length) break;
                    if (U16(tiff, entry) == 0x0112) return U16(tiff, entry + 8) is var o and >= 1 and <= 8 ? o : 1;
                }
                return 1;
            }
            p += 2 + length;
        }
        return 1;
    }

    // MARK: - Identity (MediaIdentity.swift)

    /// Content-derived UUID: the first 16 bytes of the file's SHA-256, marked as a custom-content (version 8) UUID.
    internal static string Identity(string file)
    {
        using var input = File.OpenRead(file);
        var bytes = SHA256.HashData(input).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var hex = System.Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// Encoders put the current time in generated movie headers: only those timestamps are zeroed, so repeated
    /// conversion has identical bytes.
    internal static void NormalizeGeneratedMovie(string file)
    {
        using var handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
        var atoms = 0;
        static MediaException Invalid() => new("The converted media file is invalid.");
        byte[] Read(long offset, int count)
        {
            handle.Position = offset;
            var data = new byte[count];
            if (handle.Read(data, 0, count) != count) throw Invalid();
            return data;
        }
        static ulong Integer(ReadOnlySpan<byte> bytes)
        {
            ulong value = 0;
            foreach (var b in bytes) value = value << 8 | b;
            return value;
        }
        void Walk(long start, long end, int depth)
        {
            if (depth > 4) throw Invalid();
            for (var offset = start; offset < end;)
            {
                if (++atoms > 4096 || end - offset < 8) throw Invalid();
                var header = Read(offset, 8);
                var kind = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                var size = (long)Integer(header.AsSpan(0, 4));
                long headerSize = 8;
                if (size == 1)
                {
                    if (end - offset < 16) throw Invalid();
                    size = (long)Integer(Read(offset + 8, 8));
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = end - offset;
                }
                if (size < headerSize || size > end - offset) throw Invalid();
                if (kind is "moov" or "trak" or "mdia")
                {
                    Walk(offset + headerSize, offset + size, depth + 1);
                }
                else if (kind is "mvhd" or "tkhd" or "mdhd")
                {
                    if (size < headerSize + 4) throw Invalid();
                    var version = Read(offset + headerSize, 1)[0];
                    if (version > 1) throw Invalid();
                    var count = version == 0 ? 8 : 16;
                    if (size < headerSize + 4 + count) throw Invalid();
                    handle.Position = offset + headerSize + 4;
                    handle.Write(new byte[count]);
                }
                offset += size;
            }
        }
        Walk(0, handle.Length, 0);
    }

    internal sealed record ProbeStream(JsonElement Element)
    {
        public string? Str(string key) => Element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public int? Int(string key) => Element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        public bool Disposition(string key) =>
            Element.TryGetProperty("disposition", out var d) && d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;
    }

    internal static IEnumerable<ProbeStream> Streams(JsonElement probe, string type) =>
        probe.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
            ? streams.EnumerateArray().Select(s => new ProbeStream(s)).Where(s => s.Str("codec_type") == type)
            : [];

    internal static double Duration(JsonElement probe) =>
        probe.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            ? seconds
            : 0;

    /// FairPlay-protected tracks (iTunes Store purchases before 2009) keep their sample entries' DRM types.
    private static bool Protected(JsonElement probe) =>
        Streams(probe, "audio").Concat(Streams(probe, "video"))
            .Any(s => s.Str("codec_tag_string") is "drms" or "drmi" or "drmv" or "drac" or "encv" or "enca");
}
