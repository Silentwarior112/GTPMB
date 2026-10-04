using System.Text;
using GTPMB.Core.Config;
using GTPMB.Core.Mbl;
using GTPMB.Core.Pmb.Pages;

namespace GTPMB.Core.Pmb;

/// <summary>
/// The menu source tree: one MBL family and all its numbered PMBs as one editable folder.
///
///   &lt;family&gt;/
///     menu.txt            the menu list (pages, flags, returns, items)
///     &lt;index&gt;/            one folder per "&lt;family&gt;&lt;index&gt;.pmb"
///       pages.txt         the page structures, decompiled
///       NNN_*.png         the textures
///       pmb_config.ini    build inputs (file order, gzip metadata)
///
/// Extract pulls a family apart, Build compiles the folder back into .mbl + .pmb files, and
/// Check validates the connections between the two sides: every menu entry must have its page,
/// every item its element, every target its destination.
/// </summary>
public static class MenuSource
{
    public const string MenuConfigName = "menu.txt";

    /// <summary>"family123.pmb" -> 123, for files of this family (undotted scheme only).</summary>
    private static int? PmbIndexOf(string family, string fileName)
    {
        if (!fileName.StartsWith(family, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".pmb", StringComparison.OrdinalIgnoreCase))
            return null;
        ReadOnlySpan<char> middle = fileName.AsSpan(family.Length, fileName.Length - family.Length - 4);
        return middle.Length > 0 && int.TryParse(middle, out int index) && index >= 0 ? index : null;
    }

    // ── Extract ──────────────────────────────────────────────────────────────

    public sealed class ExtractTreeResult
    {
        public required string Root { get; init; }
        public required int PmbCount { get; init; }
        public required int PageCount { get; init; }
        public required IReadOnlyList<ItemError> Errors { get; init; }
    }

    /// <summary>
    /// Extracts <paramref name="mblPath"/> and every "&lt;family&gt;N.pmb" in
    /// <paramref name="pmbFolder"/> into "&lt;destination&gt;\&lt;family&gt;".
    /// </summary>
    public static ExtractTreeResult ExtractTree(string mblPath, string pmbFolder, string destination, Action<string>? log = null)
    {
        string family = PmbConfig.WithoutExtension(Path.GetFileName(mblPath));
        string root = Path.Combine(destination, family);
        Directory.CreateDirectory(root);

        log?.Invoke($"[Menu] {family}.mbl");
        MenuText.Extract(mblPath, Path.Combine(root, MenuConfigName));
        int pageCount = MblFile.Read(File.ReadAllBytes(mblPath)).Entries.Count;

        var errors = new List<ItemError>();
        var indices = new List<int>();
        foreach (string file in Directory.GetFiles(pmbFolder, "*.pmb"))
        {
            if (PmbIndexOf(family, Path.GetFileName(file)) is not { } index)
                continue;
            indices.Add(index);
            log?.Invoke($"[PMB] {Path.GetFileName(file)} -> {index}\\");
            ExtractResult result = PmbExtractor.Extract(file, Path.Combine(root, index.ToString()), log);
            errors.AddRange(result.Errors);
        }
        if (indices.Count == 0)
            throw new PmbToolException("Error", $"No {family}<N>.pmb files found in:\n{pmbFolder}");

        return new ExtractTreeResult { Root = root, PmbCount = indices.Count, PageCount = pageCount, Errors = errors };
    }

    // ── Build ────────────────────────────────────────────────────────────────

    public sealed class BuildTreeResult
    {
        public required int PmbCount { get; init; }
        public required int FileCount { get; init; }
        public required string MblPath { get; init; }
    }

    /// <summary>
    /// Compiles a source tree back: "&lt;source&gt;\menu.txt" -> "&lt;destination&gt;\&lt;family&gt;.mbl" and
    /// every numeric subfolder -> "&lt;destination&gt;\&lt;family&gt;&lt;N&gt;.pmb". The family is the source
    /// folder's name. With <paramref name="updateEntries"/> every folder's file entries are
    /// regenerated first (order by file name, metadata carried over), so added / removed /
    /// renamed textures are picked up without a manual "Update PMB file entries" per folder.
    /// </summary>
    public static BuildTreeResult BuildTree(string sourceRoot, string destination, bool updateEntries = false, Action<string>? log = null)
    {
        string family = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
        string menuConfig = Path.Combine(sourceRoot, MenuConfigName);
        if (!File.Exists(menuConfig))
            throw new PmbToolException("Error", $"No {MenuConfigName} in:\n{sourceRoot}\n\nNot a menu source tree.");
        Directory.CreateDirectory(destination);

        string mblPath = Path.Combine(destination, family + ".mbl");
        int entries = MenuText.Build(menuConfig, mblPath);
        log?.Invoke($"[Menu] {family}.mbl ({entries} pages)");

        int pmbCount = 0, fileCount = 0;
        foreach (string folder in Directory.GetDirectories(sourceRoot).OrderBy(NumericName))
        {
            if (NumericName(folder) is not { } index)
                continue;
            string config = Path.Combine(folder, PmbConfig.ConfigName);
            string generated = Path.Combine(folder, PmbConfigGenerator.FileName);
            if (updateEntries)
            {
                // Rescan the folder's files: order by name, metadata carried over, new files
                // uncompressed. What "Update PMB file entries" does, for every folder at once.
                config = PmbConfigGenerator.Generate(folder).ConfigPath;
                log?.Invoke($"[Entries] {family}{index}: file entries regenerated");
            }
            else if (File.Exists(generated) && (!File.Exists(config) || File.GetLastWriteTimeUtc(generated) > File.GetLastWriteTimeUtc(config)))
            {
                config = generated;
            }
            if (!File.Exists(config))
                throw new PmbToolException("Error", $"No pmb_config.ini in:\n{folder}");
            string pmbPath = Path.Combine(destination, $"{family}{index}.pmb");
            fileCount += PmbBuilder.Build(config, pmbPath, log);
            pmbCount++;
            log?.Invoke($"[PMB] {family}{index}.pmb");
        }
        if (pmbCount == 0)
            throw new PmbToolException("Error", $"No numeric PMB subfolders in:\n{sourceRoot}");

        return new BuildTreeResult { PmbCount = pmbCount, FileCount = fileCount, MblPath = mblPath };
    }

    private static int? NumericName(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return int.TryParse(name, out int index) && index >= 0 ? index : null;
    }

    // ── Check ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the connections of a source tree and reports them: every menu entry's page,
    /// every item's element on its page, every function and return label, plus a summary of
    /// what the menu graph looks like. Returns the report; findings are lines starting with "!".
    /// </summary>
    public static string CheckTree(string sourceRoot)
    {
        string family = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
        string menuConfig = Path.Combine(sourceRoot, MenuConfigName);
        if (!File.Exists(menuConfig))
            throw new PmbToolException("Error", $"No {MenuConfigName} in:\n{sourceRoot}\n\nNot a menu source tree.");

        var report = new StringBuilder();
        var findings = new List<string>();
        report.AppendLine($"Menu source tree '{family}' - {sourceRoot}");

        MblFile menu;
        try
        {
            menu = MenuText.Parse(File.ReadAllText(menuConfig));
        }
        catch (InvalidDataException e)
        {
            throw new PmbToolException("Error", $"{MenuConfigName} does not compile:\n{e.Message}", inner: e);
        }

        // The page side: every numeric folder's pages.txt (or raw pages.bin, which cannot be checked).
        var pagesByIndex = new Dictionary<int, Dictionary<string, HashSet<string>>>(); // index -> page name -> element names
        foreach (string folder in Directory.GetDirectories(sourceRoot).OrderBy(NumericName))
        {
            if (NumericName(folder) is not { } index)
                continue;
            string pagesPath = Path.Combine(folder, PmbConfig.PagesTextName);
            if (!File.Exists(pagesPath))
            {
                if (File.Exists(Path.Combine(folder, PmbConfig.PagesName)))
                    findings.Add($"! {index}\\: raw {PmbConfig.PagesName} (old page format) - its pages cannot be cross-checked");
                else if (!File.Exists(Path.Combine(folder, PmbConfig.ConfigName)))
                    findings.Add($"! {index}\\: no page source and no config");
                continue;
            }
            PageList pages;
            try
            {
                pages = PageText.Parse(File.ReadAllText(pagesPath), _ => 0); // texture names checked by the build, not here
            }
            catch (InvalidDataException e)
            {
                findings.Add($"! {index}\\{PmbConfig.PagesTextName}: {e.Message}");
                continue;
            }
            var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (PmbPage page in pages.Pages)
            {
                var elements = new HashSet<string>(StringComparer.Ordinal);
                void Collect(PageElement element)
                {
                    if (element.Name.Length > 0)
                        elements.Add(element.Name);
                    if (element.Child is not null)
                        Collect(element.Child);
                }
                foreach (PageElement element in page.Elements)
                    Collect(element);
                foreach (PageLayer layer in page.Layers)
                {
                    if (layer.Name.Length > 0)
                        elements.Add(layer.Name);
                    foreach (PageElement element in layer.GroupA.Concat(layer.GroupB))
                        Collect(element);
                }
                if (!byName.TryAdd(page.Name, elements))
                    findings.Add($"! {index}\\: two pages named '{page.Name}'");
            }
            pagesByIndex[index] = byName;
        }

        // Cross-checks. An item's element must exist on its page; targets and parents may name
        // pages of OTHER menu families (default.mbl jumps to 'qm_start'), so unresolved ones are
        // notes, not findings.
        var notes = new List<string>();
        var entryNames = menu.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var referencedPages = new HashSet<(int, string)>();
        foreach (MblEntry entry in menu.Entries)
        {
            string where = $"menu page '{entry.Name}'";
            if (!pagesByIndex.TryGetValue(entry.PmbIndex, out var pages))
            {
                findings.Add($"! {where}: pmb {entry.PmbIndex} has no source folder");
                continue;
            }
            if (!pages.TryGetValue(entry.Name, out var elements))
            {
                findings.Add($"! {where}: no page of that name in {entry.PmbIndex}\\{PmbConfig.PagesTextName}");
                continue;
            }
            referencedPages.Add((entry.PmbIndex, entry.Name));
            if (entry.Return.Length > 0 && !entryNames.Contains(entry.Return))
                notes.Add($"? {where}: return '{entry.Return}' is outside this menu (another family's page, or a game action)");

            void CheckSubs(List<MblSub> subs, string path)
            {
                foreach (MblSub sub in subs)
                {
                    if (sub.Element.Length > 0 && !elements.Contains(sub.Element))
                        findings.Add($"! {where}, item '{path}{sub.Element}': no element of that name on the page");
                    if (sub.Function.Length > 0 && !entryNames.Contains(sub.Function))
                        notes.Add($"? {where}, item '{path}{sub.Element}': function '{sub.Function}' is outside this menu (another family's page, or a game action like 'buy_car')");
                    CheckSubs(sub.Children, path + sub.Element + "/");
                }
            }
            CheckSubs(entry.Subs, "");
        }

        // Pages nothing points at (informational: the game may still reach them by code).
        var orphans = pagesByIndex
            .SelectMany(kv => kv.Value.Keys.Select(name => (Index: kv.Key, Name: name)))
            .Where(page => !referencedPages.Contains((page.Index, page.Name)))
            .ToList();

        report.AppendLine($"{menu.Entries.Count} menu pages, {pagesByIndex.Count} PMB folders, "
            + $"{pagesByIndex.Sum(kv => kv.Value.Count)} pages in source");
        report.AppendLine();
        if (findings.Count == 0)
        {
            report.AppendLine("All connections check out: every menu page exists in its PMB and every"
                + " item names an element on its page.");
        }
        else
        {
            report.AppendLine($"{findings.Count} finding(s):");
            foreach (string finding in findings)
                report.AppendLine(finding);
        }
        if (notes.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"{notes.Count} cross-menu or unresolved reference(s):");
            foreach (string note in notes)
                report.AppendLine(note);
        }
        if (orphans.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"{orphans.Count} page(s) no menu entry points at (reachable by game code only):");
            foreach (var (index, name) in orphans)
                report.AppendLine($"  {index}\\ '{name}'");
        }
        return report.ToString();
    }
}
