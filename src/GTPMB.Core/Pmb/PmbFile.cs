using GTPMB.Core.Compression;

namespace GTPMB.Core.Pmb;

/// <summary>One embedded file of a PMB (in practice always a single-texture Tex1 ".img").</summary>
public sealed class PmbEntry
{
    /// <summary>The file itself, decompressed.</summary>
    public required byte[] Content { get; set; }

    /// <summary>Stored gzip-wrapped (true for most entries; pause / logger keep raw Tex1).</summary>
    public bool Compressed { get; set; }

    /// <summary>gzip FNAME / MTIME / XFL / OS, kept so a rebuild writes the same header fields.</summary>
    public string? GzipName { get; set; }
    public uint GzipMTime { get; set; }
    public byte GzipXfl { get; set; }
    public byte GzipOs { get; set; } = 0x0B;

    /// <summary>
    /// The exact bytes the entry had on disk (gzip member or raw content). While the content is
    /// untouched a rebuild splices these, so an unedited archive comes out byte-identical; any
    /// edit clears them and the entry is re-encoded.
    /// </summary>
    public byte[]? StoredBytes { get; set; }

    public GzipMember ToGzipMember() => new()
    {
        Content = Content,
        FileName = GzipName,
        MTime = GzipMTime,
        ExtraFlags = GzipXfl,
        OperatingSystem = GzipOs,
    };
}

/// <summary>
/// A GT3 menu "PMB" file: header, page structures (kept verbatim - every offset in them is
/// absolute and nothing in them points at the file data), a file table and the embedded files.
///
/// Layout (all little-endian):
///   0x00  u32 pageCount, u32 pageTableOffset (= 0x10), u32 fileCount, u32 fileTableOffset
///   0x10  pageCount * u32 page offsets, then the page structures, back to back
///   fileTable: fileCount * { u32 uncompressedSize, u32 dataOffset }
///   file data in table order: gzip members 4-aligned, raw files 16-aligned, gaps and the
///   tail padded with 'P' (0x50), file ends at align4(last entry's end).
/// A PMB with no content at all is a zero-length file on disk (arcade0.pmb, gt_mode0.pmb).
/// </summary>
public sealed class PmbFile
{
    public const int HeaderSize = 16;

    /// <summary>Zero-length on disk; nothing else is set.</summary>
    public bool Empty { get; init; }

    public int PageCount { get; set; }

    /// <summary>Bytes [0x10, fileTableOffset): the page offset table and every page structure.</summary>
    public byte[] PagesBlob { get; set; } = [];

    public List<PmbEntry> Entries { get; } = [];

    public static PmbFile Read(byte[] data)
    {
        if (data.Length == 0)
            return new PmbFile { Empty = true };
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"PMB too short ({data.Length} bytes).");

        var reader = new ByteReader(data);
        int pageCount = reader.ReadInt32();
        int pageTableOffset = reader.ReadInt32();
        int fileCount = reader.ReadInt32();
        int fileTableOffset = reader.ReadInt32();

        if (pageTableOffset != HeaderSize)
            throw new InvalidDataException($"Unexpected page table offset 0x{pageTableOffset:X} (a PMB has it at 0x10).");
        if (fileTableOffset < HeaderSize || fileTableOffset > data.Length)
            throw new InvalidDataException($"File table offset 0x{fileTableOffset:X} is outside the file.");
        if (fileCount < 0 || fileTableOffset + 8L * fileCount > data.Length)
            throw new InvalidDataException($"File table with {fileCount} entries runs past the end.");

        var pmb = new PmbFile
        {
            PageCount = pageCount,
            PagesBlob = data[HeaderSize..fileTableOffset],
        };

        // {size, offset} pairs; the data runs contiguously from the table's end to the file's end.
        var sizes = new int[fileCount];
        var offsets = new int[fileCount];
        reader.Position = fileTableOffset;
        for (int i = 0; i < fileCount; i++)
        {
            sizes[i] = reader.ReadInt32();
            offsets[i] = reader.ReadInt32();
        }

        for (int i = 0; i < fileCount; i++)
        {
            int offset = offsets[i];
            int end = i + 1 < fileCount ? offsets[i + 1] : data.Length;
            if (offset < fileTableOffset + 8 * fileCount || end > data.Length || end < offset)
                throw new InvalidDataException($"File {i} spans 0x{offset:X}..0x{end:X}, outside the data region.");

            PmbEntry entry;
            if (Gzip.IsGzip(data.AsSpan(offset)))
            {
                int memberEnd = StripPadding(data, offset, end);
                GzipMember member = Gzip.Read(data, offset, memberEnd);
                entry = new PmbEntry
                {
                    Content = member.Content,
                    Compressed = true,
                    GzipName = member.FileName,
                    GzipMTime = member.MTime,
                    GzipXfl = member.ExtraFlags,
                    GzipOs = member.OperatingSystem,
                    StoredBytes = data[offset..memberEnd],
                };
            }
            else
            {
                if (offset + sizes[i] > end)
                    throw new InvalidDataException($"Raw file {i} ({sizes[i]} bytes at 0x{offset:X}) runs past its slot.");
                byte[] content = data[offset..(offset + sizes[i])];
                entry = new PmbEntry { Content = content, Compressed = false, StoredBytes = content };
            }

            if (entry.Content.Length != sizes[i])
                throw new InvalidDataException($"File {i}: table says {sizes[i]} bytes, entry holds {entry.Content.Length}.");
            pmb.Entries.Add(entry);
        }

        return pmb;
    }

    /// <summary>The entry's end with the trailing 'P' padding removed (a gzip trailer never ends in 'P': ISIZE's top byte is 0).</summary>
    private static int StripPadding(byte[] data, int start, int end)
    {
        while (end > start && data[end - 1] == (byte)'P')
            end--;
        return end;
    }

    public byte[] Write()
    {
        if (Empty)
            return [];

        var writer = new ByteWriter(capacity: HeaderSize + PagesBlob.Length + Entries.Sum(e => e.Content.Length / 2 + 64));
        writer.WriteInt32(PageCount);
        writer.WriteInt32(HeaderSize);
        writer.WriteInt32(Entries.Count);
        writer.WriteInt32(HeaderSize + PagesBlob.Length);
        writer.WriteBytes(PagesBlob);

        int table = writer.Position;
        writer.WriteFill(8 * Entries.Count);

        for (int i = 0; i < Entries.Count; i++)
        {
            PmbEntry entry = Entries[i];
            writer.Align(entry.Compressed ? 4 : 16, (byte)'P');
            int offset = writer.Position;
            writer.WriteBytes(entry.StoredBytes ?? (entry.Compressed ? Gzip.Write(entry.ToGzipMember()) : entry.Content));

            int position = writer.Position;
            writer.Position = table + 8 * i;
            writer.WriteInt32(entry.Content.Length);
            writer.WriteInt32(offset);
            writer.Position = position;
        }

        writer.Align(4, (byte)'P');
        return writer.ToArray();
    }
}
