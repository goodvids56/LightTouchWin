using System.Text;
using System.Text.Json;
using LightTouch.Library;
using LightTouch.Services;
using Xunit;

namespace LightTouch.Tests;

public class MediaTests
{
    [Fact]
    public void IdentityIsTheMacsContentUuid()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, Encoding.ASCII.GetBytes("hello"));
            // SHA-256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e..., version and variant bits set as MediaIdentity.swift.
            Assert.Equal("2cf24dba-5fb0-830e-a6e8-3b2ac5b9e29e", MediaPreparation.Identity(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void NormalizingZeroesOnlyMovieTimestamps()
    {
        static byte[] Atom(string kind, byte[] body) =>
            [.. BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(8 + body.Length)), .. Encoding.ASCII.GetBytes(kind), .. body];
        byte[] header = [0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 9];
        var movie = Atom("ftyp", Encoding.ASCII.GetBytes("M4V "))
            .Concat(Atom("moov", Atom("mvhd", header).Concat(Atom("trak", Atom("tkhd", header))).ToArray())).ToArray();
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, movie);
            MediaPreparation.NormalizeGeneratedMovie(file);
            var result = File.ReadAllBytes(file);
            Assert.Equal(movie.Length, result.Length);
            var mvhd = Encoding.ASCII.GetBytes("mvhd");
            var at = result.AsSpan().IndexOf(mvhd) + 4;
            Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9 }, result[at..(at + 16)]);
            Assert.Equal(movie[..16], result[..16]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TagsBecomeTheMetadataPlistsKeys()
    {
        using var probe = JsonDocument.Parse("""
            {"format": {"duration": "61.5", "tags": {"title": " Song ", "ARTIST": "Band", "album": "Record",
              "album_artist": "Band", "genre": "(17)", "track": "3/12", "disc": "1", "disctotal": "2",
              "date": "1999-04-01", "compilation": "1"}},
             "streams": [{"index": 0, "codec_type": "audio", "codec_name": "mp3"},
                         {"index": 1, "codec_type": "video", "codec_name": "mjpeg", "disposition": {"attached_pic": 1}}]}
            """);
        var tags = MediaPreparation.Tags(probe.RootElement);
        Assert.Equal("Song", tags["title"]);
        Assert.Equal("Band", tags["artist"]);
        Assert.Equal("Rock", tags["genre"]);
        Assert.Equal(3, tags["track_number"]);
        Assert.Equal(12, tags["track_count"]);
        Assert.Equal(1, tags["disc_number"]);
        Assert.Equal(2, tags["disc_count"]);
        Assert.Equal(1999, tags["year"]);
        Assert.Equal(true, tags["compilation"]);
        Assert.Equal(61.5, MediaPreparation.Duration(probe.RootElement));
        Assert.True(MediaPreparation.Streams(probe.RootElement, "video").Single().Disposition("attached_pic"));
    }

    private static MediaPreparation.ProbeStream Stream(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void OnlyWhatTheDevicePlaysIsRemuxed()
    {
        var aac = Stream("""{"codec_name": "aac", "profile": "LC", "channels": 2, "sample_rate": "44100"}""");
        var ipod = Stream("""{"codec_name": "h264", "profile": "Constrained Baseline", "level": 30, "width": 640, "height": 360, "pix_fmt": "yuv420p", "avg_frame_rate": "30000/1001"}""");
        Assert.True(MediaPreparation.Playable(ipod, aac, hd: false));
        Assert.True(MediaPreparation.Playable(ipod, null, hd: false));
        var hd = Stream("""{"codec_name": "h264", "profile": "Main", "level": 31, "width": 1280, "height": 720, "pix_fmt": "yuv420p", "avg_frame_rate": "30/1"}""");
        Assert.False(MediaPreparation.Playable(hd, aac, hd: false));
        Assert.True(MediaPreparation.Playable(hd, aac, hd: true));
        var high = Stream("""{"codec_name": "h264", "profile": "High", "level": 40, "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "avg_frame_rate": "24/1"}""");
        Assert.False(MediaPreparation.Playable(high, aac, hd: true));
        Assert.False(MediaPreparation.Playable(ipod, Stream("""{"codec_name": "ac3", "channels": 6, "sample_rate": "48000"}"""), hd: false));
    }

    [Fact]
    public void ExifOrientationIsReadInEitherByteOrder()
    {
        static byte[] Jpeg(bool little, ushort orientation)
        {
            byte[] U16(ushort v) => little ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v];
            byte[] U32(uint v) => little ? BitConverter.GetBytes(v) : BitConverter.GetBytes(v).Reverse().ToArray();
            byte[] tiff = [.. (little ? "II"u8.ToArray() : "MM"u8.ToArray()), .. U16(42), .. U32(8), .. U16(1),
                .. U16(0x0112), .. U16(3), .. U32(1), .. U16(orientation), 0, 0, .. U32(0)];
            byte[] app1 = [.. "Exif\0\0"u8.ToArray(), .. tiff];
            return [0xff, 0xd8, 0xff, 0xe1, (byte)((app1.Length + 2) >> 8), (byte)(app1.Length + 2), .. app1, 0xff, 0xd9];
        }
        Assert.Equal(6, MediaPreparation.ExifOrientation(Jpeg(little: true, 6)));
        Assert.Equal(8, MediaPreparation.ExifOrientation(Jpeg(little: false, 8)));
        Assert.Equal(1, MediaPreparation.ExifOrientation([0xff, 0xd8, 0xff, 0xd9]));
        Assert.Equal(1, MediaPreparation.ExifOrientation("not a jpeg"u8));
    }

    [Fact]
    public void KindsFollowTheMacsExtensions()
    {
        Assert.Equal(MediaKind.Song, MediaPreparation.Kind(@"C:\Music\a.MP3"));
        Assert.Equal(MediaKind.Photo, MediaPreparation.Kind("b.heic"));
        Assert.Equal(MediaKind.Video, MediaPreparation.Kind("c.m4v"));
        Assert.Null(MediaPreparation.Kind("d.flac"));
    }

    /// An uncompressed pbze stream (both sizes equal) of an AA01 directory and file, and one outside the filter.
    [Fact]
    public void GuestArchiveUnpacksTheWantedEntries()
    {
        static byte[] Entry(char type, string path, byte[]? data)
        {
            var fields = new List<byte>();
            fields.AddRange("TYP1"u8.ToArray());
            fields.Add((byte)type);
            fields.AddRange("PATP"u8.ToArray());
            fields.AddRange(BitConverter.GetBytes((ushort)Encoding.UTF8.GetByteCount(path)));
            fields.AddRange(Encoding.UTF8.GetBytes(path));
            fields.AddRange("MOD2"u8.ToArray());
            fields.AddRange(BitConverter.GetBytes((ushort)0x1ed));
            if (data != null)
            {
                fields.AddRange("DATA"u8.ToArray());
                fields.AddRange(BitConverter.GetBytes((ushort)data.Length));
            }
            return [.. "AA01"u8.ToArray(), .. BitConverter.GetBytes((ushort)(6 + fields.Count)), .. fields, .. data ?? []];
        }
        var archive = Entry('D', "tools", null).Concat(Entry('F', "tools/itmedia", "media"u8.ToArray()))
            .Concat(Entry('F', "developer-tools/big", new byte[300])).ToArray();
        static byte[] BigEndian(ulong value) => BitConverter.GetBytes(value).Reverse().ToArray();
        var pbze = "pbze"u8.ToArray().Concat(BigEndian(1 << 22)).Concat(BigEndian((ulong)archive.Length))
            .Concat(BigEndian((ulong)archive.Length)).Concat(archive).ToArray();
        var target = Path.Combine(Path.GetTempPath(), "ltw-guest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var count = GuestArchive.Extract(new MemoryStream(pbze), target, p => p.StartsWith("tools", StringComparison.Ordinal));
            Assert.Equal(1, count);
            Assert.Equal("media", File.ReadAllText(Path.Combine(target, "tools", "itmedia")));
            Assert.False(Directory.Exists(Path.Combine(target, "developer-tools")));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    /// Light Touch for Mac's own guest.aar (LTW_GUEST_AAR), LZFSE blocks and all; needs lzfse on the library path.
    [Fact]
    public void RealGuestArchiveHasTheMediaHelpers()
    {
        var aar = Environment.GetEnvironmentVariable("LTW_GUEST_AAR");
        if (aar == null) return;
        var target = Path.Combine(Path.GetTempPath(), "ltw-guest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = File.OpenRead(aar);
            GuestArchive.Extract(stream, target, _ => true);
            Assert.True(new FileInfo(Path.Combine(target, "tools", "itmedia")).Length > 0);
            Assert.True(new FileInfo(Path.Combine(target, "tools", "itphoto")).Length > 0);
            Assert.True(File.Exists(Path.Combine(target, "guest-tools", "it_agent")));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
