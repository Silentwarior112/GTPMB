using System.Text;
using GTPMB.Core.Mbl;
using GTPMB.Core.Textures.Tex1;

namespace GTPMB.Core.Pmb;

/// <summary>"Inspect PMB / MBL": a text report of what is inside, for the log window.</summary>
public static class PmbInspector
{
    public static string Report(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return path.EndsWith(".mbl", StringComparison.OrdinalIgnoreCase) || LooksLikeMbl(data)
            ? ReportMbl(path, data)
            : ReportPmb(path, data);
    }

    // A PMB has its page table offset (0x10) at +4; an MBL has its entry table offset (8) there.
    private static bool LooksLikeMbl(byte[] data) =>
        data.Length >= 8 && BitConverter.ToInt32(data, 4) == 8;

    private static string ReportPmb(string path, byte[] data)
    {
        var text = new StringBuilder();
        text.AppendLine($"{Path.GetFileName(path)} - GT3 menu PMB, {data.Length:N0} bytes");

        PmbFile pmb;
        try
        {
            pmb = PmbFile.Read(data);
        }
        catch (InvalidDataException e)
        {
            return text.AppendLine($"Not a readable PMB: {e.Message}").ToString();
        }

        if (pmb.Empty)
            return text.AppendLine("Empty (zero-length, as shipped for arcade0 / gt_mode0).").ToString();

        text.AppendLine($"{pmb.PageCount} page(s), {pmb.Entries.Count} embedded file(s)");
        text.AppendLine();

        PageTree? tree = null;
        try
        {
            tree = PageTree.Read(data);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            text.AppendLine($"(page structures not decoded: {e.Message})");
        }

        if (tree is not null)
        {
            text.AppendLine($"Pages (name fields {tree.NameSize} bytes):");
            foreach (PageTree.Page page in tree.Pages)
            {
                text.AppendLine($"  page '{page.Name}'");
                foreach (PageTree.Element element in page.Elements)
                    Describe(text, element, "    ");
                foreach (PageTree.Layer layer in page.Layers)
                {
                    text.AppendLine($"    layer '{layer.Name}'");
                    foreach (PageTree.Element element in layer.ElementsA)
                        Describe(text, element, "      ");
                    foreach (PageTree.Element element in layer.ElementsB)
                        Describe(text, element, "      ");
                }
            }
            text.AppendLine();
        }

        text.AppendLine("Embedded files:");
        for (int i = 0; i < pmb.Entries.Count; i++)
        {
            PmbEntry entry = pmb.Entries[i];
            string kind = "raw file";
            if (Tex1Codec.IsTex1(entry.Content))
            {
                try
                {
                    RgbaImage image = Tex1Codec.DecodeTexture(entry.Content);
                    kind = $"Tex1 {image.Width}x{image.Height}";
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    kind = $"Tex1 (decode failed: {e.Message})";
                }
            }
            text.AppendLine($"  [{i + 1,3}] {entry.Content.Length,7:N0} bytes  {kind}"
                + $"{(entry.Compressed ? "  gzip" : "  raw")}{(entry.GzipName is null ? "" : $"  '{entry.GzipName}'")}");
        }
        return text.ToString();
    }

    private static void Describe(StringBuilder text, PageTree.Element element, string indent)
    {
        var extras = new List<string>();
        if (element.Reference is not null)
            extras.Add($"ref '{element.Reference}'");
        if (element.FileIndex >= 0)
            extras.Add($"file {element.FileIndex + 1}");
        if (element.Text is not null)
            extras.Add($"text '{element.Text}'");
        if (element.Font is not null)
            extras.Add($"font '{element.Font}'");
        text.AppendLine($"{indent}{element.Name} (type {element.Type:X}, flags {element.Flags:X}) "
            + $"at {element.X:0.#},{element.Y:0.#} size {element.W:0.#}x{element.H:0.#}"
            + (extras.Count > 0 ? "  " + string.Join(", ", extras) : ""));
        if (element.Child is not null)
            Describe(text, element.Child, indent + "  ");
    }

    private static string ReportMbl(string path, byte[] data)
    {
        var text = new StringBuilder();
        text.AppendLine($"{Path.GetFileName(path)} - GT3 menu MBL, {data.Length:N0} bytes");

        MblFile mbl;
        try
        {
            mbl = MblFile.Read(data);
        }
        catch (InvalidDataException e)
        {
            return text.AppendLine($"Not a readable MBL: {e.Message}").ToString();
        }

        string family = PmbConfig.WithoutExtension(Path.GetFileName(path));
        text.AppendLine($"{mbl.Entries.Count} page(s), variant {mbl.Variant} (name fields {mbl.NameSize} bytes)");
        text.AppendLine();
        foreach (MblEntry entry in mbl.Entries)
        {
            text.AppendLine($"  page '{entry.Name}'  ->  {family}{entry.PmbIndex}.pmb, flags 0x{entry.ButtonFlags:X}"
                + (entry.Return.Length > 0 ? $", return '{entry.Return}'" : "")
                + (entry.Common.Length > 0 ? $", common '{entry.Common}'" : "")
                + (entry.Function.Length > 0 ? $", function '{entry.Function}'" : ""));
            DescribeSubs(text, entry.Subs, "      ");
        }
        return text.ToString();
    }

    private static void DescribeSubs(StringBuilder text, List<MblSub> subs, string indent)
    {
        foreach (MblSub sub in subs)
        {
            string[] layers = [sub.Layer1, sub.Layer2];
            string named = string.Join(", ", layers.Where(n => n.Length > 0).Select(n => $"'{n}'"));
            text.AppendLine($"{indent}item '{sub.Element}'"
                + (sub.Function.Length > 0 ? $" -> '{sub.Function}'" : "")
                + (named.Length > 0 ? $", layer {named}" : "")
                + (sub.Return.Length > 0 ? $", return '{sub.Return}'" : "")
                + (sub.PmbIndex != 0xFFFF ? $", pmb {sub.PmbIndex}" : "")
                + (sub.ButtonFlags != 0 ? $"  (flags 0x{sub.ButtonFlags:X})" : ""));
            DescribeSubs(text, sub.Children, indent + "  ");
        }
    }
}
