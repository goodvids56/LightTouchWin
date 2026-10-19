// Light Touch for Mac's Resources/Guest/guest.aar: the guest binaries the Mac app ships (FirmwareSchema's
// GuestArchive.swift unpacks it with AppleArchive). It is an Apple Archive (AA01 entries) in a pbze stream: "pbze", a
// big-endian block size, then blocks of a big-endian raw size, compressed size and that many bytes, LZFSE-compressed
// unless the two sizes match. Each entry is "AA01", a little-endian header size and fields of a three-letter key and a
// type letter (TYP1 the entry's kind, PATP its path, MOD2 its mode, DATA/DATB the size of the data after the header).
// The Windows app takes tools/ from it: the iPod media helpers (itmedia, itphoto) media import uploads.

using System.Buffers.Binary;
using System.Text;
using LightTouch.Native;

namespace LightTouch.Library;

internal static class GuestArchive
{
    /// Unpacks the entries of `archive` whose path `want` accepts into `target` (replaced as a whole).
    public static int Extract(Stream archive, string target, Func<string, bool> want)
    {
        var staging = target + ".unpacking";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var count = 0;
        try
        {
            using var stream = new PbzeStream(archive);
            foreach (var entry in Entries(stream))
            {
                // The archive's own root comes first, with an empty path.
                if (entry.Path.Length == 0 || !want(entry.Path))
                {
                    entry.Skip();
                    continue;
                }
                var path = Path.GetFullPath(Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The guest archive names a path outside itself: {entry.Path}");
                }
                switch (entry.Type)
                {
                    case 'D':
                        Directory.CreateDirectory(path);
                        break;
                    case 'F':
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (var output = File.Create(path)) entry.CopyTo(output);
                        count++;
                        break;
                    default:
                        entry.Skip();
                        break;
                }
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            return count;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    internal sealed class Entry(Stream stream, char type, string path, long size)
    {
        public char Type { get; } = type;
        public string Path { get; } = path;
        public long Size { get; } = size;
        private long left = size;

        public void CopyTo(Stream output)
        {
            var buffer = new byte[1 << 16];
            while (left > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) throw new EndOfStreamException("The guest archive ends inside a file.");
                output.Write(buffer, 0, read);
                left -= read;
            }
        }

        public void Skip() => CopyTo(Stream.Null);
    }

    /// The archive's entries in order; each must be read or skipped before the next.
    internal static IEnumerable<Entry> Entries(Stream stream)
    {
        var fixedPart = new byte[6];
        while (true)
        {
            var got = ReadFully(stream, fixedPart);
            if (got == 0) yield break;
            if (got < 6 || !fixedPart.AsSpan(0, 4).SequenceEqual("AA01"u8)) throw new InvalidDataException("Not an Apple Archive entry.");
            var header = new byte[BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(4)) - 6];
            if (ReadFully(stream, header) < header.Length) throw new EndOfStreamException("The guest archive ends inside a header.");
            char type = '?';
            var path = "";
            long data = 0;
            for (var p = 0; p < header.Length;)
            {
                var key = Encoding.ASCII.GetString(header, p, 3);
                var kind = (char)header[p + 3];
                p += 4;
                switch (kind)
                {
                    case '*':
                        break;
                    case '1' or '2' or '4' or '8':
                        var width = kind - '0';
                        ulong value = 0;
                        for (var i = width - 1; i >= 0; i--) value = value << 8 | header[p + i];
                        if (key == "TYP") type = (char)value;
                        p += width;
                        break;
                    case 'P':
                        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(p));
                        if (key == "PAT") path = Encoding.UTF8.GetString(header, p + 2, length);
                        p += 2 + length;
                        break;
                    case 'A' or 'B' or 'C':
                        var sizeWidth = kind switch { 'A' => 2, 'B' => 4, _ => 8 };
                        long size = 0;
                        for (var i = sizeWidth - 1; i >= 0; i--) size = size << 8 | header[p + i];
                        data += size;
                        p += sizeWidth;
                        break;
                    case 'S':
                        p += 8;
                        break;
                    case 'T':
                        p += 12;
                        break;
                    case 'F' or 'G' or 'H' or 'I' or 'J':
                        p += kind switch { 'F' => 4, 'G' => 20, 'H' => 32, 'I' => 48, _ => 64 };
                        break;
                    default:
                        throw new InvalidDataException($"The guest archive has a field this app can't read ({key}{kind}).");
                }
            }
            var entry = new Entry(stream, type, path, data);
            yield return entry;
            entry.Skip();
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// A pbze stream, decoded a block at a time.
    internal sealed class PbzeStream : Stream
    {
        private readonly Stream inner;
        private byte[] block = [];
        private int position;

        public PbzeStream(Stream inner)
        {
            this.inner = inner;
            var magic = new byte[12];
            if (ReadFully(inner, magic) < 12 || !magic.AsSpan(0, 4).SequenceEqual("pbze"u8))
            {
                throw new InvalidDataException("Not an LZFSE-compressed Apple Archive.");
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position == block.Length && !NextBlock()) return 0;
            var n = Math.Min(count, block.Length - position);
            Buffer.BlockCopy(block, position, buffer, offset, n);
            position += n;
            return n;
        }

        private bool NextBlock()
        {
            var sizes = new byte[16];
            var got = ReadFully(inner, sizes);
            if (got == 0) return false;
            if (got < 16) throw new EndOfStreamException("The guest archive ends inside a block header.");
            var raw = checked((int)BinaryPrimitives.ReadUInt64BigEndian(sizes));
            var compressed = checked((int)BinaryPrimitives.ReadUInt64BigEndian(sizes.AsSpan(8)));
            var data = new byte[compressed];
            if (ReadFully(inner, data) < compressed) throw new EndOfStreamException("The guest archive ends inside a block.");
            block = raw == compressed ? data : Lzfse.Decode(data, raw);
            position = 0;
            return block.Length > 0 || NextBlock();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
