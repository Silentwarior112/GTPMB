namespace GTPMB.Core.Pmb;

// The UI-free ports of the button handlers live in PmbExtractor, PmbBuilder, MenuText,
// MenuSource and ColorInspector. They run on a worker thread: they never touch UI, report
// through the log callback, and signal problems the user must see by throwing PmbToolException.

/// <summary>A failure the UI shows as a message box: <see cref="Title"/> is the box caption, Message the text, <see cref="Details"/> an optional per-file log.</summary>
public sealed class PmbToolException : Exception
{
    public PmbToolException(string title, string message, string? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Title = title;
        Details = details;
    }

    public string Title { get; }
    public string? Details { get; }
}

/// <summary>One file that failed to convert.</summary>
public sealed record ItemError(string Path, string Message);

public sealed class ExtractResult
{
    public required int EntryCount { get; init; }
    public required string ConfigPath { get; init; }

    /// <summary>Entries whose conversion failed (extraction + config still completed).</summary>
    public required IReadOnlyList<ItemError> Errors { get; init; }
}

/// <summary>What the extract / build code shares: the config vocabulary and path rules.</summary>
internal static class PmbConfig
{
    public const string ConfigName = "pmb_config.ini";
    public const string PagesName = "pages.bin";
    public const string PagesTextName = "pages.txt";

    public const string PmbSection = "Pmb";
    public const string FilesSection = "Files";

    public static string PathKey(int index) => $"file_{index}_path";
    public static string CompressionKey(int index) => $"file_{index}_compression";
    public static string GzipNameKey(int index) => $"file_{index}_gzip_name";
    public static string GzipMTimeKey(int index) => $"file_{index}_gzip_mtime";
    public static string GzipXflKey(int index) => $"file_{index}_gzip_xfl";
    public static string GzipOsKey(int index) => $"file_{index}_gzip_os";

    public static ParallelOptions Parallelism => new() { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) };

    /// <summary>Config paths are written absolute with forward slashes; relative ones resolve against the ini's folder.</summary>
    public static string ToConfigPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>A file name built from an entry's gzip FNAME: characters Windows cannot have become '_'.</summary>
    public static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)).TrimEnd(' ', '.');
        return safe.Length == 0 ? "_unnamed_" : safe;
    }

    /// <summary>os.path.splitext-style: the extension starts at the last dot; ".hidden" has none.</summary>
    public static string WithoutExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 && fileName.AsSpan(0, dot).TrimStart('.').Length > 0 ? fileName[..dot] : fileName;
    }
}
