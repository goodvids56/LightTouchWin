// NTFS sparse files for the NAND chip files: an image store's cs<N>.pages are gigabytes long and mostly holes (blank
// NAND). On the Mac the live NAND is an APFS clone of the base's; here it is a sparse copy of the allocated ranges, so
// a device costs what its data holds rather than every chip's full size.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LightTouch.Device;

internal static partial class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint FsctlQueryAllocatedRanges = 0x000940CF;
    private const int ErrorMoreData = 234;
    private const int Chunk = 1 << 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct AllocatedRange
    {
        public long Offset;
        public long Length;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, int inputSize,
        void* output, int outputSize, out int returned, IntPtr overlapped);

    /// Creates (or truncates) `path` as a sparse file of `length` bytes, all of it a hole.
    public static FileStream Create(string path, long length)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        MarkSparse(stream.SafeFileHandle);
        stream.SetLength(length);
        return stream;
    }

    /// Best effort: a volume without sparse support (FAT) just stores the zeros.
    private static unsafe void MarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = DeviceIoControl(handle, FsctlSetSparse, null, 0, null, 0, out _, IntPtr.Zero);
    }

    /// The allocated ranges of `handle`'s file; the whole file where the volume can't say.
    private static unsafe List<(long Offset, long Length)> Allocated(SafeFileHandle handle, long length)
    {
        if (!OperatingSystem.IsWindows()) return [(0, length)];
        var result = new List<(long, long)>();
        var query = new AllocatedRange { Offset = 0, Length = length };
        var buffer = new AllocatedRange[256];
        while (query.Length > 0)
        {
            bool ok;
            int returned;
            fixed (AllocatedRange* output = buffer)
            {
                ok = DeviceIoControl(handle, FsctlQueryAllocatedRanges, &query, sizeof(AllocatedRange), output,
                    buffer.Length * sizeof(AllocatedRange), out returned, IntPtr.Zero);
            }
            var error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (!ok && error != ErrorMoreData) return [(0, length)];
            var count = returned / sizeof(AllocatedRange);
            for (var i = 0; i < count; i++) result.Add((buffer[i].Offset, buffer[i].Length));
            if (ok || count == 0) break;
            var last = buffer[count - 1];
            var end = last.Offset + last.Length;
            query = new AllocatedRange { Offset = end, Length = length - end };
        }
        return result;
    }

    /// Copies `source` to `destination` (created, sparse): only the allocated ranges are read, and an all-zero chunk
    /// within them stays a hole.
    public static void Copy(string source, string destination, Action<long>? copied = null)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var length = input.Length;
        using var output = Create(destination, length);
        var buffer = new byte[Chunk];
        foreach (var (offset, rangeLength) in Allocated(input.SafeFileHandle, length))
        {
            for (var at = offset; at < offset + rangeLength;)
            {
                var want = (int)Math.Min(Chunk, offset + rangeLength - at);
                input.Position = at;
                input.ReadExactly(buffer, 0, want);
                if (buffer.AsSpan(0, want).ContainsAnyExcept((byte)0))
                {
                    output.Position = at;
                    output.Write(buffer, 0, want);
                }
                at += want;
                copied?.Invoke(want);
            }
        }
        output.Flush(flushToDisk: true);
    }

    /// Whether the file is worth a sparse copy: the NAND chip files.
    public static bool IsChipFile(string path) => path.EndsWith(".pages", StringComparison.OrdinalIgnoreCase);

    /// Copies a directory tree, the chip files sparse.
    public static void CopyTree(string source, string destination, Action<long>? copied = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (Path.GetFileName(file) is ".DS_Store") continue;
            if (IsChipFile(file))
            {
                Copy(file, target, copied);
            }
            else
            {
                File.Copy(file, target, overwrite: true);
                copied?.Invoke(new FileInfo(file).Length);
            }
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
    }

    /// Clears read-only attributes under `path` and deletes it.
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
