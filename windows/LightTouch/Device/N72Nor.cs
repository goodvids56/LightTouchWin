// The iPod touch 2G's 1 MiB SPI NOR, reidentified for a new unit identity (FirmwareKit's N72NOR.reidentify): only
// the SysCfg block and the nvram bank carry the unit's identity.

using System.Text;

namespace LightTouch.Device;

internal static class N72Nor
{
    private const int Size = 0x100000, SysCfg = 0x4000, Nvram = 0xFC000, NvramBank = 0x2000, ImageStart = 0x40 * 0x200;

    public static byte[] Reidentify(byte[] nor, UnitIdentity id)
    {
        if (nor.Length != Size) throw new InvalidDataException($"nor.bin is {nor.Length} bytes, not {Size}");
        var b = (byte[])nor.Clone();
        var fresh = Base(id);
        Array.Copy(fresh, SysCfg, b, SysCfg, ImageStart - SysCfg);
        Array.Copy(fresh, Nvram, b, Nvram, Size - Nvram);
        return b;
    }

    /// synth_base: IMG2 + SysCfg + nvram for the identity, an empty image area.
    private static byte[] Base(UnitIdentity id)
    {
        var nor = new byte[Size];
        Encoding.ASCII.GetBytes("2GMI").CopyTo(nor, 0);
        Put32(nor, 4, 0x40);
        Put32(nor, 8, 0);
        Put32(nor, 12, 0x200);
        WriteSysCfg(nor, id);
        var bank = Bank([
            ("debug-uarts", "1"),
            ("btaddr", (id["bt-mac"] ?? "").ToUpperInvariant()),
            ("wifiaddr", (id["wifi-mac"] ?? "").ToUpperInvariant()),
        ]);
        bank.CopyTo(nor, Nvram);
        Put32(nor, 0x30, Crc32(nor.AsSpan(0, 0x30)));
        return nor;
    }

    private static void WriteSysCfg(byte[] nor, UnitIdentity id)
    {
        Encoding.ASCII.GetBytes("gfCS").CopyTo(nor, SysCfg);
        uint[] header = [0xC8, 0x2000, 0x0001_0001, 0, 4];
        for (var i = 0; i < header.Length; i++) Put32(nor, SysCfg + 4 + 4 * i, header[i]);
        (string Tag, string Key)[] entries =
            [("Mod#", "model-number"), ("Regn", "region-info"), ("SrNm", "serial-number"), ("Batt", "battery-serial")];
        for (var i = 0; i < entries.Length; i++)
        {
            var value = Encoding.UTF8.GetBytes(id[entries[i].Key] ?? "");
            if (value.Length > 16) throw new InvalidDataException($"{entries[i].Key} is longer than a SysCfg value");
            var o = SysCfg + 0x18 + i * 0x14;
            var tag = Encoding.ASCII.GetBytes(entries[i].Tag);
            Array.Reverse(tag);
            tag.CopyTo(nor, o);
            Array.Clear(nor, o + 4, 16);
            value.CopyTo(nor, o + 4);
        }
    }

    /// A CHRP nvram partition header: signature, checksum, length in 16-byte units, 12-byte name.
    private static byte[] Chrp(byte signature, int length, string name)
    {
        var h = new byte[16];
        h[0] = signature;
        h[2] = (byte)((length / 16) & 0xFF);
        h[3] = (byte)((length / 16) >> 8);
        Encoding.ASCII.GetBytes(name).CopyTo(h, 4);
        var s = 0;
        foreach (var b in h.Take(1).Concat(h.Skip(2)))
        {
            s += b;
            if (s > 0xFF) s = (s & 0xFF) + 1;
        }
        h[1] = (byte)s;
        return h;
    }

    /// One 8 KiB bank as iBoot writes it: "nvram", "common", an empty "APL,OSXPanic" and the free space.
    private static byte[] Bank((string Key, string Value)[] common)
    {
        var bank = new byte[NvramBank];
        Chrp(0x5A, 0x20, "nvram").CopyTo(bank, 0);
        Put32(bank, 0x14, 0x10);
        var body = common.SelectMany(kv => Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}").Append((byte)0)).ToArray();
        if (body.Length > 0x800 - 16) throw new InvalidDataException("nvram common partition overflows");
        Chrp(0x70, 0x800, "common").CopyTo(bank, 0x20);
        body.CopyTo(bank, 0x30);
        Chrp(0xA1, 0x810, "APL,OSXPanic").CopyTo(bank, 0x820);
        Chrp(0x7F, NvramBank - 0x1030, new string('w', 12)).CopyTo(bank, 0x1030);
        Put32(bank, 0x10, Adler32(bank.AsSpan(0x14)));
        return bank;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return b << 16 | a;
    }

    /// zlib's CRC-32.
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in data)
        {
            crc ^= x;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static void Put32(byte[] b, int o, uint v)
    {
        for (var k = 0; k < 4; k++) b[o + k] = (byte)(v >> (8 * k));
    }
}
