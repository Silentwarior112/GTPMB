using System.Text;

namespace GTPMB.Core.Pmb;

/// <summary>
/// Read-only decode of the page structures inside a PMB, for inspection. The tool never
/// re-serializes pages (they travel verbatim in pages.bin); this exists so the user can see
/// what a page contains and which element uses which embedded file.
///
/// Structures (offsets absolute, N = name-field size 0x20 or 0x40, uniform per file):
///   page    name[N]; u32 elemCount, elemTable, layerCount, layerTable
///   layer   name[N]; u32 countA, tableA, countB, tableB
///   element name[N]; u8 type, u8 sub, u16 flags; f32 x, y, z, w, h; u32 slot[5]
/// type is a bitmask of the used slots: 1 = texture block (slot7: u32 fileIndex + f32 uv[8]),
/// 2 = colors (slot8: 4 * RGBA), 4 = text (slot9: u16, u16, RGBA, textOff, fontOff),
/// 8 = child element (slot6); slot5 = named-reference block { u32 strOff } when flags bit 0.
/// One shipped file (US arcade2.pmb, a leftover) uses an older four-slot layout and fails this
/// decode; the container level is unaffected.
/// </summary>
public sealed class PageTree
{
    public sealed class Element
    {
        public required string Name;
        public byte Type, Sub;
        public ushort Flags;
        public float X, Y, Z, W, H;
        public string? Reference;      // slot5 named binding ("render_model", "rank_disp", ...)
        public int FileIndex = -1;     // slot7 texture block's embedded-file index
        public float[]? Uv;            // slot7 texture block's 8 floats
        public uint[]? Colors;         // slot8
        public string? Text;           // slot9
        public string? Font;           // slot9
        public Element? Child;         // slot6
    }

    public sealed class Layer
    {
        public required string Name;
        public List<Element> ElementsA = [];
        public List<Element> ElementsB = [];
    }

    public sealed class Page
    {
        public required string Name;
        public List<Element> Elements = [];
        public List<Layer> Layers = [];
    }

    public int NameSize = 0x20;
    public List<Page> Pages = [];

    /// <summary>Decodes the pages of a whole PMB file image (header + pages + file data).</summary>
    public static PageTree Read(byte[] pmbData)
    {
        var reader = new ByteReader(pmbData);
        int pageCount = reader.ReadInt32();
        int pageTable = reader.ReadInt32();
        var tree = new PageTree();
        if (pageCount == 0)
            return tree;

        var offsets = new int[pageCount];
        reader.Position = pageTable;
        for (int i = 0; i < pageCount; i++)
            offsets[i] = reader.ReadInt32();

        tree.NameSize = DetectNameSize(pmbData, offsets[0]);
        foreach (int offset in offsets)
            tree.Pages.Add(ReadPage(pmbData, offset, tree.NameSize));
        return tree;
    }

    private static int DetectNameSize(byte[] data, int page)
    {
        foreach (int n in stackalloc[] { 0x20, 0x40 })
        {
            if (page + n + 16 > data.Length)
                continue;
            int countA = BitConverter.ToInt32(data, page + n);
            int tableA = BitConverter.ToInt32(data, page + n + 4);
            int countB = BitConverter.ToInt32(data, page + n + 8);
            int tableB = BitConverter.ToInt32(data, page + n + 12);
            if (countA is >= 0 and < 0x10000 && countB is >= 0 and < 0x10000
                && tableA >= page + n + 16 && tableA <= data.Length
                && tableB >= tableA && tableB <= data.Length)
                return n;
        }
        throw new InvalidDataException("Page name-field size is neither 0x20 nor 0x40.");
    }

    private static string Name(byte[] data, int offset, int size)
    {
        int nul = Array.IndexOf(data, (byte)0, offset, size);
        int length = nul < 0 ? size : nul - offset;
        return Encoding.Latin1.GetString(data, offset, length);
    }

    private static string CString(byte[] data, int offset)
    {
        int nul = Array.IndexOf(data, (byte)0, offset);
        return Encoding.Latin1.GetString(data, offset, (nul < 0 ? data.Length : nul) - offset);
    }

    private static Page ReadPage(byte[] data, int offset, int n)
    {
        var page = new Page { Name = Name(data, offset, n) };
        int countA = BitConverter.ToInt32(data, offset + n);
        int tableA = BitConverter.ToInt32(data, offset + n + 4);
        int countB = BitConverter.ToInt32(data, offset + n + 8);
        int tableB = BitConverter.ToInt32(data, offset + n + 12);
        for (int i = 0; i < countA; i++)
            page.Elements.Add(ReadElement(data, BitConverter.ToInt32(data, tableA + 4 * i), n));
        for (int i = 0; i < countB; i++)
            page.Layers.Add(ReadLayer(data, BitConverter.ToInt32(data, tableB + 4 * i), n));
        return page;
    }

    private static Layer ReadLayer(byte[] data, int offset, int n)
    {
        var layer = new Layer { Name = Name(data, offset, n) };
        int countA = BitConverter.ToInt32(data, offset + n);
        int tableA = BitConverter.ToInt32(data, offset + n + 4);
        int countB = BitConverter.ToInt32(data, offset + n + 8);
        int tableB = BitConverter.ToInt32(data, offset + n + 12);
        for (int i = 0; i < countA; i++)
            layer.ElementsA.Add(ReadElement(data, BitConverter.ToInt32(data, tableA + 4 * i), n));
        for (int i = 0; i < countB; i++)
            layer.ElementsB.Add(ReadElement(data, BitConverter.ToInt32(data, tableB + 4 * i), n));
        return layer;
    }

    private static Element ReadElement(byte[] data, int offset, int n)
    {
        var element = new Element { Name = Name(data, offset, n) };
        int p = offset + n;
        element.Type = data[p];
        element.Sub = data[p + 1];
        element.Flags = BitConverter.ToUInt16(data, p + 2);
        element.X = BitConverter.ToSingle(data, p + 4);
        element.Y = BitConverter.ToSingle(data, p + 8);
        element.Z = BitConverter.ToSingle(data, p + 12);
        element.W = BitConverter.ToSingle(data, p + 16);
        element.H = BitConverter.ToSingle(data, p + 20);

        int slot5 = BitConverter.ToInt32(data, p + 24);
        int slot6 = BitConverter.ToInt32(data, p + 28);
        int slot7 = BitConverter.ToInt32(data, p + 32);
        int slot8 = BitConverter.ToInt32(data, p + 36);
        int slot9 = BitConverter.ToInt32(data, p + 40);

        if (slot5 != 0)
            element.Reference = CString(data, BitConverter.ToInt32(data, slot5));
        if (slot6 != 0)
            element.Child = ReadElement(data, slot6, n);
        if (slot7 != 0)
        {
            element.FileIndex = BitConverter.ToInt32(data, slot7);
            element.Uv = new float[8];
            for (int i = 0; i < 8; i++)
                element.Uv[i] = BitConverter.ToSingle(data, slot7 + 4 + 4 * i);
        }
        if (slot8 != 0)
        {
            element.Colors = new uint[4];
            for (int i = 0; i < 4; i++)
                element.Colors[i] = BitConverter.ToUInt32(data, slot8 + 4 * i);
        }
        if (slot9 != 0)
        {
            int textOff = BitConverter.ToInt32(data, slot9 + 8);
            int fontOff = BitConverter.ToInt32(data, slot9 + 12);
            element.Text = textOff != 0 ? CString(data, textOff) : null;
            element.Font = fontOff != 0 ? CString(data, fontOff) : null;
        }
        return element;
    }
}
