using System.Text;
using GTPMB.Core.Imaging;

namespace GTPMB.Core.Pmb;

/// <summary>"Inspect color depths".</summary>
public static class ColorInspector
{
    // The palette tiers, by distinct RGBA colour count. They decide the format on every platform with paletted
    // textures: a PS2 Tex1 build picks its format from exactly this count, and a PSP IDTEX4 / IDTEX8 texture with
    // too many colours for its flag is promoted to the next tier. (PS3 textures have no paletted formats.)
    // Listed largest first: the images that need attention before a build come at the top.
    private static readonly (string Title, int Low, int? High)[] Tiers =
    [
        ("More than 256 colors - 32-bit true color only (PS2 PSMCT32, PSP 8888); reduce the colors first if a paletted format is wanted", 257, null),
        ("17 to 256 colors - fits an 8-bit palette (PS2 PSMT8, PSP IDTEX8)", 17, 256),
        ("Up to 16 colors - fits a 4-bit palette (PS2 PSMT4, PSP IDTEX4)", 0, 16),
    ];

    /// <summary>
    /// The distinct RGBA colour count of every .png under <paramref name="rootFolder"/>, grouped by the palette
    /// tier it fits (largest tier first), largest textures first within a tier. Null when there are no PNGs.
    /// <paramref name="tick"/> is invoked once per file.
    /// </summary>
    public static string? BuildReport(string rootFolder, Action? tick = null)
    {
        string root = Path.GetFullPath(rootFolder);
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            return null;

        var results = new (int Colors, int Width, int Height, string Path)?[files.Length];
        var failures = new (string Path, string Error)?[files.Length];
        Parallel.For(0, files.Length, PmbConfig.Parallelism, i =>
        {
            string relative = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            try
            {
                RgbaImage image = Png.Load(files[i]);
                results[i] = (image.CountDistinctColors(), image.Width, image.Height, relative);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures[i] = (relative, e.Message);
            }
            tick?.Invoke();
        });

        var scanned = results.Where(r => r is not null).Select(r => r!.Value).ToList();
        var failed = failures.Where(f => f is not null).Select(f => f!.Value).ToList();

        var text = new StringBuilder();
        text.Append($"PNG color counts under: {rootFolder}\n");
        text.Append($"{scanned.Count} PNG(s) scanned{(failed.Count > 0 ? $", {failed.Count} could not be read" : "")}\n\n");

        foreach (var (title, low, high) in Tiers)
        {
            var group = scanned.Where(r => r.Colors >= low && (high is null || r.Colors <= high))
                .OrderByDescending(r => (long)r.Width * r.Height)
                .ThenByDescending(r => r.Width)
                .ThenBy(r => r.Path, StringComparer.Ordinal)
                .ToList();

            text.Append($"=== {title} ===  {group.Count} file(s)\n");
            if (group.Count > 0)
            {
                text.Append($"  {"colors",7}  {"size",9}  file\n");
                foreach (var r in group)
                    text.Append($"  {r.Colors,7}  {r.Width,4}x{r.Height,-4}  {r.Path}\n");
            }
            text.Append('\n');
        }

        if (failed.Count > 0)
        {
            text.Append("=== Could not read ===\n");
            foreach (var (path, error) in failed)
                text.Append($"  {path}: {error}\n");
            text.Append('\n');
        }

        // The Python joined its lines with "\n" (no newline after the last, which is an empty line).
        return text.ToString(0, text.Length - 1);
    }
}
