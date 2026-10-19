// Songs, photos and videos made ready for the device's own libraries before they are staged, as LightTouchCore's
// MediaSong, MediaPhoto and MediaVideo prepare them on the Mac: a private snapshot of the file in its own folder, the
// metadata plist the guest's itmedia reads (tags, duration), and a content-derived id (MediaIdentity) that names the
// staging folder on the device. Where the Mac reads media with AVFoundation and ImageIO, this reads it with ffprobe
// and System.Drawing, and converts with ffmpeg (windows/scripts/build-media.sh): ADTS AAC remuxed into M4A, videos the
// iPod can't play converted to its H.264/AAC M4V with Windows' H.264 encoder (the Mac uses AVFoundation's iPod and 720p
// presets).

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LightTouch.Device;
using LightTouch.Native;

namespace LightTouch.Services;

internal static partial class MediaPreparation
{
    public static string DialogFilter =>
        "Songs, photos and videos|" + string.Join(';', SongExtensions.Concat(PhotoExtensions).Concat(VideoExtensions).Select(e => "*." + e))
        + "|Songs|" + string.Join(';', SongExtensions.Select(e => "*." + e))
        + "|Photos|" + string.Join(';', PhotoExtensions.Select(e => "*." + e))
        + "|Videos|" + string.Join(';', VideoExtensions.Select(e => "*." + e));

    public static Task<PreparedMedia> Prepare(string source, Board? board, CancellationToken cancel = default) =>
        Task.Run(() => Kind(source) switch
        {
            MediaKind.Song => PrepareSong(source, cancel),
            MediaKind.Photo => PreparePhoto(source, cancel),
            MediaKind.Video => PrepareVideo(source, board, cancel),
            _ => throw new MediaException("Choose a song (MP3, M4A, AAC, WAV), a photo (JPEG, PNG, HEIC) or a video (MP4, M4V, MOV)."),
        }, cancel);

    // MARK: - Songs (MediaSong.swift)

    private static PreparedMedia PrepareSong(string source, CancellationToken cancel)
    {
        var suffix = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        var size = new FileInfo(source).Length;
        if (size <= 0 || size > Gigabyte) throw new MediaException("Audio files must be smaller than 1 GB.");
        var directory = WorkDirectory("music");
        try
        {
            var audio = Path.Combine(directory, "audio." + suffix);
            Snapshot(source, audio, size, cancel);
            // The source's tags count toward identity, as on the Mac, even those the M4A remux drops.
            var id = Identity(audio);
            var probe = Probe(audio);
            var properties = Tags(probe);
            if (suffix == "aac")
            {
                var converted = Path.Combine(directory, "audio.m4a");
                Run(Tool("ffmpeg"), cancel, "-nostdin", "-v", "error", "-i", audio, "-map", "0:a:0", "-c", "copy",
                    "-map_metadata", "-1", "-fflags", "+bitexact", "-f", "ipod", converted);
                if (new FileInfo(converted).Length is <= 0 or > Gigabyte) throw new MediaException("The converted audio must be smaller than 1 GB.");
                NormalizeGeneratedMovie(converted);
                File.Delete(audio);
                audio = converted;
                probe = Probe(audio);
            }
            var duration = Duration(probe);
            if (Protected(probe) || duration is not (> 0 and <= 86400))
            {
                throw new MediaException("This audio file is protected or has an unsupported duration.");
            }
            var tracks = Streams(probe, "audio").ToList();
            if (tracks.Count != 1) throw new MediaException("The file must contain one audio track.");
            var track = tracks[0];
            var codec = track.Str("codec_name") ?? "";
            var channels = track.Int("channels") ?? 0;
            var rate = int.TryParse(track.Str("sample_rate"), out var r) ? r : 0;
            if (!(codec is "aac" or "mp3" or "alac" || codec.StartsWith("pcm_", StringComparison.Ordinal))
                || channels is < 1 or > 2 || rate is < 8000 or > 48000)
            {
                throw new MediaException("Use AAC, MP3, Apple Lossless or PCM audio, with one or two channels at 8–48 kHz.");
            }
            properties["filename"] = Path.GetFileName(audio);
            properties["duration_ms"] = duration * 1000;
            var title = properties.GetValueOrDefault("title") as string ?? Path.GetFileNameWithoutExtension(source);
            properties["title"] = title;
            string? artwork = null;
            // Unreadable art is dropped, never the song.
            if (Streams(probe, "video").FirstOrDefault(s => s.Disposition("attached_pic")) is { } art)
            {
                try
                {
                    var cover = Path.Combine(directory, "cover.bin");
                    Run(Tool("ffmpeg"), cancel, "-nostdin", "-v", "error", "-i", audio, "-map", $"0:{art.Int("index")}",
                        "-c", "copy", "-frames:v", "1", "-f", "image2", cover);
                    if (new FileInfo(cover).Length <= 16 << 20)
                    {
                        artwork = Path.Combine(directory, "artwork.jpg");
                        using (var image = LoadImage(cover)) WriteBaselineJpeg(image, 640, artwork, ExifOrientation(File.ReadAllBytes(cover)));
                        properties["artwork"] = "artwork.jpg";
                    }
                    File.Delete(cover);
                }
                catch (Exception error) when (error is MediaException or IOException or ArgumentException or ExternalException)
                {
                    artwork = null;
                }
            }
            var metadata = Path.Combine(directory, "metadata.plist");
            File.WriteAllBytes(metadata, Plist.Write(properties));
            return new PreparedMedia(MediaKind.Song, id, directory, audio, title, metadata, artwork);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    // MARK: - Photos (MediaPhoto.swift)

    private static PreparedMedia PreparePhoto(string source, CancellationToken cancel)
    {
        var size = new FileInfo(source).Length;
        if (size <= 0) throw new MediaException("This photo couldn't be read.");
        if (size > 64 << 20) throw new MediaException("This photo is too large. Choose one smaller than 64 MB.");
        var directory = WorkDirectory("photo");
        try
        {
            var readable = source;
            if (Path.GetExtension(source).ToLowerInvariant() is ".heic" or ".heif")
            {
                // GDI+ has no HEIF decoder: ffmpeg decodes the primary image (its tiles put together) to a bitmap.
                readable = Path.Combine(directory, "decoded.bmp");
                Run(Tool("ffmpeg"), cancel, "-nostdin", "-v", "error", "-i", source, "-frames:v", "1", "-c:v", "bmp",
                    "-pix_fmt", "bgr24", "-f", "image2", readable);
            }
            var output = Path.Combine(directory, "image.jpg");
            using (var image = LoadImage(readable))
            {
                if (image.Width > 100000 || image.Height > 100000 || (long)image.Width * image.Height > 100_000_000)
                {
                    throw new MediaException("This photo is too large. Choose one below 100 megapixels.");
                }
                cancel.ThrowIfCancellationRequested();
                WriteBaselineJpeg(image, 2048, output, readable == source ? ExifOrientation(File.ReadAllBytes(source)) : 1);
            }
            if (readable != source) File.Delete(readable);
            return new PreparedMedia(MediaKind.Photo, Identity(output), directory, output, Path.GetFileNameWithoutExtension(source));
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    private static Image LoadImage(string path)
    {
        try
        {
            // Read into memory so the file isn't held open for the image's life.
            return Image.FromStream(new MemoryStream(File.ReadAllBytes(path)), useEmbeddedColorManagement: true, validateImageData: true);
        }
        catch (ArgumentException)
        {
            throw new MediaException("This photo couldn't be read.");
        }
    }

    /// Upright (its EXIF orientation applied), opaque (flattened on white), baseline JPEG no larger than
    /// `maxPixelSize` on either side: what the legacy guests' Photos and Music artwork decoders accept. `orientation`
    /// is the source's EXIF orientation (ExifOrientation), read from its bytes: GDI+ under Wine doesn't report it.
    internal static void WriteBaselineJpeg(Image image, int maxPixelSize, string output, int orientation = 1)
    {
        var flip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(flip);
        var scale = Math.Min(1.0, (double)maxPixelSize / Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // JPEG has no alpha: transparent images flatten against white.
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        // GDI+'s JPEG encoder writes baseline JPEG, with no orientation tag on the new bitmap.
        bitmap.Save(output, codec, parameters);
    }

    // MARK: - Videos (MediaVideo.swift)

    /// The A4 devices (iPad, iPhone 4, iPod touch 4th generation) play H.264 up to 720p.
    public static bool PlaysHD(Board? board) => board?.SoC == SoC.S5L8930;

    private static PreparedMedia PrepareVideo(string source, Board? board, CancellationToken cancel)
    {
        var size = new FileInfo(source).Length;
        if (size <= 0 || size > Gigabyte) throw new MediaException("Videos must be smaller than 1 GB.");
        var directory = WorkDirectory("video");
        try
        {
            // The original can be renamed or edited after the drop: the snapshot keeps the movie and its metadata together.
            var snapshot = Path.Combine(directory, "source" + Path.GetExtension(source).ToLowerInvariant());
            Snapshot(source, snapshot, size, cancel);
            var probe = Probe(snapshot);
            var duration = Duration(probe);
            var videos = Streams(probe, "video").Where(s => !s.Disposition("attached_pic")).ToList();
            var audios = Streams(probe, "audio").ToList();
            if (duration is not (> 0 and <= 86400) || Protected(probe) || videos.Count != 1 || audios.Count > 1)
            {
                throw new MediaException("Choose an unprotected video with one video track and no more than one audio track.");
            }
            var title = Path.GetFileNameWithoutExtension(source);
            if (probe.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var tags)
                && tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } t
                && t.Value.GetString() is { Length: > 0 } named && System.Text.Encoding.UTF8.GetByteCount(named) <= 4096)
            {
                title = named;
            }
            var hd = PlaysHD(board);
            var output = Path.Combine(directory, "video.m4v");
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightTouch", "Converted Videos");
            System.IO.Directory.CreateDirectory(cache);
            var cached = Path.Combine(cache, Identity(snapshot) + (hd ? "-720p" : "") + ".m4v");
            var reused = false;
            if (File.Exists(cached))
            {
                try
                {
                    File.Copy(cached, output);
                    ValidatedDuration(output, duration, board);
                    reused = true;
                }
                catch (MediaException)
                {
                    TryDeleteFile(output);
                    TryDeleteFile(cached);
                }
            }
            if (!reused)
            {
                Convert(snapshot, output, videos[0], audios.FirstOrDefault(), hd, cancel);
                ValidatedDuration(output, duration, board);
                NormalizeGeneratedMovie(output);
                // Encoding can vary between identical runs: reuse the finished conversion so a retry reconciles one
                // library entry, not a second movie. Published with a rename that fails if another import won.
                var publishing = Path.Combine(cache, $".{Guid.NewGuid():N}.m4v");
                File.Copy(output, publishing);
                try
                {
                    File.Move(publishing, cached, overwrite: false);
                }
                catch (IOException) when (File.Exists(cached))
                {
                    File.Delete(publishing);
                    File.Delete(output);
                    File.Copy(cached, output);
                }
            }
            cancel.ThrowIfCancellationRequested();
            var exported = ValidatedDuration(output, duration, board);
            var metadata = Path.Combine(directory, "metadata.plist");
            File.WriteAllBytes(metadata, Plist.Write(new Dictionary<string, object>
            {
                ["filename"] = "video.m4v",
                ["kind"] = "feature-movie",
                ["title"] = title,
                ["duration_ms"] = exported * 1000,
            }));
            File.Delete(snapshot);
            return new PreparedMedia(MediaKind.Video, Identity(output), directory, output, title, metadata);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    private static void Convert(string source, string output, ProbeStream video, ProbeStream? audio, bool hd, CancellationToken cancel)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-i", source, "-map", $"0:{video.Int("index")}" };
        if (audio != null) args.AddRange(["-map", $"0:{audio.Int("index")}"]);
        if (Playable(video, audio, hd))
        {
            args.AddRange(["-c", "copy"]);
        }
        else
        {
            var (w, h) = hd ? (1280, 720) : (640, 480);
            args.AddRange([
                "-vf", $"scale=w={w}:h={h}:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                "-fpsmax", "30", "-pix_fmt", "nv12",
                "-c:v", "h264_mf", "-profile:v", hd ? "77" : "66", "-scenario", "camera_record",
                "-rate_control", "pc_vbr", "-b:v", hd ? "4000k" : "1500k", "-maxrate", hd ? "5000k" : "2000k",
            ]);
            if (audio != null)
            {
                args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "44100"]);
            }
        }
        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-movflags", "+faststart",
            "-fs", Gigabyte.ToString(CultureInfo.InvariantCulture), "-f", "ipod", output]);
        try
        {
            Run(Tool("ffmpeg"), cancel, [.. args]);
        }
        catch (MediaException error)
        {
            Console.Error.WriteLine($"LightTouch: video conversion failed: {error.Message}");
            throw new MediaException("This video couldn't be converted for the device. "
                + "Windows' H.264 encoder (Media Foundation) is needed; Windows N editions get it with the Media Feature Pack.");
        }
    }

    private static double ValidatedDuration(string file, double expected, Board? board)
    {
        var size = new FileInfo(file).Length;
        if (size <= 0) throw new MediaException("The prepared video couldn't be read.");
        if (size > Gigabyte) throw new MediaException("The prepared video is too large. Choose a shorter video.");
        var probe = Probe(file);
        var duration = Duration(probe);
        if (Math.Abs(duration - expected) >= 0.2 || Streams(probe, "video").Count() != 1)
        {
            throw new MediaException($"The whole video couldn't be converted for the {board?.ShortName ?? "device"}. Try a shorter video.");
        }
        return duration;
    }

    // MARK: - ffprobe and ffmpeg

    private static JsonElement Probe(string file)
    {
        var output = Run(Tool("ffprobe"), CancellationToken.None, "-v", "error", "-print_format", "json",
            "-show_format", "-show_streams", file);
        try
        {
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new MediaException("This file couldn't be read.");
        }
    }

    /// ffmpeg.exe and ffprobe.exe beside the app (LTW_MEDIA_TOOLS overrides, for tests).
    internal static string Tool(string name)
    {
        var directory = Environment.GetEnvironmentVariable("LTW_MEDIA_TOOLS") ?? AppContext.BaseDirectory;
        foreach (var candidate in new[] { name + ".exe", name })
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path)) return path;
        }
        throw new MediaException($"{name}.exe is missing from the app's folder. Reinstall Light Touch.");
    }

    /// Runs `tool` and returns its standard output; MediaException with its error output when it fails.
    private static string Run(string tool, CancellationToken cancel, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new MediaException($"{Path.GetFileName(tool)} didn't start.");
        var error = process.StandardError.ReadToEndAsync(cancel);
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        try
        {
            process.WaitForExitAsync(cancel).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var message = error.GetAwaiter().GetResult().Trim();
            throw new MediaException(message.Length == 0 ? $"{Path.GetFileName(tool)} failed ({process.ExitCode})." : message);
        }
        return output.GetAwaiter().GetResult();
    }

    // MARK: - Files

    private static string WorkDirectory(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ltw-{kind}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// A private copy of exactly `size` bytes; a file that changes while it's copied is refused.
    private static void Snapshot(string source, string destination, long size, CancellationToken cancel)
    {
        using var input = File.OpenRead(source);
        using var output = File.Create(destination);
        var buffer = new byte[1 << 16];
        long copied = 0;
        while (copied < size)
        {
            cancel.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size - copied));
            if (read == 0) throw new MediaException("The file changed while it was being prepared.");
            output.Write(buffer, 0, read);
            copied += read;
        }
        if (input.Read(buffer, 0, 1) != 0) throw new MediaException("The file changed while it was being prepared.");
    }

    private static void TryDelete(string directory)
    {
        try { System.IO.Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
