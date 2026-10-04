using System.Text;

namespace GTPMB.Core.Config;

/// <summary>
/// Reads and writes .ini files the way Python's configparser.ConfigParser does, so configs stay
/// interchangeable with the original GTGPB.py: "[Section]" headers, "key = value" (also "key: value")
/// pairs, keys lower-cased, "#" / ";" full-line comments, whitespace-indented continuation lines,
/// UTF-8. Written as "[Section]" then "key = value" lines then one blank line. Key order is preserved.
/// No %-interpolation is performed (paths may contain "%").
/// </summary>
/// <remarks>
/// Deliberately more lenient than configparser's strict mode: a repeated section is merged and a repeated
/// key keeps its first position with the last value, instead of failing. "[DEFAULT]" is an ordinary section.
/// </remarks>
public sealed class IniFile
{
    private sealed class Section
    {
        public readonly List<KeyValuePair<string, string>> Items = [];
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        /// <summary>Like a Python dict: a new key is appended, an existing key keeps its position.</summary>
        public void Set(string key, string value)
        {
            var item = new KeyValuePair<string, string>(key, value);
            if (_index.TryGetValue(key, out int at))
            {
                Items[at] = item;
            }
            else
            {
                _index.Add(key, Items.Count);
                Items.Add(item);
            }
        }
    }

    private readonly List<KeyValuePair<string, Section>> _sections = [];
    private readonly Dictionary<string, Section> _byName = new(StringComparer.Ordinal); // section names are case-sensitive

    /// <summary>
    /// Reads a config: strict UTF-8 first (a BOM is skipped); bytes that are not valid UTF-8 are taken as Latin-1
    /// instead (GTGPB.py read configs with the Windows locale code page, so such files exist).
    /// </summary>
    public static IniFile Load(string path)
    {
        ReadOnlySpan<byte> bytes = File.ReadAllBytes(path);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
            bytes = bytes[3..];

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        return Parse(text, path);
    }

    public static IniFile Parse(string text) => Parse(text, "<string>");

    // A line-for-line port of configparser.RawConfigParser._read with ConfigParser's defaults: delimiters "=" and
    // ":", comment prefixes "#" and ";", no inline comments, empty lines allowed inside multi-line values.
    private static IniFile Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ini = new IniFile();

        Section? section = null;
        string? option = null;             // the option that continuation lines extend; null right after a header
        var valueLines = new List<string>(); // its value so far, one element per line
        int indentLevel = 0;
        int lineNumber = 0;
        StringBuilder? errors = null;

        void FinishOption()
        {
            if (option == null)
                return;
            // configparser joins the lines with "\n" and strips the end (dropping trailing blank lines).
            string value = valueLines.Count == 1 ? valueLines[0] : Trim(string.Join('\n', valueLines), start: false).ToString();
            section!.Set(option, value);
            option = null;
            valueLines.Clear();
        }

        ReadOnlySpan<char> rest = text;
        if (rest.Length > 0 && rest[0] == '﻿')
            rest = rest[1..];

        while (!rest.IsEmpty)
        {
            // Universal newlines as in Python text mode: "\n", "\r" and "\r\n" only (not U+2028, form feed, ...).
            ReadOnlySpan<char> line;
            int eol = rest.IndexOfAny('\r', '\n');
            if (eol < 0)
            {
                line = rest;
                rest = default;
            }
            else
            {
                line = rest[..eol];
                rest = rest[(rest[eol] == '\r' && eol + 1 < rest.Length && rest[eol + 1] == '\n' ? eol + 2 : eol + 1)..];
            }
            lineNumber++;

            ReadOnlySpan<char> content = Trim(line);
            if (content.IsEmpty)
            {
                // An empty line is part of a multi-line value (trailing ones are stripped when the value is joined).
                if (option != null)
                    valueLines.Add("");
                continue;
            }
            if (content[0] is '#' or ';')
                continue;

            // Continuation line: indented deeper than the line its option started on.
            int indent = line.Length - Trim(line, end: false).Length;
            if (option != null && indent > indentLevel)
            {
                valueLines.Add(content.ToString());
                continue;
            }
            indentLevel = indent;

            // Section header, configparser's SECTCRE "\[(?P<header>.+)\]": greedy, so the name runs to the LAST "]"
            // and anything after it is ignored.
            int close = content.LastIndexOf(']');
            if (content[0] == '[' && close >= 2)
            {
                FinishOption();
                string name = content[1..close].ToString();
                if (!ini._byName.TryGetValue(name, out section))
                    section = ini.AddSection(name);
                continue;
            }

            if (section == null)
                throw new InvalidDataException($"File contains no section headers.\nfile: '{source}', line: {lineNumber}\n'{line}'");

            // Option line: the name is everything before the FIRST "=" or ":" (so values may contain both).
            int delimiter = content.IndexOfAny('=', ':');
            if (delimiter <= 0)
            {
                // Not "key = value". Like configparser, keep going and report every bad line at the end.
                (errors ??= new StringBuilder()).Append($"\n\t[line {lineNumber,2}]: '{line}'");
                continue;
            }

            FinishOption();
            option = Trim(content[..delimiter], start: false).ToString().ToLowerInvariant();
            valueLines.Add(Trim(content[(delimiter + 1)..]).ToString());
        }
        FinishOption();

        if (errors != null)
            throw new InvalidDataException($"Source contains parsing errors: '{source}'{errors}");
        return ini;
    }

    public bool HasSection(string section) => _byName.ContainsKey(section);

    /// <summary>Keys (lower-cased) and values of a section in file order; empty when the section is missing.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetSection(string section) =>
        _byName.TryGetValue(section, out Section? found) ? found.Items.AsReadOnly() : [];

    /// <summary>Sets a value, creating the section on demand. The key is lower-cased; an existing key keeps its position.</summary>
    public void Set(string section, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!_byName.TryGetValue(section, out Section? target))
            target = AddSection(section);
        target.Set(key.ToLowerInvariant(), value);
    }

    private Section AddSection(string name)
    {
        var section = new Section();
        _sections.Add(new KeyValuePair<string, Section>(name, section));
        _byName.Add(name, section);
        return section;
    }

    /// <summary>
    /// Writes <see cref="ToString"/> as UTF-8 without a BOM, with "\n" turned into the platform line ending -
    /// exactly what GTGPB.py's open(path, 'w', encoding='utf-8') + config.write() put on disk.
    /// </summary>
    public void Save(string path) =>
        File.WriteAllText(path, ToString().Replace("\n", Environment.NewLine), new UTF8Encoding(false));

    /// <summary>The text configparser.write() produces: "[Section]\n", "key = value\n" per key, then a blank line.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach ((string name, Section section) in _sections)
        {
            text.Append('[').Append(name).Append("]\n");
            foreach ((string key, string value) in section.Items)
                text.Append(key).Append(" = ").Append(value.Replace("\n", "\n\t")).Append('\n'); // continuation lines are tab-indented
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>str.strip() / lstrip() / rstrip() with Python's notion of whitespace.</summary>
    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> text, bool start = true, bool end = true)
    {
        int from = 0;
        int to = text.Length;
        if (start)
            while (from < to && IsSpace(text[from])) from++;
        if (end)
            while (to > from && IsSpace(text[to - 1])) to--;
        return text[from..to];
    }

    // Python's str.isspace() is .NET's char.IsWhiteSpace plus the four ASCII separators U+001C..U+001F.
    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '' and <= '';
}
