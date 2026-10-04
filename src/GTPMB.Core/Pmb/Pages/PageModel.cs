using System.Text;

namespace GTPMB.Core.Pmb.Pages;

/// <summary>Slot 7: which embedded file the element draws, and its four corner UVs.</summary>
public sealed class TextureRef
{
    /// <summary>0-based index into the PMB's file list.</summary>
    public int FileIndex;

    /// <summary>Four corner UV pairs; values above 1 tile.</summary>
    public float[] Uv = new float[8];
}

/// <summary>Slot 9: the text properties (two unknown codes, RGBA colour, text and font strings).</summary>
public sealed class TextBlock
{
    public ushort Code1, Code2;

    /// <summary>RGBA as stored (byte order R G B A, alpha 0x80 = opaque on the GS).</summary>
    public uint Rgba;

    /// <summary>Null when the block has no text pointer at all; "" is a real empty string.</summary>
    public string? Text;
    public string? Font;
}

/// <summary>
/// One element of a menu page. Its on-disk type byte is fully derived: bit 1 = has
/// <see cref="Texture"/>, bit 2 = <see cref="Colors"/>, bit 4 = <see cref="Text"/>, bit 8 =
/// <see cref="Child"/> (measured with zero exceptions over the whole game), plus bit 0x20 =
/// <see cref="Arrow"/>.
/// </summary>
public sealed class PageElement
{
    public string Name = "";
    public byte Sub;
    public ushort Flags;
    public float X, Y, Z, W, H;

    /// <summary>Slot 5: the named runtime binding ('render_model', 'rank_disp', ...). Null = absent.</summary>
    public string? Key;

    /// <summary>Slot 6: composites carry one child element (which may chain further).</summary>
    public PageElement? Child;

    public TextureRef? Texture;

    /// <summary>Slot 8: four RGBA corner colours as stored.</summary>
    public uint[]? Colors;

    public TextBlock? Text;

    /// <summary>Type bit 0x20 (the shipped sample is literally named 'arrow').</summary>
    public bool Arrow;

    public byte Type => (byte)((Texture is null ? 0 : 1) | (Colors is null ? 0 : 2)
        | (Text is null ? 0 : 4) | (Child is null ? 0 : 8) | (Arrow ? 0x20 : 0));
}

public sealed class PageLayer
{
    public string Name = "";
    public List<PageElement> GroupA = [];
    public List<PageElement> GroupB = [];
}

public sealed class PmbPage
{
    public string Name = "";
    public List<PageElement> Elements = [];
    public List<PageLayer> Layers = [];
}

/// <summary>
/// The decoded form of a PMB's page region ("pages.bin": bytes [0x10, fileTableOffset), the
/// page offset table plus every page structure; internal offsets are absolute file offsets).
/// <see cref="Write"/> re-emits the canonical layout, byte-identical for every current-format
/// page region in the shipped game.
/// </summary>
public sealed class PageList
{
    /// <summary>The base file offset of the blob: offsets inside it are absolute.</summary>
    private const int Base = PmbFile.HeaderSize;

    /// <summary>Name-field size: 0x20 or 0x40, uniform per file.</summary>
    public int NameSize = 0x20;

    public List<PmbPage> Pages = [];

    public static PageList Read(byte[] pagesBlob)
    {
        var list = new PageList();
        if (pagesBlob.Length == 0)
            return list;

        int firstPage = BitConverter.ToInt32(pagesBlob, 0);
        int pageCount = (firstPage - Base) / 4;
        if (pageCount <= 0 || firstPage % 4 != 0 || 4 * pageCount > pagesBlob.Length)
            throw new InvalidDataException("The page region does not start with a page table.");

        var reader = new Reader(pagesBlob);
        list.NameSize = reader.DetectNameSize(firstPage, pageCount);
        for (int i = 0; i < pageCount; i++)
            list.Pages.Add(reader.ReadPage(BitConverter.ToInt32(pagesBlob, 4 * i)));
        return list;
    }

    private sealed class Reader(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _nameSize;

        private int At(int fileOffset)
        {
            int index = fileOffset - Base;
            if (index < 0 || index > _data.Length)
                throw new InvalidDataException($"Page offset 0x{fileOffset:X} is outside the page region.");
            return index;
        }

        public int DetectNameSize(int firstPage, int pageCount)
        {
            int page = At(firstPage);
            foreach (int n in stackalloc[] { 0x20, 0x40 })
            {
                if (page + n + 16 > _data.Length)
                    continue;
                int cA = BitConverter.ToInt32(_data, page + n);
                int tA = BitConverter.ToInt32(_data, page + n + 4);
                int cB = BitConverter.ToInt32(_data, page + n + 8);
                int tB = BitConverter.ToInt32(_data, page + n + 12);
                if (cA is >= 0 and < 0x10000 && cB is >= 0 and < 0x10000
                    && tA >= firstPage + n + 16 && tA - Base <= _data.Length
                    && tB >= tA && tB - Base <= _data.Length)
                {
                    _nameSize = n;
                    return n;
                }
            }
            throw new InvalidDataException("Page name-field size is neither 0x20 nor 0x40.");
        }

        private string ReadName(int at)
        {
            ReadOnlySpan<byte> field = _data.AsSpan(at, _nameSize);
            int nul = field.IndexOf((byte)0);
            if (nul < 0)
                return Encoding.Latin1.GetString(field); // two shipped names fill the field exactly
            foreach (byte b in field[nul..])
            {
                if (b != 0)
                    throw new InvalidDataException($"A name field at +0x{at:X} is not zero-padded.");
            }
            return Encoding.Latin1.GetString(field[..nul]);
        }

        private string ReadString(int fileOffset)
        {
            int at = At(fileOffset);
            int nul = Array.IndexOf(_data, (byte)0, at);
            if (nul < 0)
                throw new InvalidDataException($"Unterminated string at 0x{fileOffset:X}.");
            int length = nul - at;
            int paddedEnd = Math.Min(at + ((length + 1 + 3) & ~3), _data.Length);
            for (int i = nul + 1; i < paddedEnd; i++)
            {
                if (_data[i] != 0)
                    throw new InvalidDataException($"String padding at 0x{fileOffset:X} is not zero.");
            }
            return Encoding.Latin1.GetString(_data, at, length);
        }

        public PmbPage ReadPage(int fileOffset)
        {
            int at = At(fileOffset);
            var page = new PmbPage { Name = ReadName(at) };
            var (cA, tA, cB, tB) = ReadCounts(at);
            for (int i = 0; i < cA; i++)
                page.Elements.Add(ReadElement(TableEntry(tA, i)));
            for (int i = 0; i < cB; i++)
                page.Layers.Add(ReadLayer(TableEntry(tB, i)));
            return page;
        }

        private PageLayer ReadLayer(int fileOffset)
        {
            int at = At(fileOffset);
            var layer = new PageLayer { Name = ReadName(at) };
            var (cA, tA, cB, tB) = ReadCounts(at);
            for (int i = 0; i < cA; i++)
                layer.GroupA.Add(ReadElement(TableEntry(tA, i)));
            for (int i = 0; i < cB; i++)
                layer.GroupB.Add(ReadElement(TableEntry(tB, i)));
            return layer;
        }

        private (int, int, int, int) ReadCounts(int at) => (
            BitConverter.ToInt32(_data, at + _nameSize),
            BitConverter.ToInt32(_data, at + _nameSize + 4),
            BitConverter.ToInt32(_data, at + _nameSize + 8),
            BitConverter.ToInt32(_data, at + _nameSize + 12));

        private int TableEntry(int tableFileOffset, int i) =>
            BitConverter.ToInt32(_data, At(tableFileOffset) + 4 * i);

        private PageElement ReadElement(int fileOffset)
        {
            int at = At(fileOffset);
            var element = new PageElement { Name = ReadName(at) };
            int p = at + _nameSize;
            byte type = _data[p];
            element.Arrow = (type & 0x20) != 0;
            element.Sub = _data[p + 1];
            element.Flags = BitConverter.ToUInt16(_data, p + 2);
            element.X = BitConverter.ToSingle(_data, p + 4);
            element.Y = BitConverter.ToSingle(_data, p + 8);
            element.Z = BitConverter.ToSingle(_data, p + 12);
            element.W = BitConverter.ToSingle(_data, p + 16);
            element.H = BitConverter.ToSingle(_data, p + 20);

            int s5 = BitConverter.ToInt32(_data, p + 24);
            int s6 = BitConverter.ToInt32(_data, p + 28);
            int s7 = BitConverter.ToInt32(_data, p + 32);
            int s8 = BitConverter.ToInt32(_data, p + 36);
            int s9 = BitConverter.ToInt32(_data, p + 40);

            if (s5 != 0)
                element.Key = ReadString(BitConverter.ToInt32(_data, At(s5)));
            if (s6 != 0)
                element.Child = ReadElement(s6);
            if (s7 != 0)
            {
                int b = At(s7);
                var texture = new TextureRef { FileIndex = BitConverter.ToInt32(_data, b) };
                for (int i = 0; i < 8; i++)
                    texture.Uv[i] = BitConverter.ToSingle(_data, b + 4 + 4 * i);
                element.Texture = texture;
            }
            if (s8 != 0)
            {
                int b = At(s8);
                element.Colors = new uint[4];
                for (int i = 0; i < 4; i++)
                    element.Colors[i] = BitConverter.ToUInt32(_data, b + 4 * i);
            }
            if (s9 != 0)
            {
                int b = At(s9);
                var text = new TextBlock
                {
                    Code1 = BitConverter.ToUInt16(_data, b),
                    Code2 = BitConverter.ToUInt16(_data, b + 2),
                    Rgba = BitConverter.ToUInt32(_data, b + 4),
                };
                int textOff = BitConverter.ToInt32(_data, b + 8);
                int fontOff = BitConverter.ToInt32(_data, b + 12);
                text.Text = textOff != 0 ? ReadString(textOff) : null;
                text.Font = fontOff != 0 ? ReadString(fontOff) : null;
                element.Text = text;
            }

            byte expected = element.Type;
            if (type != expected)
                throw new InvalidDataException(
                    $"Element '{element.Name}' at 0x{fileOffset:X} has type 0x{type:X} but its blocks say 0x{expected:X} - an unknown page format.");
            return element;
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public byte[] Write()
    {
        if (Pages.Count == 0)
            return [];
        var writer = new ByteWriter();
        int table = writer.Position;
        writer.WriteFill(4 * Pages.Count);
        for (int i = 0; i < Pages.Count; i++)
        {
            int at = writer.Position;
            writer.Position = table + 4 * i;
            writer.WriteInt32(Base + at);
            writer.Position = at;
            WritePage(writer, Pages[i]);
        }
        return writer.ToArray();
    }

    private void WritePage(ByteWriter writer, PmbPage page)
    {
        WriteName(writer, page.Name);
        int header = writer.Position;
        writer.WriteFill(16);
        int tableA = writer.Position;
        writer.WriteFill(4 * page.Elements.Count);
        for (int i = 0; i < page.Elements.Count; i++)
        {
            Patch(writer, tableA + 4 * i, writer.Position);
            WriteElement(writer, page.Elements[i]);
        }
        int tableB = writer.Position;
        writer.WriteFill(4 * page.Layers.Count);
        for (int i = 0; i < page.Layers.Count; i++)
        {
            Patch(writer, tableB + 4 * i, writer.Position);
            WriteLayer(writer, page.Layers[i]);
        }
        PatchCounts(writer, header, page.Elements.Count, tableA, page.Layers.Count, tableB);
    }

    private void WriteLayer(ByteWriter writer, PageLayer layer)
    {
        WriteName(writer, layer.Name);
        int header = writer.Position;
        writer.WriteFill(16);
        int tableA = writer.Position;
        writer.WriteFill(4 * layer.GroupA.Count);
        for (int i = 0; i < layer.GroupA.Count; i++)
        {
            Patch(writer, tableA + 4 * i, writer.Position);
            WriteElement(writer, layer.GroupA[i]);
        }
        int tableB = writer.Position;
        writer.WriteFill(4 * layer.GroupB.Count);
        for (int i = 0; i < layer.GroupB.Count; i++)
        {
            Patch(writer, tableB + 4 * i, writer.Position);
            WriteElement(writer, layer.GroupB[i]);
        }
        PatchCounts(writer, header, layer.GroupA.Count, tableA, layer.GroupB.Count, tableB);
    }

    private void WriteElement(ByteWriter writer, PageElement element)
    {
        WriteName(writer, element.Name);
        writer.WriteByte(element.Type);
        writer.WriteByte(element.Sub);
        writer.WriteUInt16(element.Flags);
        writer.WriteSingle(element.X);
        writer.WriteSingle(element.Y);
        writer.WriteSingle(element.Z);
        writer.WriteSingle(element.W);
        writer.WriteSingle(element.H);
        int slots = writer.Position;
        writer.WriteFill(20);

        if (element.Key is not null)
        {
            Patch(writer, slots, writer.Position);
            int block = writer.Position;
            writer.WriteFill(4);
            Patch(writer, block, writer.Position);
            WriteString(writer, element.Key);
        }
        if (element.Child is not null)
        {
            Patch(writer, slots + 4, writer.Position);
            WriteElement(writer, element.Child);
        }
        if (element.Texture is not null)
        {
            Patch(writer, slots + 8, writer.Position);
            writer.WriteInt32(element.Texture.FileIndex);
            for (int i = 0; i < 8; i++)
                writer.WriteSingle(element.Texture.Uv[i]);
        }
        if (element.Colors is not null)
        {
            Patch(writer, slots + 12, writer.Position);
            for (int i = 0; i < 4; i++)
                writer.WriteUInt32(element.Colors[i]);
        }
        if (element.Text is not null)
        {
            Patch(writer, slots + 16, writer.Position);
            int block = writer.Position;
            writer.WriteUInt16(element.Text.Code1);
            writer.WriteUInt16(element.Text.Code2);
            writer.WriteUInt32(element.Text.Rgba);
            writer.WriteFill(8);
            if (element.Text.Text is not null)
            {
                Patch(writer, block + 8, writer.Position);
                WriteString(writer, element.Text.Text);
            }
            if (element.Text.Font is not null)
            {
                Patch(writer, block + 12, writer.Position);
                WriteString(writer, element.Text.Font);
            }
        }
    }

    private void WriteName(ByteWriter writer, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        if (bytes.Length > NameSize)
            throw new InvalidDataException($"Name does not fit its {NameSize}-byte field: '{name}'.");
        writer.WriteBytes(bytes);
        writer.WriteFill(NameSize - bytes.Length);
    }

    private static void WriteString(ByteWriter writer, string value)
    {
        writer.WriteBytes(Encoding.Latin1.GetBytes(value));
        writer.WriteByte(0);
        writer.Align(4);
    }

    private static void Patch(ByteWriter writer, int at, int value)
    {
        int position = writer.Position;
        writer.Position = at;
        writer.WriteInt32(Base + value);
        writer.Position = position;
    }

    private static void PatchCounts(ByteWriter writer, int header, int countA, int tableA, int countB, int tableB)
    {
        int position = writer.Position;
        writer.Position = header;
        writer.WriteInt32(countA);
        writer.WriteInt32(Base + tableA);
        writer.WriteInt32(countB);
        writer.WriteInt32(Base + tableB);
        writer.Position = position;
    }
}
