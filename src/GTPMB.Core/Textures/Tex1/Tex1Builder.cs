using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GTPMB.Core.Textures.Tex1;

/// <summary>
/// Builds a single-texture Tex1 set, byte for byte what the old TextureSetBuilder + TextureSet1.Serialize wrote
/// for one image with a default TextureConfig (REGION_CLAMP on both axes).
/// The pixel format follows the image's distinct RGBA colour count and nothing is ever quantized:
/// up to 16 colours -> PSMT4, up to 256 -> PSMT8, more -> PSMCT32.
/// </summary>
internal static class Tex1Builder
{
    /// <summary>TW / TH top out at 2^10 and MAXU / MAXV are 10-bit fields: the GS cannot address a bigger texture.</summary>
    public const int MaxTextureSize = 1024;

    private const int TexturesOffset = Tex1Reader.HeaderSize;
    private const int TransfersOffset = TexturesOffset + PgluTexture.Size;

    public static byte[] Build(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.Width, height = image.Height;
        if (width > MaxTextureSize || height > MaxTextureSize)
            throw new NotSupportedException($"A PS2 texture cannot exceed {MaxTextureSize}x{MaxTextureSize} (image is {width}x{height}).");

        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(image.Pixels);

        // The palette is the image's colours in first-seen order, scanning row-major. More than 256: true colour.
        var paletteLookup = new Dictionary<uint, byte>(256);
        var palette = new uint[256];
        bool indexed = true;
        foreach (uint pixel in pixels)
        {
            if (paletteLookup.ContainsKey(pixel))
                continue;
            if (paletteLookup.Count == 256)
            {
                indexed = false;
                break;
            }
            palette[paletteLookup.Count] = pixel;
            paletteLookup.Add(pixel, (byte)paletteLookup.Count);
        }

        GsFormat layout = !indexed ? GsFormat.CT32 : paletteLookup.Count <= 16 ? GsFormat.T4 : GsFormat.T8;
        bool is256 = layout == GsFormat.T8;

        // Indexed rows are packed at a width rounded UP to a multiple of 8 pixels (one whole 32-bit word of PSMT4
        // texels), the extra columns repeating the row's last texel. That is what PDI's own single-texture sets
        // carry (GT4 display.gpb: a 36-wide PSMT4 image ships as a 40x50 transfer, a 54-wide PSMT8 as 56x54), so a
        // GS IMAGE transfer row never ends mid-word. The padding stays inside the block column the last real texel
        // already occupies, so the block allocation is unchanged, and it lies past MAXU so REGION_CLAMP never
        // samples it.
        int packedWidth = indexed ? (width + 7) & ~7 : width;
        byte[] imageData = indexed
            ? PackIndices(pixels, width, height, packedWidth, paletteLookup, layout == GsFormat.T4)
            : PackTrueColour(pixels);

        // GS block allocation. The image sits at block 0. Because blocks follow a Z curve, an image usually
        // allocates blocks it puts no pixel in; the CLUT goes into the first of those that fits (one block for a
        // 16-colour CLUT, four consecutive ones for a 256-colour CLUT), else right after the image.
        int totalBlocks = layout.GetLastBlockIndex(width, height) + 1;
        int cbp = 0;
        if (indexed)
        {
            int clutBlocks = is256 ? 4 : 1;
            cbp = FindFreeBlocks(layout.GetBlockUsage(width, height), clutBlocks);
            if (cbp < 0)
            {
                cbp = totalBlocks;
                totalBlocks += clutBlocks;
            }
        }

        // TBW is in units of 64 pixels; the indexed formats' pages are 128 wide, so theirs must be even.
        int tbw = (width + 63) / 64;
        if (indexed && (tbw & 1) != 0)
            tbw++;

        var texture = new PgluTexture
        {
            // tex0: TBP0:14 TBW:6 PSM:6 TW:4 TH:4 TCC:1 TFX:2 CBP:14 CPSM:4 CSM:1 CSA:5 CLD:3
            // TCC = 1 (RGBA), CPSM = PSMCT32, CSM1, CSA = 0, CLD = 1 (load the CLUT) for the indexed formats.
            Tex0 = (ulong)tbw << 14
                 | (ulong)layout.Psm << 20
                 | (ulong)BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)width)) << 26
                 | (ulong)BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)height)) << 30
                 | 1UL << 34
                 | (ulong)cbp << 37
                 | (indexed ? 1UL : 0UL) << 61,

            // tex1: MMAG (bit 5) and MMIN (bits 6-8) = LINEAR, everything else 0.
            Tex1 = 1UL << 5 | 1UL << 6,

            // clamp: REGION_CLAMP on both axes, with the real size - 1 in MAXU / MAXV.
            Clamp = PgluTexture.RegionClamp
                  | (ulong)PgluTexture.RegionClamp << 2
                  | (ulong)(width - 1) << 14
                  | (ulong)(height - 1) << 34,
        };

        byte[]? clutData = indexed ? PackClut(palette, is256) : null;
        return Serialize(texture, (ushort)totalBlocks, layout.Psm, tbw, packedWidth, height, imageData, cbp, is256, clutData);
    }

    private static byte[] PackIndices(ReadOnlySpan<uint> pixels, int width, int height, int packedWidth, Dictionary<uint, byte> paletteLookup, bool fourBit)
    {
        var data = new byte[GsFormat.GetDataSize(packedWidth, height, fourBit ? GsPsm.PSMT4 : GsPsm.PSMT8)];
        var row = new byte[packedWidth];

        uint lastPixel = pixels[0];
        byte lastIndex = paletteLookup[lastPixel];

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> source = pixels.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                if (source[x] != lastPixel)
                {
                    lastPixel = source[x];
                    lastIndex = paletteLookup[lastPixel];
                }
                row[x] = lastIndex;
            }
            row.AsSpan(width).Fill(row[width - 1]);

            if (fourBit)
            {
                // One continuous nibble stream, low nibble first; packedWidth is even, so rows stay byte aligned.
                Span<byte> target = data.AsSpan(y * (packedWidth / 2), packedWidth / 2);
                for (int x = 0; x < target.Length; x++)
                    target[x] = (byte)(row[x * 2] | row[x * 2 + 1] << 4);
            }
            else
            {
                row.CopyTo(data.AsSpan(y * packedWidth));
            }
        }
        return data;
    }

    private static byte[] PackTrueColour(ReadOnlySpan<uint> pixels)
    {
        var data = new byte[pixels.Length * 4];
        Span<uint> target = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
        for (int i = 0; i < pixels.Length; i++)
            target[i] = Tex1Reader.PngToGsAlpha(pixels[i]);
        return data;
    }

    // PNG alpha 0..255 -> GS 0..0x80 (255 -> 0x80 = opaque): the exact inverse of the decoder's doubling, so
    // extract -> rebuild reproduces the CLUT bytes. A 256-colour CLUT is stored in CSM1's 8x2-tile order.
    private static byte[] PackClut(uint[] palette, bool is256)
    {
        int colours = is256 ? 256 : 16;
        var data = new byte[colours * 4];
        Span<uint> target = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
        for (int i = 0; i < colours; i++)
            target[is256 ? Tex1Reader.TiledClutIndex(i) : i] = Tex1Reader.PngToGsAlpha(palette[i]);
        return data;
    }

    /// <summary>First block index at which <paramref name="count"/> consecutive blocks are allocated but hold no pixels, or -1.</summary>
    private static int FindFreeBlocks(bool[] used, int count)
    {
        for (int start = 0; start + count <= used.Length; start++)
        {
            int run = 0;
            while (run < count && !used[start + run])
                run++;
            if (run == count)
                return start;
        }
        return -1;
    }

    private static byte[] Serialize(PgluTexture texture, ushort totalBlocks, GsPsm psm, int tbw, int packedWidth, int height,
                                    byte[] imageData, int cbp, bool is256, byte[]? clutData)
    {
        int transferCount = clutData is null ? 1 : 2;
        int imageOffset = Align16(TransfersOffset + transferCount * Tex1Reader.TransferInfoSize);
        int clutOffset = Align16(imageOffset + imageData.Length);

        // Every transfer is padded to 16 bytes ("the required minimum, otherwise GS memory gets jumbled for small
        // textures"), but the size field in the header is the UNPADDED end of the last transfer's data.
        int dataEnd = clutData is null ? imageOffset + imageData.Length : clutOffset + clutData.Length;
        var file = new byte[Align16(dataEnd)];

        Span<byte> header = file;
        BinaryPrimitives.WriteUInt32LittleEndian(header, Tex1Reader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0C..], (uint)dataEnd);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x12..], totalBlocks);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x14..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x16..], (ushort)transferCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x18..], TexturesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x1C..], TransfersOffset);

        texture.Write(file.AsSpan(TexturesOffset));

        WriteTransfer(file.AsSpan(TransfersOffset), imageOffset, bp: 0, tbw, psm, packedWidth, height);
        imageData.CopyTo(file.AsSpan(imageOffset));

        if (clutData is not null)
        {
            // The CLUT goes up as a PSMCT32 image: 16x16 with a buffer width of 4 (important), or 8x2 with 1.
            WriteTransfer(file.AsSpan(TransfersOffset + Tex1Reader.TransferInfoSize), clutOffset, cbp,
                          is256 ? 4 : 1, GsPsm.PSMCT32, is256 ? 16 : 8, is256 ? 16 : 2);
            clutData.CopyTo(file.AsSpan(clutOffset));
        }

        return file;
    }

    private static void WriteTransfer(Span<byte> target, int dataOffset, int bp, int bw, GsPsm format, int width, int height)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, (uint)dataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(target[4..], (ushort)bp);
        target[6] = (byte)bw;
        target[7] = (byte)format;
        BinaryPrimitives.WriteUInt16LittleEndian(target[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(target[10..], (ushort)height);
    }

    private static int Align16(int value) => (value + 15) & ~15;
}
