using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GTPMB.Core.Config;
using GTPMB.Core.Imaging;
using GTPMB.Core.Textures.Tex1;

namespace GTPMB.Core.Pmb;

/// <summary>"Generate PMB from .ini".</summary>
public static partial class PmbBuilder
{
    [GeneratedRegex(@"^file_(\d+)_(path|compression|gzip_name|gzip_mtime|gzip_xfl|gzip_os)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntryKey();

    private sealed class ConfigEntry
    {
        public string? Path;
        public bool Compress;
        public string? GzipName;
        public uint GzipMTime;
        public byte GzipXfl;
        public byte GzipOs = 0x0B;
    }

    /// <summary>
    /// Converts each [Files] entry to its packed form in memory (PNG -> Tex1, optional gzip wrap)
    /// and writes the PMB around the verbatim pages.bin. File order is the order the page
    /// structures reference textures by index, so entries build in config order. Nothing is
    /// written unless every entry converts; failures throw <see cref="PmbToolException"/>.
    /// </summary>
    public static int Build(string iniPath, string outputPmbPath, Action<string>? log = null)
    {
        IniFile ini;
        try
        {
            ini = IniFile.Load(iniPath);
        }
        catch (InvalidDataException e)
        {
            throw new PmbToolException("Error", e.Message, inner: e);
        }

        if (!ini.HasSection(PmbConfig.PmbSection))
            throw new PmbToolException("Error", $"Missing [{PmbConfig.PmbSection}] section in ini.");

        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(iniPath)) ?? string.Empty;
        var pmbKeys = ini.GetSection(PmbConfig.PmbSection).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        if (pmbKeys.TryGetValue("empty", out string? empty) && empty.Trim() == "1")
        {
            File.WriteAllBytes(outputPmbPath, []);
            return 0;
        }

        if (!pmbKeys.TryGetValue("pages", out string? pagesName))
            throw new PmbToolException("Error", "Missing 'pages' key in [Pmb].");
        string pagesPath = Path.GetFullPath(pagesName, baseDirectory);
        if (!File.Exists(pagesPath))
            throw new PmbToolException("Error", $"Pages file not found:\n{pagesPath}");
        bool pagesAreText = pagesName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        int pageCount = 0;
        if (!pagesAreText
            && (!pmbKeys.TryGetValue("page_count", out string? pageCountText)
                || !int.TryParse(pageCountText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pageCount)))
            throw new PmbToolException("Error", "Missing or invalid 'page_count' in [Pmb].");

        // [Files] entries in numeric order drive conversion + packing; order defines the indices
        // the pages reference.
        var items = new SortedDictionary<long, ConfigEntry>();
        foreach (var (key, value) in ini.GetSection(PmbConfig.FilesSection))
        {
            Match match = EntryKey().Match(key);
            if (!match.Success || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long index))
                continue;

            if (!items.TryGetValue(index, out ConfigEntry? entry))
                items.Add(index, entry = new ConfigEntry());
            switch (match.Groups[2].Value.ToLowerInvariant())
            {
                case "path": entry.Path = value; break;
                case "compression": entry.Compress = value.Trim() == "1"; break;
                case "gzip_name": entry.GzipName = value; break;
                case "gzip_mtime": entry.GzipMTime = ParseUInt(value, key); break;
                case "gzip_xfl": entry.GzipXfl = (byte)ParseUInt(value, key); break;
                default: entry.GzipOs = (byte)ParseUInt(value, key); break;
            }
        }

        foreach (var (index, entry) in items)
        {
            if (entry.Path is null)
                throw new PmbToolException("Error", $"Missing path for file_{index}.");
        }

        ConfigEntry[] entries = [.. items.Values];
        var contents = new byte[entries.Length][];
        var errors = new string?[entries.Length];
        var logLock = new object();
        Parallel.For(0, entries.Length, PmbConfig.Parallelism, i =>
        {
            try
            {
                contents[i] = Convert(Path.GetFullPath(entries[i].Path!, baseDirectory), message =>
                {
                    if (log is not null)
                        lock (logLock) log(message);
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                errors[i] = e.Message;
            }
        });

        int failed = errors.Count(e => e is not null);
        if (failed > 0)
        {
            var details = new StringBuilder("File conversion errors:\n");
            for (int i = 0; i < entries.Length; i++)
            {
                if (errors[i] is not null)
                    details.Append($"- {entries[i].Path}\n  {errors[i]}\n\n");
            }
            throw new PmbToolException("Conversion error",
                $"File conversion failed for {failed} file(s).\nSee the log window for details.\n\nAborting PMB generation.",
                details.ToString());
        }

        byte[] pagesBlob;
        if (pagesAreText)
        {
            // Compile the mproject-style page source; "image string" references resolve against
            // the [Files] entries' file names.
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Length; i++)
            {
                string file = Path.GetFileName(entries[i].Path!.Replace('/', '\\'));
                byName.TryAdd(file, i);
            }
            try
            {
                Pages.PageList pages = Pages.PageText.Parse(File.ReadAllText(pagesPath),
                    name => byName.TryGetValue(name, out int index) ? index : null);
                foreach (Pages.PmbPage page in pages.Pages)
                    ValidateTextureIndices(page, entries.Length);
                pagesBlob = pages.Write();
                pageCount = pages.Pages.Count;
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or OverflowException)
            {
                throw new PmbToolException("Error", $"Page source '{pagesName}' does not compile:\n{e.Message}", inner: e);
            }
        }
        else
        {
            pagesBlob = File.ReadAllBytes(pagesPath);
        }

        var pmb = new PmbFile { PageCount = pageCount, PagesBlob = pagesBlob };
        for (int i = 0; i < entries.Length; i++)
        {
            pmb.Entries.Add(new PmbEntry
            {
                Content = contents[i],
                Compressed = entries[i].Compress,
                GzipName = entries[i].GzipName,
                GzipMTime = entries[i].GzipMTime,
                GzipXfl = entries[i].GzipXfl,
                GzipOs = entries[i].GzipOs,
            });
        }

        File.WriteAllBytes(outputPmbPath, pmb.Write());
        return entries.Length;
    }

    private static void ValidateTextureIndices(Pages.PmbPage page, int fileCount)
    {
        foreach (Pages.PageElement element in page.Elements)
            Validate(element);
        foreach (Pages.PageLayer layer in page.Layers)
        {
            foreach (Pages.PageElement element in layer.GroupA.Concat(layer.GroupB))
                Validate(element);
        }

        void Validate(Pages.PageElement element)
        {
            if (element.Texture is { } texture && (texture.FileIndex < 0 || texture.FileIndex >= fileCount))
                throw new InvalidDataException(
                    $"page '{page.Name}', element '{element.Name}': image {texture.FileIndex + 1} is outside the {fileCount} [Files] entries.");
            if (element.Child is not null)
                Validate(element.Child);
        }
    }

    private static uint ParseUInt(string value, string key)
    {
        string text = value.Trim();
        bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)
            : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        if (!ok)
            throw new InvalidDataException($"'{key}' is not a number: {value}");
        return parsed;
    }

    /// <summary>Routes a config path by kind and returns the file that gets packed.</summary>
    private static byte[] Convert(string path, Action<string> log)
    {
        // A plain .png is a Tex1 source; the format is picked from the image's distinct colour
        // count (up to 16 -> PSMT4, up to 256 -> PSMT8, more -> PSMCT32) - never quantized.
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return Tex1Builder.Build(Png.Load(path));

        // Anything else is packed verbatim.
        if (!File.Exists(path))
            throw new FileNotFoundException($"Raw file not found:\n{path}");
        return File.ReadAllBytes(path);
    }
}
