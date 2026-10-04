using System.Text;
using GTPMB.Core.Config;
using GTPMB.Core.Imaging;
using GTPMB.Core.Pmb.Pages;
using GTPMB.Core.Textures.Tex1;

namespace GTPMB.Core.Pmb;

/// <summary>"Extract PMB to folder".</summary>
public static class PmbExtractor
{
    private sealed class Extracted
    {
        public required string ConfigPath { get; init; }
        public ItemError? Error { get; init; }
    }

    /// <summary>
    /// Unpacks a PMB under <paramref name="destinationDir"/> and writes pmb_config.ini. The page
    /// structures go to pages.bin verbatim (they hold the menu layout; nothing in them moves when
    /// textures change). Every embedded file is un-gzipped; a single-texture Tex1 becomes an
    /// editable PNG named "NNN_&lt;its gzip name&gt;.png", anything else is written as a raw file and
    /// packed back as-is. In the shipped GT3 every one of the 33,567 embedded files is a
    /// single-texture Tex1.
    /// </summary>
    public static ExtractResult Extract(string pmbPath, string destinationDir, Action<string>? log = null)
    {
        byte[] file = File.ReadAllBytes(pmbPath);
        PmbFile pmb;
        try
        {
            pmb = PmbFile.Read(file);
        }
        catch (InvalidDataException e)
        {
            throw new PmbToolException("Error", $"The PMB file is corrupt:\n{e.Message}", inner: e);
        }

        Directory.CreateDirectory(destinationDir);
        var config = new IniFile();
        config.Set(PmbConfig.PmbSection, "empty", pmb.Empty ? "1" : "0");

        if (pmb.Empty)
        {
            log?.Invoke("Empty PMB (a zero-length file, as shipped).");
            string emptyConfig = Path.Combine(destinationDir, PmbConfig.ConfigName);
            config.Save(emptyConfig);
            return new ExtractResult { EntryCount = 0, ConfigPath = emptyConfig, Errors = [] };
        }

        log?.Invoke($"Pages: {pmb.PageCount}, embedded files: {pmb.Entries.Count}");

        var results = new Extracted[pmb.Entries.Count];
        var logLock = new object();
        Parallel.For(0, pmb.Entries.Count, PmbConfig.Parallelism, i =>
        {
            results[i] = ExtractEntry(pmb.Entries[i], i, destinationDir, message =>
            {
                if (log is not null)
                    lock (logLock) log(message);
            });
        });

        // The pages decompile to an editable pages.txt (mproject-style); the one shipped file in
        // an older page format (US arcade2.pmb) keeps its raw pages.bin instead.
        string pagesName = PmbConfig.PagesTextName;
        try
        {
            PageList pages = PageList.Read(pmb.PagesBlob);
            string text = PageText.Decompile(pages,
                index => index >= 0 && index < results.Length ? Path.GetFileName(results[index].ConfigPath) : null);
            File.WriteAllText(Path.Combine(destinationDir, pagesName), text, new UTF8Encoding(false));
            log?.Invoke($"[Pages] '{pagesName}' ({pmb.PageCount} page(s) decompiled)");
        }
        catch (InvalidDataException e)
        {
            pagesName = PmbConfig.PagesName;
            File.WriteAllBytes(Path.Combine(destinationDir, pagesName), pmb.PagesBlob);
            config.Set(PmbConfig.PmbSection, "page_count", pmb.PageCount.ToString());
            log?.Invoke($"[Pages] '{pagesName}' kept as raw data ({e.Message})");
        }
        config.Set(PmbConfig.PmbSection, "pages", pagesName);

        for (int i = 0; i < pmb.Entries.Count; i++)
        {
            PmbEntry entry = pmb.Entries[i];
            int n = i + 1;
            config.Set(PmbConfig.FilesSection, PmbConfig.PathKey(n), results[i].ConfigPath);
            config.Set(PmbConfig.FilesSection, PmbConfig.CompressionKey(n), entry.Compressed ? "1" : "0");
            if (entry.Compressed)
            {
                if (entry.GzipName is not null)
                    config.Set(PmbConfig.FilesSection, PmbConfig.GzipNameKey(n), entry.GzipName);
                config.Set(PmbConfig.FilesSection, PmbConfig.GzipMTimeKey(n), entry.GzipMTime.ToString());
                if (entry.GzipXfl != 0)
                    config.Set(PmbConfig.FilesSection, PmbConfig.GzipXflKey(n), entry.GzipXfl.ToString());
                if (entry.GzipOs != 0x0B)
                    config.Set(PmbConfig.FilesSection, PmbConfig.GzipOsKey(n), entry.GzipOs.ToString());
            }
        }

        string configPath = Path.Combine(destinationDir, PmbConfig.ConfigName);
        config.Save(configPath);

        log?.Invoke($"PMB extraction completed. {pmb.Entries.Count} files extracted.");
        return new ExtractResult
        {
            EntryCount = pmb.Entries.Count,
            ConfigPath = configPath,
            Errors = results.Where(r => r.Error is not null).Select(r => r.Error!).ToList(),
        };
    }

    /// <summary>"NNN" or "NNN_&lt;gzip name without .img&gt;", safe for Windows.</summary>
    private static string BaseName(PmbEntry entry, int index)
    {
        string number = (index + 1).ToString("D3");
        if (string.IsNullOrEmpty(entry.GzipName))
            return number;
        return number + "_" + PmbConfig.SafeFileName(PmbConfig.WithoutExtension(entry.GzipName));
    }

    private static Extracted ExtractEntry(PmbEntry entry, int index, string destinationDir, Action<string> log)
    {
        string baseName = BaseName(entry, index);
        bool isTex1 = Tex1Codec.IsTex1(entry.Content);
        try
        {
            string produced;
            if (isTex1 && Tex1Codec.GetTextureCount(entry.Content) == 1)
            {
                produced = Path.Combine(destinationDir, baseName + ".png");
                Png.Save(Tex1Codec.DecodeTexture(entry.Content), produced);
                log($"[Texture] '{Path.GetFileName(produced)}'");
            }
            else
            {
                // Not a single-texture Tex1: the file itself, packed back as-is on rebuild.
                produced = Path.Combine(destinationDir, baseName + (isTex1 ? ".img" : ".dat"));
                File.WriteAllBytes(produced, entry.Content);
                log($"[File] '{Path.GetFileName(produced)}'{(isTex1 ? " (Tex1 set with several textures: kept as a raw file)" : "")}");
            }
            return new Extracted { ConfigPath = PmbConfig.ToConfigPath(produced) };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Keep the entry's bytes as a raw .img so nothing is lost; the config points at it and packs it back.
            string scratch = Path.Combine(destinationDir, baseName + ".img");
            try
            {
                File.WriteAllBytes(scratch, entry.Content);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            log($"[Error] '{baseName}': {e.Message}");
            return new Extracted
            {
                ConfigPath = PmbConfig.ToConfigPath(scratch),
                Error = new ItemError(scratch, e.Message),
            };
        }
    }
}
