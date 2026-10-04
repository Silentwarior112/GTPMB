using GTPMB.Core.Config;

namespace GTPMB.Core.Pmb;

/// <summary>One content file of an extracted-PMB folder, as "Update PMB file entries" sees it.</summary>
public sealed class ScannedEntry
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }

    /// <summary>The file matched an entry of the folder's config (its metadata is carried over).</summary>
    public bool Known { get; init; }

    /// <summary>The default compression choice: the config's setting for known files, off for new ones.</summary>
    public bool Compressed { get; set; }

    public string? GzipName { get; init; }
    public string? GzipMTime { get; init; }
    public string? GzipXfl { get; init; }
    public string? GzipOs { get; init; }
}

/// <summary>"Update PMB file entries".</summary>
public static class PmbConfigGenerator
{
    public const string FileName = "generated_pmb_config.ini";

    private sealed class FolderState
    {
        public string Root = "";
        public string? PagesValue, PageCountValue, EmptyValue;
        public List<ScannedEntry> Entries = [];
    }

    /// <summary>
    /// Lists the folder's content files in ordinal name order - that order IS the index the
    /// page structures reference, which is why the extractor prefixes every file with its
    /// number. A file whose name matches an entry of pmb_config.ini / generated_pmb_config.ini
    /// keeps that entry's compression and gzip metadata; a file the configs do not know
    /// defaults to uncompressed.
    /// </summary>
    public static List<ScannedEntry> Scan(string rootFolder) => ScanFolder(rootFolder).Entries;

    private static FolderState ScanFolder(string rootFolder)
    {
        var state = new FolderState { Root = Path.GetFullPath(rootFolder) };

        // Metadata from the previous config, keyed by the on-disk file name.
        var known = new Dictionary<string, (string Compression, string? Name, string? MTime, string? Xfl, string? Os)>(StringComparer.OrdinalIgnoreCase);
        foreach (string configName in new[] { PmbConfig.ConfigName, FileName })
        {
            string path = Path.Combine(state.Root, configName);
            if (!File.Exists(path))
                continue;
            IniFile old;
            try
            {
                old = IniFile.Load(path);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            foreach (var (key, value) in old.GetSection(PmbConfig.PmbSection))
            {
                switch (key)
                {
                    case "pages": state.PagesValue ??= value; break;
                    case "page_count": state.PageCountValue ??= value; break;
                    case "empty": state.EmptyValue ??= value; break;
                }
            }
            var bucket = new Dictionary<long, (string? Path, string Compression, string? Name, string? MTime, string? Xfl, string? Os)>();
            foreach (var (key, value) in old.GetSection(PmbConfig.FilesSection))
            {
                var parts = key.Split('_', 3);
                if (parts.Length < 3 || parts[0] != "file" || !long.TryParse(parts[1], out long index))
                    continue;
                var entry = bucket.TryGetValue(index, out var found) ? found : (null, "1", null, null, null, null);
                switch (parts[2])
                {
                    case "path": entry.Path = value; break;
                    case "compression": entry.Compression = value.Trim(); break;
                    case "gzip_name": entry.Name = value; break;
                    case "gzip_mtime": entry.MTime = value; break;
                    case "gzip_xfl": entry.Xfl = value; break;
                    case "gzip_os": entry.Os = value; break;
                }
                bucket[index] = entry;
            }
            foreach (var entry in bucket.Values)
            {
                if (entry.Path is not null)
                    known.TryAdd(Path.GetFileName(entry.Path.Replace('/', '\\')), (entry.Compression, entry.Name, entry.MTime, entry.Xfl, entry.Os));
            }
        }

        foreach (string file in Directory.GetFiles(state.Root)
                     .Where(f => IsContentFile(Path.GetFileName(f), state.PagesValue))
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (known.TryGetValue(name, out var meta))
            {
                state.Entries.Add(new ScannedEntry
                {
                    FileName = name,
                    FullPath = file,
                    Known = true,
                    Compressed = meta.Compression == "1",
                    GzipName = meta.Name,
                    GzipMTime = meta.MTime,
                    GzipXfl = meta.Xfl,
                    GzipOs = meta.Os,
                });
            }
            else
            {
                // Not in any config: default off, per the workflow (the user opts files in).
                state.Entries.Add(new ScannedEntry { FileName = name, FullPath = file, Known = false, Compressed = false });
            }
        }
        return state;
    }

    private static bool IsContentFile(string name, string? pagesValue) =>
        !name.Equals(PmbConfig.PagesName, StringComparison.OrdinalIgnoreCase)
        && !name.Equals(PmbConfig.PagesTextName, StringComparison.OrdinalIgnoreCase)
        && !(pagesValue is not null && name.Equals(pagesValue, StringComparison.OrdinalIgnoreCase))
        && !name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".pmb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes generated_pmb_config.ini for the folder. <paramref name="compression"/> overrides
    /// the per-file compression choice by file name (what the checkbox list collected); files it
    /// does not mention keep their scanned default.
    /// </summary>
    public static (string ConfigPath, int EntryCount) Generate(string rootFolder, IReadOnlyDictionary<string, bool>? compression = null)
    {
        FolderState state = ScanFolder(rootFolder);

        var config = new IniFile();
        string pagesName = state.PagesValue
            ?? (File.Exists(Path.Combine(state.Root, PmbConfig.PagesTextName)) ? PmbConfig.PagesTextName : PmbConfig.PagesName);
        string pagesPath = Path.Combine(state.Root, pagesName);
        if (!File.Exists(pagesPath) && state.Entries.Count == 0)
        {
            config.Set(PmbConfig.PmbSection, "empty", "1");
        }
        else
        {
            if (!File.Exists(pagesPath))
                throw new PmbToolException("Error", $"No {PmbConfig.PagesTextName} / {PmbConfig.PagesName} in the folder - not an extracted PMB:\n{state.Root}");
            config.Set(PmbConfig.PmbSection, "empty", state.EmptyValue ?? "0");
            if (pagesName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                config.Set(PmbConfig.PmbSection, "page_count", state.PageCountValue ?? DerivePageCount(pagesPath).ToString());
            config.Set(PmbConfig.PmbSection, "pages", pagesName);
        }

        for (int i = 0; i < state.Entries.Count; i++)
        {
            ScannedEntry entry = state.Entries[i];
            bool compress = compression is not null && compression.TryGetValue(entry.FileName, out bool choice)
                ? choice
                : entry.Compressed;
            int n = i + 1;
            config.Set(PmbConfig.FilesSection, PmbConfig.PathKey(n), PmbConfig.ToConfigPath(entry.FullPath));
            config.Set(PmbConfig.FilesSection, PmbConfig.CompressionKey(n), compress ? "1" : "0");
            if (!compress)
                continue;
            // A compressed entry gets its stored gzip header fields back, or fresh ones
            // named after the file itself.
            config.Set(PmbConfig.FilesSection, PmbConfig.GzipNameKey(n),
                entry.GzipName ?? StripIndex(PmbConfig.WithoutExtension(entry.FileName)) + ".img");
            config.Set(PmbConfig.FilesSection, PmbConfig.GzipMTimeKey(n), entry.GzipMTime ?? "0");
            if (entry.GzipXfl is not null)
                config.Set(PmbConfig.FilesSection, PmbConfig.GzipXflKey(n), entry.GzipXfl);
            if (entry.GzipOs is not null)
                config.Set(PmbConfig.FilesSection, PmbConfig.GzipOsKey(n), entry.GzipOs);
        }

        string configPath = Path.Combine(state.Root, FileName);
        config.Save(configPath);
        return (configPath, state.Entries.Count);
    }

    /// <summary>pages.bin starts with the page offset table; entry 0 says where it ends.</summary>
    private static int DerivePageCount(string pagesPath)
    {
        using var stream = File.OpenRead(pagesPath);
        Span<byte> first = stackalloc byte[4];
        if (stream.Read(first) < 4)
            return 0;
        int firstPage = BitConverter.ToInt32(first);
        int count = (firstPage - PmbFile.HeaderSize) / 4;
        if (count < 0 || firstPage % 4 != 0)
            throw new PmbToolException("Error", $"{PmbConfig.PagesName} does not start with a page table.");
        return count;
    }

    /// <summary>"012_name" -> "name" (the extractor's index prefix is not part of the dev name).</summary>
    private static string StripIndex(string name)
    {
        int underscore = name.IndexOf('_');
        return underscore is >= 1 and <= 4 && name.AsSpan(0, underscore).IndexOfAnyExceptInRange('0', '9') < 0
            ? name[(underscore + 1)..]
            : name;
    }
}
