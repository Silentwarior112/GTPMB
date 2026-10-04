using System.Globalization;
using System.Text;

namespace GTPMB.Core.Pmb.Pages;

/// <summary>
/// The editable text form of a PMB's pages, styled after GT4's .mproject files (the later
/// generation of the same UI system):
///
///   PageList {
///       name_size digit {64}
///       pages [1] {
///           Page {
///               name string {"arcade_mini"}
///               elements [1] { Composite { ... children [1] { ... } } }
///               layers [1] { Layer { name string {...} group_a [n] {...} group_b [n] {...} } }
///           }
///       }
///   }
///
/// Element node names are readability sugar (Composite / TextFace / ImageFace / ColorFace /
/// Face); the on-disk type byte is derived from which properties are present, which matches
/// the whole shipped game with zero exceptions. Textures are referenced by extracted file name
/// ("image string") or 1-based index ("image digit").
/// </summary>
public static class PageText
{
    // ── Decompile ────────────────────────────────────────────────────────────

    /// <summary><paramref name="fileName"/> maps a 0-based file index to the extracted file's name (null = emit indices).</summary>
    public static string Decompile(PageList list, Func<int, string?>? fileName = null)
    {
        var text = new StringBuilder();
        text.Append("PageList {\n");
        text.Append($"\tname_size digit {{{list.NameSize}}}\n");
        text.Append($"\tpages [{list.Pages.Count}] {{\n");
        foreach (PmbPage page in list.Pages)
        {
            text.Append("\t\tPage {\n");
            Prop(text, 3, "name", page.Name);
            NodeList(text, 3, "elements", page.Elements, e => Element(text, 4, e, fileName));
            NodeList(text, 3, "layers", page.Layers, layer =>
            {
                text.Append('\t', 4).Append("Layer {\n");
                Prop(text, 5, "name", layer.Name);
                NodeList(text, 5, "group_a", layer.GroupA, e => Element(text, 6, e, fileName));
                NodeList(text, 5, "group_b", layer.GroupB, e => Element(text, 6, e, fileName));
                text.Append('\t', 4).Append("}\n");
            });
            text.Append("\t\t}\n");
        }
        text.Append("\t}\n}\n");
        return text.ToString();
    }

    private static void NodeList<T>(StringBuilder text, int depth, string name, List<T> items, Action<T> write)
    {
        if (items.Count == 0)
            return; // an empty list is simply absent; the compiler emits count 0
        text.Append('\t', depth).Append($"{name} [{items.Count}] {{\n");
        foreach (T item in items)
            write(item);
        text.Append('\t', depth).Append("}\n");
    }

    private static string NodeName(PageElement e) =>
        e.Child is not null ? "Composite"
        : e.Text is not null ? "TextFace"
        : e.Texture is not null ? "ImageFace"
        : e.Colors is not null ? "ColorFace"
        : "Face";

    private static void Element(StringBuilder text, int depth, PageElement e, Func<int, string?>? fileName)
    {
        text.Append('\t', depth).Append(NodeName(e)).Append(" {\n");
        int d = depth + 1;
        Prop(text, d, "name", e.Name);
        if (e.Sub != 0)
            text.Append('\t', d).Append($"sub digit {{{e.Sub}}}\n");
        text.Append('\t', d).Append($"flags digit {{{e.Flags}}}\n");
        text.Append('\t', d).Append($"geometry rectangle {{{F(e.X)} {F(e.Y)} {F(e.W)} {F(e.H)}}}\n");
        if (e.Z != 0f || float.IsNegative(e.Z))
            text.Append('\t', d).Append($"z digit {{{F(e.Z)}}}\n");
        if (e.Arrow)
            text.Append('\t', d).Append("arrow digit {1}\n");
        if (e.Key is not null)
            Prop(text, d, "key", e.Key);
        if (e.Texture is not null)
        {
            string? name = fileName?.Invoke(e.Texture.FileIndex);
            text.Append('\t', d).Append(name is not null
                ? $"image string {{{Quote(name)}}}\n"
                : $"image digit {{{e.Texture.FileIndex + 1}}}\n");
            text.Append('\t', d).Append($"uv vector {{{string.Join(' ', e.Texture.Uv.Select(F))}}}\n");
        }
        if (e.Colors is not null)
        {
            for (int i = 0; i < 4; i++)
                text.Append('\t', d).Append($"color{i} RGBA {{{Rgba(e.Colors[i])}}}\n");
        }
        if (e.Text is not null)
        {
            if (e.Text.Code1 != 0)
                text.Append('\t', d).Append($"text_code1 digit {{{e.Text.Code1}}}\n");
            if (e.Text.Code2 != 0)
                text.Append('\t', d).Append($"text_code2 digit {{{e.Text.Code2}}}\n");
            text.Append('\t', d).Append($"text_color RGBA {{{Rgba(e.Text.Rgba)}}}\n");
            if (e.Text.Text is not null)
                Prop(text, d, "text", e.Text.Text);
            if (e.Text.Font is not null)
                Prop(text, d, "font", e.Text.Font);
        }
        if (e.Child is not null)
        {
            text.Append('\t', d).Append("children [1] {\n");
            Element(text, d + 1, e.Child, fileName);
            text.Append('\t', d).Append("}\n");
        }
        text.Append('\t', depth).Append("}\n");
    }

    private static void Prop(StringBuilder text, int depth, string name, string value) =>
        text.Append('\t', depth).Append($"{name} string {{{Quote(value)}}}\n");

    private static string F(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Rgba(uint value) =>
        $"{value & 0xFF} {(value >> 8) & 0xFF} {(value >> 16) & 0xFF} {value >> 24}";

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
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

    // ── Parse / compile ──────────────────────────────────────────────────────

    /// <summary><paramref name="fileIndex"/> maps an "image string" file name to its 0-based index (null = indices only).</summary>
    public static PageList Parse(string source, Func<string, int?>? fileIndex = null)
    {
        var parser = new Parser(source, fileIndex);
        return parser.ParseRoot();
    }

    private sealed class Parser(string source, Func<string, int?>? fileIndex)
    {
        private readonly string _s = source;
        private int _p;
        private int _line = 1;

        private InvalidDataException Error(string message) => new($"pages.txt line {_line}: {message}");

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
                    text.Append(e switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'x' => Hex2(),
                        _ => e,
                    });
                }
                else
                {
                    text.Append(c);
                }
            }
            return text.ToString();

            char Hex2()
            {
                if (_p + 2 > _s.Length)
                    throw Error("bad \\x escape");
                char value = (char)int.Parse(_s.AsSpan(_p, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _p += 2;
                return value;
            }
        }

        private float Number()
        {
            SkipSpace();
            int start = _p;
            while (_p < _s.Length && (char.IsAsciiLetterOrDigit(_s[_p]) || _s[_p] is '-' or '+' or '.'))
                _p++;
            if (_p == start)
                throw Error("expected a number");
            string token = _s[start.._p];
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return float.Parse(token, CultureInfo.InvariantCulture);
        }

        private List<float> NumbersUntilBrace()
        {
            var values = new List<float>();
            while (!TryPeek('}'))
                values.Add(Number());
            return values;
        }

        public PageList ParseRoot()
        {
            if (Ident() != "PageList")
                throw Error("the file must start with PageList");
            Expect('{');
            var list = new PageList();
            while (!TryPeek('}'))
            {
                string name = Ident();
                switch (name)
                {
                    case "name_size":
                        Ident(); Expect('{');
                        list.NameSize = (int)Number();
                        if (list.NameSize is not (0x20 or 0x40))
                            throw Error("name_size must be 32 or 64");
                        Expect('}');
                        break;
                    case "pages":
                        ParseNodeList(page => list.Pages.Add(ParsePage()));
                        break;
                    default:
                        throw Error($"unknown PageList entry '{name}'");
                }
            }
            Expect('}');
            return list;
        }

        /// <summary>Reads "[n] { ... }" and invokes <paramref name="one"/> per node until the closing brace.</summary>
        private void ParseNodeList(Action<int> one)
        {
            Expect('[');
            Number(); // the declared count is decorative; the nodes decide
            Expect(']');
            Expect('{');
            int i = 0;
            while (!TryPeek('}'))
                one(i++);
            Expect('}');
        }

        private PmbPage ParsePage()
        {
            if (Ident() != "Page")
                throw Error("expected Page");
            Expect('{');
            var page = new PmbPage();
            while (!TryPeek('}'))
            {
                string name = Ident();
                switch (name)
                {
                    case "name":
                        Ident(); Expect('{'); page.Name = QuotedString(); Expect('}');
                        break;
                    case "elements":
                        ParseNodeList(_ => page.Elements.Add(ParseElement()));
                        break;
                    case "layers":
                        ParseNodeList(_ => page.Layers.Add(ParseLayer()));
                        break;
                    default:
                        throw Error($"unknown Page entry '{name}'");
                }
            }
            Expect('}');
            return page;
        }

        private PageLayer ParseLayer()
        {
            if (Ident() != "Layer")
                throw Error("expected Layer");
            Expect('{');
            var layer = new PageLayer();
            while (!TryPeek('}'))
            {
                string name = Ident();
                switch (name)
                {
                    case "name":
                        Ident(); Expect('{'); layer.Name = QuotedString(); Expect('}');
                        break;
                    case "group_a":
                        ParseNodeList(_ => layer.GroupA.Add(ParseElement()));
                        break;
                    case "group_b":
                        ParseNodeList(_ => layer.GroupB.Add(ParseElement()));
                        break;
                    default:
                        throw Error($"unknown Layer entry '{name}'");
                }
            }
            Expect('}');
            return layer;
        }

        private static readonly string[] ElementNodeNames = ["Composite", "TextFace", "ImageFace", "ColorFace", "Face"];

        private PageElement ParseElement()
        {
            string node = Ident();
            if (!ElementNodeNames.Contains(node))
                throw Error($"unknown element node '{node}'");
            Expect('{');
            var e = new PageElement();
            TextBlock Text() => e.Text ??= new TextBlock();
            TextureRef Texture() => e.Texture ??= new TextureRef();
            while (!TryPeek('}'))
            {
                string name = Ident();
                if (name == "children")
                {
                    ParseNodeList(i => e.Child = i == 0 ? ParseElement() : throw Error("an element carries at most one child"));
                    continue;
                }
                Ident(); // the value-type word is decorative
                Expect('{');
                switch (name)
                {
                    case "name": e.Name = QuotedString(); break;
                    case "sub": e.Sub = (byte)Number(); break;
                    case "flags": e.Flags = (ushort)Number(); break;
                    case "geometry":
                    {
                        var v = NumbersUntilBrace();
                        if (v.Count != 4) throw Error("geometry needs x y w h");
                        (e.X, e.Y, e.W, e.H) = (v[0], v[1], v[2], v[3]);
                        break;
                    }
                    case "z": e.Z = Number(); break;
                    case "arrow": e.Arrow = Number() != 0; break;
                    case "key": e.Key = QuotedString(); break;
                    case "image":
                        SkipSpace();
                        if (_p < _s.Length && _s[_p] == '"')
                        {
                            string file = QuotedString();
                            Texture().FileIndex = fileIndex?.Invoke(file)
                                ?? throw Error($"cannot resolve image '{file}' to a file entry");
                        }
                        else
                        {
                            Texture().FileIndex = (int)Number() - 1;
                        }
                        break;
                    case "uv":
                    {
                        var v = NumbersUntilBrace();
                        if (v.Count != 8) throw Error("uv needs 8 values");
                        v.CopyTo(Texture().Uv);
                        break;
                    }
                    case "color0" or "color1" or "color2" or "color3":
                    {
                        var v = NumbersUntilBrace();
                        if (v.Count != 4) throw Error($"{name} needs r g b a");
                        e.Colors ??= new uint[4];
                        e.Colors[name[^1] - '0'] =
                            (uint)v[0] | ((uint)v[1] << 8) | ((uint)v[2] << 16) | ((uint)v[3] << 24);
                        break;
                    }
                    case "text_code1": Text().Code1 = (ushort)Number(); break;
                    case "text_code2": Text().Code2 = (ushort)Number(); break;
                    case "text_color":
                    {
                        var v = NumbersUntilBrace();
                        if (v.Count != 4) throw Error("text_color needs r g b a");
                        Text().Rgba = (uint)v[0] | ((uint)v[1] << 8) | ((uint)v[2] << 16) | ((uint)v[3] << 24);
                        break;
                    }
                    case "text": Text().Text = QuotedString(); break;
                    case "font": Text().Font = QuotedString(); break;
                    default:
                        throw Error($"unknown element property '{name}'");
                }
                Expect('}');
            }
            Expect('}');
            return e;
        }
    }
}
