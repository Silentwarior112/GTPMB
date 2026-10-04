using System.Globalization;
using System.Text;
using GTPMB.Core.Pmb;

namespace GTPMB.Core.Mbl;

/// <summary>
/// The editable text form of an MBL, in the same style as pages.txt:
///
///   Menu {
///       variant string {"mbl40"}
///       pages [397] {
///           MenuPage {
///               name string {"ar/top"}
///               pmb digit {1}
///               flags digit {0x120}
///               return string {"ar/select_game"}
///               items [3] {
///                   Item {
///                       element string {"easy"}
///                       layer2 string {"layer1"}
///                       function string {"qm_start"}
///                       flags digit {0x2}
///                   }
///               }
///           }
///       }
///   }
///
/// Empty labels, a flags value of 0 and an item pmb of 0xFFFF (= none) are simply omitted.
/// "function" is a page name (this or another family) or a game-code action ('buy_car').
/// The rebuild is byte-identical to the original for every shipped MBL.
/// </summary>
public static class MenuText
{
    public static string Extract(string mblPath, string textPath, Action<string>? log = null)
    {
        MblFile mbl;
        try
        {
            mbl = MblFile.Read(File.ReadAllBytes(mblPath));
        }
        catch (InvalidDataException e)
        {
            throw new PmbToolException("Error", $"The MBL file is corrupt or not an MBL:\n{e.Message}", inner: e);
        }
        File.WriteAllText(textPath, Decompile(mbl), new UTF8Encoding(false));
        log?.Invoke($"MBL extracted: {mbl.Entries.Count} pages ({mbl.Variant}).");
        return textPath;
    }

    public static int Build(string textPath, string mblPath)
    {
        MblFile mbl;
        try
        {
            mbl = Parse(File.ReadAllText(textPath));
        }
        catch (InvalidDataException e)
        {
            throw new PmbToolException("Error", $"Menu source does not compile:\n{e.Message}", inner: e);
        }
        byte[] data;
        try
        {
            data = mbl.Write();
        }
        catch (InvalidDataException e)
        {
            throw new PmbToolException("Error", e.Message, inner: e);
        }
        File.WriteAllBytes(mblPath, data);
        return mbl.Entries.Count;
    }

    // ── Decompile ────────────────────────────────────────────────────────────

    public static string Decompile(MblFile mbl)
    {
        var text = new StringBuilder();
        text.Append("Menu {\n");
        text.Append($"\tvariant string {{\"{mbl.Variant}\"}}\n");
        text.Append($"\tpages [{mbl.Entries.Count}] {{\n");
        foreach (MblEntry entry in mbl.Entries)
        {
            text.Append("\t\tMenuPage {\n");
            Str(text, 3, "name", entry.Name);
            text.Append('\t', 3).Append($"pmb digit {{{entry.PmbIndex}}}\n");
            Hex(text, 3, "flags", entry.ButtonFlags);
            if (entry.Common.Length > 0)
                Str(text, 3, "common", entry.Common);
            if (entry.Return.Length > 0)
                Str(text, 3, "return", entry.Return);
            if (entry.Function.Length > 0)
                Str(text, 3, "function", entry.Function);
            Items(text, 3, entry.Subs);
            text.Append("\t\t}\n");
        }
        text.Append("\t}\n}\n");
        return text.ToString();
    }

    private static void Items(StringBuilder text, int depth, List<MblSub> subs)
    {
        if (subs.Count == 0)
            return;
        text.Append('\t', depth).Append($"items [{subs.Count}] {{\n");
        foreach (MblSub sub in subs)
        {
            text.Append('\t', depth + 1).Append("Item {\n");
            int d = depth + 2;
            Str(text, d, "element", sub.Element);
            if (sub.Layer1.Length > 0)
                Str(text, d, "layer1", sub.Layer1);
            if (sub.Layer2.Length > 0)
                Str(text, d, "layer2", sub.Layer2);
            if (sub.Function.Length > 0)
                Str(text, d, "function", sub.Function);
            if (sub.Return.Length > 0)
                Str(text, d, "return", sub.Return);
            if (sub.PmbIndex != 0xFFFF)
                text.Append('\t', d).Append($"pmb digit {{{sub.PmbIndex}}}\n");
            if (sub.ButtonFlags != 0)
                Hex(text, d, "flags", sub.ButtonFlags);
            Items(text, d, sub.Children);
            text.Append('\t', depth + 1).Append("}\n");
        }
        text.Append('\t', depth).Append("}\n");
    }

    private static void Str(StringBuilder text, int depth, string name, string value) =>
        text.Append('\t', depth).Append($"{name} string {{{Quote(value)}}}\n");

    private static void Hex(StringBuilder text, int depth, string name, ushort value) =>
        text.Append('\t', depth).Append($"{name} digit {{0x{value:X}}}\n");

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                default:
                    if (c < 0x20)
                        text.Append($"\\x{(int)c:X2}");
                    else
                        text.Append(c);
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    // ── Parse ────────────────────────────────────────────────────────────────

    public static MblFile Parse(string source)
    {
        var parser = new Parser(source);
        return parser.ParseRoot();
    }

    private sealed class Parser(string source)
    {
        private readonly string _s = source;
        private int _p;
        private int _line = 1;

        private InvalidDataException Error(string message) => new($"line {_line}: {message}");

        private void SkipSpace()
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (c == '\n') { _line++; _p++; }
                else if (char.IsWhiteSpace(c)) _p++;
                else if (c == '/' && _p + 1 < _s.Length && _s[_p + 1] == '/')
                {
                    while (_p < _s.Length && _s[_p] != '\n') _p++;
                }
                else break;
            }
        }

        private bool TryPeek(char c)
        {
            SkipSpace();
            return _p < _s.Length && _s[_p] == c;
        }

        private void Expect(char c)
        {
            SkipSpace();
            if (_p >= _s.Length || _s[_p] != c)
                throw Error($"expected '{c}'");
            _p++;
        }

        private string Ident()
        {
            SkipSpace();
            int start = _p;
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_'))
                _p++;
            if (_p == start)
                throw Error("expected a name");
            return _s[start.._p];
        }

        private string QuotedString()
        {
            SkipSpace();
            if (_p >= _s.Length || _s[_p] != '"')
                throw Error("expected a quoted string");
            _p++;
            var text = new StringBuilder();
            while (true)
            {
                if (_p >= _s.Length)
                    throw Error("unterminated string");
                char c = _s[_p++];
                if (c == '"')
                    break;
                if (c == '\\')
                {
                    if (_p >= _s.Length)
                        throw Error("unterminated escape");
                    char e = _s[_p++];
                    if (e == 'x')
                    {
                        if (_p + 2 > _s.Length)
                            throw Error("bad \\x escape");
                        text.Append((char)int.Parse(_s.AsSpan(_p, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _p += 2;
                    }
                    else
                    {
                        text.Append(e);
                    }
                }
                else
                {
                    text.Append(c);
                }
            }
            return text.ToString();
        }

        private uint Number()
        {
            SkipSpace();
            int start = _p;
            while (_p < _s.Length && (char.IsAsciiLetterOrDigit(_s[_p])))
                _p++;
            if (_p == start)
                throw Error("expected a number");
            string token = _s[start.._p];
            return token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.Parse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : uint.Parse(token, CultureInfo.InvariantCulture);
        }

        private string StringProp()
        {
            Ident(); Expect('{');
            string value = QuotedString();
            Expect('}');
            return value;
        }

        private uint NumberProp()
        {
            Ident(); Expect('{');
            uint value = Number();
            Expect('}');
            return value;
        }

        private void NodeList(Action one)
        {
            Expect('[');
            Number(); // decorative
            Expect(']');
            Expect('{');
            while (!TryPeek('}'))
                one();
            Expect('}');
        }

        public MblFile ParseRoot()
        {
            if (Ident() != "Menu")
                throw Error("the file must start with Menu");
            Expect('{');
            var mbl = new MblFile();
            while (!TryPeek('}'))
            {
                string name = Ident();
                switch (name)
                {
                    case "variant":
                        (mbl.NameSize, mbl.HasFunction) = MblFile.ParseVariant(StringProp());
                        break;
                    case "pages":
                        NodeList(() => mbl.Entries.Add(ParsePage()));
                        break;
                    default:
                        throw Error($"unknown Menu entry '{name}'");
                }
            }
            Expect('}');
            if (mbl.Entries.Count == 0)
                throw Error("the menu has no pages");
            return mbl;
        }

        private MblEntry ParsePage()
        {
            if (Ident() != "MenuPage")
                throw Error("expected MenuPage");
            Expect('{');
            var entry = new MblEntry();
            while (!TryPeek('}'))
            {
                string name = Ident();
                switch (name)
                {
                    case "name": entry.Name = StringProp(); break;
                    case "pmb": entry.PmbIndex = (ushort)NumberProp(); break;
                    case "flags": entry.ButtonFlags = (ushort)NumberProp(); break;
                    case "common": entry.Common = StringProp(); break;
                    case "return": entry.Return = StringProp(); break;
                    case "function": entry.Function = StringProp(); break;
                    case "items": NodeList(() => entry.Subs.Add(ParseItem())); break;
                    default: throw Error($"unknown MenuPage property '{name}'");
                }
            }
            Expect('}');
            if (entry.Name.Length == 0)
                throw Error("a MenuPage has no name");
            return entry;
        }

        private MblSub ParseItem()
        {
            if (Ident() != "Item")
                throw Error("expected Item");
            Expect('{');
            var sub = new MblSub();
            while (!TryPeek('}'))
            {
                string name = Ident();
                switch (name)
                {
                    case "element": sub.Element = StringProp(); break;
                    case "layer1": sub.Layer1 = StringProp(); break;
                    case "layer2": sub.Layer2 = StringProp(); break;
                    case "function": sub.Function = StringProp(); break;
                    case "return": sub.Return = StringProp(); break;
                    case "pmb": sub.PmbIndex = (ushort)NumberProp(); break;
                    case "flags": sub.ButtonFlags = (ushort)NumberProp(); break;
                    case "items": NodeList(() => sub.Children.Add(ParseItem())); break;
                    default: throw Error($"unknown Item property '{name}'");
                }
            }
            Expect('}');
            return sub;
        }
    }
}
