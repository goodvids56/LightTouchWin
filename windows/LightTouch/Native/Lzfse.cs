// lzfse.dll (windows/scripts/build-media.sh): Apple's LZFSE, which Light Touch for Mac's Guest/guest.aar is compressed
// with (GuestArchive).

using System.Runtime.InteropServices;

namespace LightTouch.Native;

internal static unsafe partial class Lzfse
{
    [LibraryImport("lzfse", EntryPoint = "lzfse_decode_buffer")]
    private static partial nuint DecodeBuffer(byte* destination, nuint destinationSize, byte* source, nuint sourceSize,
        IntPtr scratch);

    /// `source` decoded into exactly `size` bytes.
    public static byte[] Decode(ReadOnlySpan<byte> source, int size)
    {
        var output = new byte[size];
        fixed (byte* d = output)
        fixed (byte* s = source)
        {
            if (DecodeBuffer(d, (nuint)size, s, (nuint)source.Length, IntPtr.Zero) != (nuint)size)
            {
                throw new InvalidDataException("A compressed block of the guest archive didn't decode.");
            }
        }
        return output;
    }
}
