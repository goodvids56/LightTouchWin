// A recording of the running device's screen and sound, as LightTouchCore's ScreenRecordingSession and
// ScreenMovieWriter make one on the Mac. The device window's frames, fitted into a canvas the size of the first one
// (a turned device is letterboxed), go to ffmpeg at a steady 30 fps on the guest audio capture's clock; the mixer's
// 44100 Hz stereo packets are laid out at their capture times, with silence through the gaps, as the Mac's writer
// does. Stopping muxes the two into a movie: H.264 from Windows' own encoder (h264_mf) and AAC in an .mp4, or
// Motion-JPEG in a .mov where Windows has no H.264 encoder (an N edition without the Media Feature Pack, Wine).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using LightTouch.Native;

namespace LightTouch.Services;

internal sealed class ScreenRecorder : IDisposable
{
    private const int Fps = 30;
    private readonly string work = Path.Combine(Path.GetTempPath(), $"ltw-recording-{Guid.NewGuid():N}");
    private readonly int width, height;
    private readonly bool h264;
    private readonly Process video;
    private readonly BlockingCollection<byte[]> frames = new(boundedCapacity: Fps);
    private readonly Thread videoWriter;
    private readonly Thread? audioReader;
    private readonly ulong audio;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Bitmap canvas;
    private long framesQueued;
    private volatile bool stopping;
    private string? failure;

    /// Starts a recording of frames `frameWidth` × `frameHeight` (the first frame's size); `withAudio` captures the
    /// guest's sound too.
    public ScreenRecorder(int frameWidth, int frameHeight, bool withAudio)
    {
        Directory.CreateDirectory(work);
        width = frameWidth + (frameWidth & 1);
        height = frameHeight + (frameHeight & 1);
        canvas = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        h264 = EncodesH264();
        video = Start(MediaPreparation.Tool("ffmpeg"), [
            "-nostdin", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-framerate", $"{Fps}", "-i", "pipe:0",
            .. h264
                ? (string[])["-c:v", "h264_mf", "-pix_fmt", "nv12", "-b:v", $"{Math.Max(2000, width * height / 40)}k", "-f", "mp4", VideoFile]
                : ["-c:v", "mjpeg", "-pix_fmt", "yuvj420p", "-q:v", "3", "-f", "mov", VideoFile],
        ], redirectInput: true);
        videoWriter = new Thread(WriteVideo) { IsBackground = true, Name = "recording video" };
        videoWriter.Start();
        if (withAudio && (audio = Qemu.AudioCaptureStart()) != 0)
        {
            audioReader = new Thread(ReadAudio) { IsBackground = true, Name = "recording audio" };
            audioReader.Start();
        }
    }

    private string VideoFile => Path.Combine(work, h264 ? "video.mp4" : "video.mov");
    private string AudioFile => Path.Combine(work, "audio.pcm");
    public string Extension => h264 ? ".mp4" : ".mov";

    /// Seconds since the recording began: the audio capture's clock when there is one, so picture and sound agree.
    public double Seconds
    {
        get
        {
            if (audio != 0 && Qemu.AudioCaptureTime(audio) is var t and >= 0) return t;
            return clock.Elapsed.TotalSeconds;
        }
    }

    /// The device window's current frame, at this moment of the recording; called at the window's frame rate.
    public void Frame(Bitmap? frame)
    {
        if (stopping || frame == null) return;
        var due = (long)(Seconds * Fps) + 1 - framesQueued;
        if (due <= 0) return;
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            var scale = Math.Min((float)width / frame.Width, (float)height / frame.Height);
            var w = frame.Width * scale;
            var h = frame.Height * scale;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(frame, (width - w) / 2, (height - h) / 2, w, h);
        }
        var bytes = new byte[width * height * 4];
        var data = canvas.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * width * 4, width * 4);
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }
        // A late frame stands in for the ones it missed, so the movie keeps the recording's time.
        for (var i = 0; i < Math.Min(due, Fps); i++)
        {
            if (!frames.TryAdd(bytes)) break;
            framesQueued++;
        }
        if (due > Fps) framesQueued += due - Fps;
    }

    private void WriteVideo()
    {
        var input = video.StandardInput.BaseStream;
        try
        {
            foreach (var frame in frames.GetConsumingEnumerable()) input.Write(frame);
        }
        catch (IOException)
        {
            failure ??= "The video encoder stopped.";
        }
        finally
        {
            try { input.Close(); } catch (IOException) { }
        }
    }

    /// The mixer's packets at their capture times; silence fills the gaps.
    private unsafe void ReadAudio()
    {
        using var output = File.Create(AudioFile);
        var buffer = new byte[16384];
        long written = 0;
        var idle = false;
        while (true)
        {
            int n;
            double seconds;
            fixed (byte* p = buffer) n = Qemu.AudioCaptureRead(audio, p, buffer.Length, out seconds);
            if (n < 0) break;
            if (double.IsFinite(seconds) && seconds >= 0)
            {
                var target = (long)Math.Round(seconds * 44100);
                while (written < target)
                {
                    var silence = (int)Math.Min(4410, target - written);
                    output.Write(new byte[silence * 4]);
                    written += silence;
                }
                if (n > 0)
                {
                    var skip = (int)Math.Min(n / 4, Math.Max(0, written - target));
                    output.Write(buffer, skip * 4, n - skip * 4);
                    written += n / 4 - skip;
                }
            }
            if (n == 0)
            {
                if (idle && stopping) break;
                idle = stopping;
                Thread.Sleep(20);
            }
        }
    }

    /// Ends the recording and writes the movie to `destination` (its extension is Extension's).
    public async Task<string> Finish(string destination)
    {
        var end = Seconds;
        stopping = true;
        // The capture's queued packets stay readable after its stop; the reader drains them.
        if (audio != 0) Qemu.AudioCaptureStop(audio);
        frames.CompleteAdding();
        await Task.Run(() => videoWriter.Join());
        if (audioReader != null) await Task.Run(audioReader.Join);
        await video.WaitForExitAsync();
        var error = await video.StandardError.ReadToEndAsync();
        if (video.ExitCode != 0 || !File.Exists(VideoFile))
        {
            Console.Error.WriteLine($"LightTouch: recording: ffmpeg exited with {video.ExitCode}: {error.Trim()}");
            throw new MediaException(error.Trim() is { Length: > 0 } e ? e : failure ?? "The video encoder failed.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (audio != 0 && File.Exists(AudioFile) && new FileInfo(AudioFile).Length > 0)
        {
            using var mux = Start(MediaPreparation.Tool("ffmpeg"), [
                "-nostdin", "-v", "error", "-y", "-i", VideoFile, "-f", "s16le", "-ar", "44100", "-ac", "2", "-i", AudioFile,
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
                "-t", end.ToString("F3", CultureInfo.InvariantCulture), "-movflags", "+faststart", destination,
            ]);
            await mux.WaitForExitAsync();
            if (mux.ExitCode == 0) return destination;
            Console.Error.WriteLine($"LightTouch: recording: the sound couldn't be added: {(await mux.StandardError.ReadToEndAsync()).Trim()}");
        }
        File.Move(VideoFile, destination, overwrite: true);
        return destination;
    }

    /// Whether this PC's ffmpeg can use Windows' H.264 encoder: one frame, tried.
    private bool EncodesH264()
    {
        var probe = Path.Combine(work, "probe.mp4");
        try
        {
            using var process = Start(MediaPreparation.Tool("ffmpeg"), [
                "-nostdin", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", "64x64", "-i", "pipe:0",
                "-frames:v", "1", "-c:v", "h264_mf", "-pix_fmt", "nv12", "-f", "mp4", probe,
            ], redirectInput: true);
            process.StandardInput.BaseStream.Write(new byte[64 * 64 * 4]);
            process.StandardInput.Close();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            try { File.Delete(probe); } catch (IOException) { }
        }
    }

    private static Process Start(string tool, string[] arguments, bool redirectInput = false)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new MediaException("ffmpeg didn't start.");
    }

    public void Dispose()
    {
        stopping = true;
        if (!frames.IsAddingCompleted) frames.CompleteAdding();
        if (audio != 0) Qemu.AudioCaptureStop(audio);
        try { if (!video.HasExited) video.Kill(); } catch (InvalidOperationException) { }
        video.Dispose();
        canvas.Dispose();
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
