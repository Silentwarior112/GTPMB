using System.IO.Compression;
using System.Text;

namespace GTPMB.Core.Compression;

/// <summary>
/// One gzip member (RFC 1952) with the header fields Polyphony's build tools left in the PMB
/// entries: the original file name (FNAME), timestamp (MTIME), XFL and OS byte. The deflate
/// stream itself is standard; a rebuilt member carries a fresh stream (matching PDI's 1999-era
/// zlib byte for byte is not possible, and the game only inflates it).
/// </summary>
public sealed class GzipMember
{
    public required byte[] Content { get; init; }

    /// <summary>FNAME, Latin-1, without a path as PDI stored it ("hoge_..." dev names).</summary>
    public string? FileName { get; init; }

    /// <summary>MTIME: seconds since the Unix epoch, 0 when absent.</summary>
    public uint MTime { get; init; }

    public byte ExtraFlags { get; init; }

    /// <summary>OS byte; PDI's files say 0x0B (NTFS).</summary>
    public byte OperatingSystem { get; init; } = 0x0B;
}

public static class Gzip
{
    public static bool IsGzip(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 8;

    private const byte FlagHeaderCrc = 2, FlagExtra = 4, FlagName = 8, FlagComment = 16;

    /// <summary>
    /// Reads the member spanning [<paramref name="offset"/>, <paramref name="end"/>) - the caller
    /// knows the span from the surrounding container. The CRC32 and ISIZE trailer are verified.
    /// </summary>
    public static GzipMember Read(byte[] data, int offset, int end)
    {
        if (!IsGzip(data.AsSpan(offset)))
            throw new InvalidDataException($"Not a gzip member at 0x{offset:X}.");
        if (end - offset < 18)
            throw new InvalidDataException($"gzip member at 0x{offset:X} is truncated ({end - offset} bytes).");

        byte flags = data[offset + 3];
        uint mtime = BitConverter.ToUInt32(data, offset + 4);
        byte xfl = data[offset + 8], os = data[offset + 9];
        int p = offset + 10;
        if ((flags & FlagExtra) != 0)
            p += 2 + BitConverter.ToUInt16(data, p);
        string? name = null;
        if ((flags & FlagName) != 0)
        {
            int nul = Array.IndexOf(data, (byte)0, p, end - p);
            if (nul < 0)
                throw new InvalidDataException($"gzip FNAME at 0x{p:X} is unterminated.");
            name = Encoding.Latin1.GetString(data, p, nul - p);
            p = nul + 1;
        }
        if ((flags & FlagComment) != 0)
        {
            int nul = Array.IndexOf(data, (byte)0, p, end - p);
            if (nul < 0)
                throw new InvalidDataException($"gzip FCOMMENT at 0x{p:X} is unterminated.");
            p = nul + 1;
        }
        if ((flags & FlagHeaderCrc) != 0)
            p += 2;

        using var deflate = new DeflateStream(new MemoryStream(data, p, end - p - 8), CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        byte[] content = output.ToArray();

        uint crc = BitConverter.ToUInt32(data, end - 8);
        uint isize = BitConverter.ToUInt32(data, end - 4);
        if (isize != (uint)content.Length)
            throw new InvalidDataException($"gzip ISIZE {isize} != inflated size {content.Length} at 0x{offset:X}.");
        if (crc != Crc32.Compute(content))
            throw new InvalidDataException($"gzip CRC mismatch at 0x{offset:X}.");

        return new GzipMember
        {
            Content = content,
            FileName = name,
            MTime = mtime,
            ExtraFlags = xfl,
            OperatingSystem = os,
        };
    }

    /// <summary>Writes a member with a fresh deflate stream and the metadata this member carries.</summary>
    public static byte[] Write(GzipMember member)
    {
        using var output = new MemoryStream();
        byte flags = member.FileName is null ? (byte)0 : FlagName;
        Span<byte> header = [0x1F, 0x8B, 8, flags, 0, 0, 0, 0, member.ExtraFlags, member.OperatingSystem];
        BitConverter.TryWriteBytes(header[4..8], member.MTime);
        output.Write(header);
        if (member.FileName is not null)
        {
            byte[] name = Encoding.Latin1.GetBytes(member.FileName);
            output.Write(name);
            output.WriteByte(0);
        }
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(member.Content);
        Span<byte> trailer = stackalloc byte[8];
        BitConverter.TryWriteBytes(trailer, Crc32.Compute(member.Content));
        BitConverter.TryWriteBytes(trailer[4..], (uint)member.Content.Length);
        output.Write(trailer);
        return output.ToArray();
    }
}

/// <summary>CRC-32 (the gzip / PNG polynomial, reflected 0xEDB88320).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
