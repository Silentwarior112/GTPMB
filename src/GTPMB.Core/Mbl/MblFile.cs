using System.Text;

namespace GTPMB.Core.Mbl;

/// <summary>
/// An interactive item on a menu page. Items nest: an item can carry child items.
/// Field names follow the old community tool's research (verified against this tool's own
/// measurements): the item names an element on the page, may switch two layers, and invokes
/// a function - another page's name or a game-code action ('buy_car', 'wash_car', ...).
/// </summary>
public sealed class MblSub
{
    /// <summary>The element on the page this item belongs to (button / hotspot).</summary>
    public string Element = "";

    public string Layer1 = "";
    public string Layer2 = "";

    /// <summary>What the item invokes: a page name (this or another family) or a game action.</summary>
    public string Function = "";

    /// <summary>A return label; empty everywhere in the shipped game.</summary>
    public string Return = "";

    /// <summary>A PMB number; 0xFFFF = none (the shipped norm).</summary>
    public ushort PmbIndex = 0xFFFF;

    /// <summary>A small bitfield (0..3 in the shipped files). Meaning unknown, preserved.</summary>
    public ushort ButtonFlags;

    public List<MblSub> Children = [];
}

/// <summary>One menu page: its name, which PMB holds it, its flags, and its place in the menu path.</summary>
public sealed class MblEntry
{
    /// <summary>Page name; the page of the same name lives in "&lt;mbl&gt;&lt;PmbIndex&gt;.pmb". May be a path ("ar/h_garage").</summary>
    public string Name = "";

    /// <summary>Which numbered PMB of this family holds the page (donn.mbl uses 1, 6 and 106).</summary>
    public ushort PmbIndex;

    /// <summary>The page's button-flag bitfield (0x120, 0x164, ...). Bit meanings unknown, preserved.</summary>
    public ushort ButtonFlags;

    /// <summary>The "common" label; almost always empty ('status' in setting.mbl, 'v' in dealer.mbl).</summary>
    public string Common = "";

    /// <summary>The page this one returns to - the "back" path through the menus. Empty for roots.</summary>
    public string Return = "";

    /// <summary>A function label; almost always empty ('cancel_select_game' on arcade's ar/top).</summary>
    public string Function = "";

    public List<MblSub> Subs = [];
}

/// <summary>
/// A GT3 "MBL" menu list: the index of every menu page of one family. Three layout variants
/// ship (named as in the old community tool): mbl21 (0x20-byte names), mbl40 (0x40-byte
/// names) - both with the function label - and mbl20 (0x20-byte names, no function label;
/// only four orphaned dev files).
///
/// Layout (little-endian):
///   header  u32 entryCount, u32 tableOffset (= 8)
///   table   entryCount * u32 entry offsets
///   entry   name[E]; u16 pmbIndex, u16 buttonFlags; common[E]; return[E]; function[E]*;
///           u32 subCount; u32 subTableOffset (== its own position + 8); table; subs
///   sub     element[E]; layer1[E]; layer2[E]; function[E]; return[E];
///           u16 pmbIndex, u16 buttonFlags; u32 childCount; u32 childTableOffset
///           (== position + 8); child table; child subs (same shape, recursive)
///   (* = not in mbl20)
/// Reading is strict - a byte this model does not explain fails the parse - and the writer
/// reproduces every shipped MBL byte for byte.
/// </summary>
public sealed class MblFile
{
    /// <summary>The name-field size E: 0x20 or 0x40, uniform per file.</summary>
    public int NameSize = 0x20;

    /// <summary>False only for the mbl20 dev leftovers (no function label field on entries).</summary>
    public bool HasFunction = true;

    /// <summary>The variant name the old community research uses.</summary>
    public string Variant => !HasFunction ? "mbl20" : NameSize == 0x40 ? "mbl40" : "mbl21";

    /// <summary>Sets <see cref="NameSize"/> / <see cref="HasFunction"/> from a variant name.</summary>
    public static (int NameSize, bool HasFunction) ParseVariant(string variant) => variant switch
    {
        "mbl20" => (0x20, false),
        "mbl21" => (0x20, true),
        "mbl40" => (0x40, true),
        _ => throw new InvalidDataException($"Unknown MBL variant '{variant}' (mbl20, mbl21 or mbl40)."),
    };

    public List<MblEntry> Entries = [];

    public static MblFile Read(byte[] data)
    {
        var reader = new ByteReader(data);
        int count = reader.ReadInt32();
        int table = reader.ReadInt32();
        if (table != 8)
            throw new InvalidDataException($"MBL entry table at 0x{table:X} (expected 8).");
        if (count < 0 || 8 + 4L * count > data.Length)
            throw new InvalidDataException($"MBL with {count} entries runs past the end.");

        var offsets = new int[count];
        for (int i = 0; i < count; i++)
            offsets[i] = reader.ReadInt32();

        var mbl = new MblFile();
        if (count > 0)
            (mbl.NameSize, mbl.HasFunction) = DetectShape(data, offsets[0]);
        for (int i = 0; i < count; i++)
        {
            int end = i + 1 < count ? offsets[i + 1] : data.Length;
            mbl.Entries.Add(ReadEntry(data, offsets[i], end, mbl.NameSize, mbl.HasFunction));
        }
        return mbl;
    }

    /// <summary>
    /// Neither E nor the variant is recorded; both show in where entry 0's sub list lands,
    /// because its table offset field always equals its own position + 8.
    /// </summary>
    private static (int E, bool HasFunction) DetectShape(byte[] data, int entry)
    {
        foreach (int e in stackalloc[] { 0x20, 0x40 })
        {
            if (!IsNameField(data, entry, e))
                continue;
            foreach (int extraNames in stackalloc[] { 3, 2 })
            {
                int probe = entry + e + 4 + extraNames * e;
                if (probe + 8 > data.Length)
                    continue;
                int subCount = BitConverter.ToInt32(data, probe);
                int subTable = BitConverter.ToInt32(data, probe + 4);
                if (subTable == probe + 8 && subCount is >= 0 and < 0x10000)
                    return (e, extraNames == 3);
            }
        }
        throw new InvalidDataException("Not an MBL entry layout this tool knows (name size / field count undetected).");
    }

    private static bool IsNameField(byte[] data, int offset, int size)
    {
        if (offset + size > data.Length)
            return false;
        int nul = Array.IndexOf(data, (byte)0, offset, size);
        if (nul < 0)
            return false;
        for (int i = nul; i < offset + size; i++)
        {
            if (data[i] != 0)
                return false;
        }
        return true;
    }

    private static MblEntry ReadEntry(byte[] data, int offset, int end, int e, bool hasFunction)
    {
        var reader = new ByteReader(data, position: offset);
        var entry = new MblEntry { Name = ReadName(reader, e, "entry name") };
        entry.PmbIndex = reader.ReadUInt16();
        entry.ButtonFlags = reader.ReadUInt16();
        entry.Common = ReadName(reader, e, "entry common label");
        entry.Return = ReadName(reader, e, "entry return label");
        if (hasFunction)
            entry.Function = ReadName(reader, e, "entry function label");

        ReadSubList(reader, e, entry.Subs, end - offset);

        if (reader.Position != end)
            throw new InvalidDataException($"Entry at 0x{offset:X} ends at 0x{reader.Position:X}, expected 0x{end:X}.");
        return entry;
    }

    private static void ReadSubList(ByteReader reader, int e, List<MblSub> subs, int budget)
    {
        int subCount = reader.ReadInt32();
        int subTable = reader.ReadInt32();
        if (subCount < 0 || (long)subCount * (5 * e + 12) > budget)
            throw new InvalidDataException($"A sub list declares {subCount} items at 0x{reader.Position:X}.");
        if (subTable != reader.Position)
            throw new InvalidDataException($"A sub table points at 0x{subTable:X}, expected 0x{reader.Position:X}.");

        var offsets = new int[subCount];
        for (int i = 0; i < subCount; i++)
            offsets[i] = reader.ReadInt32();

        for (int i = 0; i < subCount; i++)
        {
            if (offsets[i] != reader.Position)
                throw new InvalidDataException($"Sub {i} is at 0x{offsets[i]:X}, expected 0x{reader.Position:X}.");
            var sub = new MblSub
            {
                Element = ReadName(reader, e, "item element"),
                Layer1 = ReadName(reader, e, "item layer1"),
                Layer2 = ReadName(reader, e, "item layer2"),
                Function = ReadName(reader, e, "item function"),
                Return = ReadName(reader, e, "item return"),
                PmbIndex = reader.ReadUInt16(),
                ButtonFlags = reader.ReadUInt16(),
            };
            ReadSubList(reader, e, sub.Children, budget);
            subs.Add(sub);
        }
    }

    private static string ReadName(ByteReader reader, int size, string what)
    {
        ReadOnlySpan<byte> field = reader.ReadSpan(size);
        int nul = field.IndexOf((byte)0);
        if (nul < 0)
            throw new InvalidDataException($"A {what} fills its whole {size}-byte field (no terminator).");
        foreach (byte b in field[nul..])
        {
            if (b != 0)
                throw new InvalidDataException($"A {what} field is not zero-padded.");
        }
        return Encoding.Latin1.GetString(field[..nul]);
    }

    public byte[] Write()
    {
        var writer = new ByteWriter();
        writer.WriteInt32(Entries.Count);
        writer.WriteInt32(8);
        int table = writer.Position;
        writer.WriteFill(4 * Entries.Count);

        for (int i = 0; i < Entries.Count; i++)
        {
            MblEntry entry = Entries[i];
            int at = writer.Position;
            writer.Position = table + 4 * i;
            writer.WriteInt32(at);
            writer.Position = at;

            WriteName(writer, entry.Name, "entry name");
            writer.WriteUInt16(entry.PmbIndex);
            writer.WriteUInt16(entry.ButtonFlags);
            WriteName(writer, entry.Common, "entry common label");
            WriteName(writer, entry.Return, "entry return label");
            if (HasFunction)
                WriteName(writer, entry.Function, "entry function label");
            else if (entry.Function.Length > 0)
                throw new InvalidDataException($"Entry '{entry.Name}' has a function label but the {Variant} layout has no field for it.");
            WriteSubList(writer, entry.Subs);
        }

        return writer.ToArray();
    }

    private void WriteSubList(ByteWriter writer, List<MblSub> subs)
    {
        writer.WriteInt32(subs.Count);
        writer.WriteInt32(writer.Position + 4);
        int table = writer.Position;
        writer.WriteFill(4 * subs.Count);
        for (int i = 0; i < subs.Count; i++)
        {
            MblSub sub = subs[i];
            int at = writer.Position;
            writer.Position = table + 4 * i;
            writer.WriteInt32(at);
            writer.Position = at;

            WriteName(writer, sub.Element, "item element");
            WriteName(writer, sub.Layer1, "item layer1");
            WriteName(writer, sub.Layer2, "item layer2");
            WriteName(writer, sub.Function, "item function");
            WriteName(writer, sub.Return, "item return");
            writer.WriteUInt16(sub.PmbIndex);
            writer.WriteUInt16(sub.ButtonFlags);
            WriteSubList(writer, sub.Children);
        }
    }

    private void WriteName(ByteWriter writer, string name, string what)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        if (bytes.Length >= NameSize)
            throw new InvalidDataException($"A {what} does not fit its {NameSize}-byte field: '{name}'.");
        writer.WriteBytes(bytes);
        writer.WriteFill(NameSize - bytes.Length);
    }
}
