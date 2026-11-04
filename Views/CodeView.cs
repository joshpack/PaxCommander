using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace PaxCommander.Views;

/// <summary>
/// Source code in the preview: coloured by a small per-language tokenizer, with line numbers, tabs kept as indents
/// and minified JSON/XML laid out properly. Not a parser, just enough to read code at a glance.
/// </summary>
public static class CodeView
{
    private enum Lang { CLike, Python, Shell, PowerShell, Json, Xml, Css, Sql, Yaml, Ini, Markdown, Lua, Ruby }

    private static readonly Dictionary<string, Lang> ByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Lang.CLike, [".js"] = Lang.CLike, [".mjs"] = Lang.CLike, [".cjs"] = Lang.CLike, [".ts"] = Lang.CLike,
        [".tsx"] = Lang.CLike, [".jsx"] = Lang.CLike, [".java"] = Lang.CLike, [".kt"] = Lang.CLike, [".kts"] = Lang.CLike,
        [".c"] = Lang.CLike, [".cc"] = Lang.CLike, [".cpp"] = Lang.CLike, [".cxx"] = Lang.CLike, [".h"] = Lang.CLike,
        [".hpp"] = Lang.CLike, [".go"] = Lang.CLike, [".rs"] = Lang.CLike, [".php"] = Lang.CLike, [".swift"] = Lang.CLike,
        [".dart"] = Lang.CLike, [".gradle"] = Lang.CLike, [".fs"] = Lang.CLike, [".vb"] = Lang.CLike, [".zig"] = Lang.CLike,
        [".proto"] = Lang.CLike, [".graphql"] = Lang.CLike, [".scss"] = Lang.Css, [".sass"] = Lang.Css, [".less"] = Lang.Css,
        [".css"] = Lang.Css, [".py"] = Lang.Python, [".sh"] = Lang.Shell, [".bash"] = Lang.Shell, [".zsh"] = Lang.Shell,
        [".make"] = Lang.Shell, [".dockerfile"] = Lang.Shell, [".env"] = Lang.Shell, [".ps1"] = Lang.PowerShell,
        [".psm1"] = Lang.PowerShell, [".psd1"] = Lang.PowerShell, [".bat"] = Lang.PowerShell, [".cmd"] = Lang.PowerShell,
        [".json"] = Lang.Json, [".jsonc"] = Lang.Json, [".xml"] = Lang.Xml, [".xaml"] = Lang.Xml, [".axaml"] = Lang.Xml,
        [".csproj"] = Lang.Xml, [".props"] = Lang.Xml, [".targets"] = Lang.Xml, [".html"] = Lang.Xml, [".htm"] = Lang.Xml,
        [".svg"] = Lang.Xml, [".vue"] = Lang.Xml, [".svelte"] = Lang.Xml, [".sql"] = Lang.Sql, [".yaml"] = Lang.Yaml,
        [".yml"] = Lang.Yaml, [".toml"] = Lang.Ini, [".ini"] = Lang.Ini, [".cfg"] = Lang.Ini, [".conf"] = Lang.Ini,
        [".editorconfig"] = Lang.Ini, [".md"] = Lang.Markdown, [".markdown"] = Lang.Markdown, [".lua"] = Lang.Lua,
        [".rb"] = Lang.Ruby, [".r"] = Lang.Python, [".pl"] = Lang.Shell, [".ex"] = Lang.Ruby, [".exs"] = Lang.Ruby,
        [".nim"] = Lang.Python, [".cmake"] = Lang.Shell, [".sln"] = Lang.Ini,
        // Dot files and ignore lists: "#" comments and key=value lines read best with the shell colours.
        [".gitignore"] = Lang.Shell, [".gitattributes"] = Lang.Shell, [".dockerignore"] = Lang.Shell, [".npmrc"] = Lang.Ini,
        [".gitmodules"] = Lang.Ini, [".gitconfig"] = Lang.Ini, [".prettierrc"] = Lang.Json, [".eslintrc"] = Lang.Json, [".babelrc"] = Lang.Json,
        [".makefile"] = Lang.Shell, [".log"] = Lang.Ini,
    };

    public static bool Supports(string ext) => ByExt.ContainsKey(ext);

    // Dark-theme palette, close to what most editors use.
    private static readonly SolidColorBrush Plain = Solid("#C9D1DE");
    private static readonly SolidColorBrush Keyword = Solid("#C586C0");
    private static readonly SolidColorBrush Storage = Solid("#569CD6");
    private static readonly SolidColorBrush Type = Solid("#4EC9B0");
    private static readonly SolidColorBrush Function = Solid("#DCDCAA");
    private static readonly SolidColorBrush Str = Solid("#CE9178");
    private static readonly SolidColorBrush Number = Solid("#B5CEA8");
    private static readonly SolidColorBrush Comment = Solid("#6A9955");
    private static readonly SolidColorBrush Meta = Solid("#9B9EA8");
    private static readonly SolidColorBrush Name = Solid("#9CDCFE");
    private static readonly SolidColorBrush Heading = Solid("#569CD6");
    private static readonly SolidColorBrush Gutter = Solid("#4A5263");

    private static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));

    /// <summary>Editor colours for the theme: VS Code's dark set, or its light set.</summary>
    public static void ApplyTheme(bool light)
    {
        string[] dark = ["#C9D1DE", "#C586C0", "#569CD6", "#4EC9B0", "#DCDCAA", "#CE9178", "#B5CEA8", "#6A9955", "#9B9EA8", "#9CDCFE", "#569CD6", "#4A5263"];
        string[] bright = ["#1F2430", "#AF00DB", "#0000FF", "#267F99", "#795E26", "#A31515", "#098658", "#008000", "#6A737D", "#001080", "#0000FF", "#A0A8B6"];
        var set = light ? bright : dark;
        SolidColorBrush[] brushes = [Plain, Keyword, Storage, Type, Function, Str, Number, Comment, Meta, Name, Heading, Gutter];
        for (var i = 0; i < brushes.Length; i++) brushes[i].Color = Color.Parse(set[i]);
    }

    private const string CKeywords =
        "abstract as async await base break case catch checked class const continue default delegate do else enum event explicit " +
        "extern finally fixed for foreach goto if implicit in interface internal is lock namespace new operator out override params " +
        "private protected public readonly record ref return sealed sizeof stackalloc static struct switch this throw try typeof " +
        "unchecked unsafe using virtual volatile while yield get set init value when where with required file global partial " +
        "function let var export import from extends implements package throws final native synchronized transient instanceof " +
        "func go defer chan map range select type fallthrough fn impl mod pub use mut match loop crate trait dyn move unsafe " +
        "def elif lambda pass raise del nonlocal not and or except echo require include auto register signed unsigned union typedef " +
        "inline constexpr template typename friend mutable nullptr co_await co_return co_yield static_cast dynamic_cast " +
        "reinterpret_cast guard inout fileprivate open extension protocol deinit subscript late factory mixin show hide " +
        "suspend object companion fun val sealed data inner reified crossinline noinline tailrec vararg keyof declare readonly";
    private const string CStorage =
        "true false null undefined void int long short byte sbyte uint ulong ushort char float double decimal bool string object " +
        "dynamic nint nuint var let const self Self super nil None True False i8 i16 i32 i64 u8 u16 u32 u64 f32 f64 usize isize str";
    private const string PyKeywords =
        "and as assert async await break class continue def del elif else except finally for from global if import in is lambda " +
        "nonlocal not or pass raise return try while with yield match case print self cls function end local then repeat until " +
        "begin rescue ensure module do unless elsif require attr_accessor defmodule defp when fn";
    private const string PyStorage = "True False None nil true false";
    private const string ShKeywords =
        "if then else elif fi for in do done while until case esac function return local export readonly declare source alias " +
        "echo exit set unset shift break continue cd test trap eval exec FROM RUN CMD COPY ADD ENV WORKDIR EXPOSE ENTRYPOINT ARG";
    private const string PsKeywords =
        "begin process end function filter param if else elseif switch foreach for while do until break continue return try catch " +
        "finally throw trap exit in class enum using module hidden static echo set goto call rem setlocal endlocal not exist defined";
    private const string SqlKeywords =
        "select from where and or not insert into values update set delete create table drop alter add column index primary key " +
        "foreign references join inner left right outer full on as group by order having limit offset union all distinct case " +
        "when then else end null is in like between exists view procedure function trigger begin commit rollback transaction " +
        "declare int integer varchar nvarchar text bit datetime date default unique check with top asc desc count sum avg min max";

    private static string Words(string list) => @"\b(?:" + string.Join('|', list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Select(Regex.Escape)) + @")\b";

    private static readonly Dictionary<Lang, Regex> Grammars = new()
    {
        [Lang.CLike] = new(
            @"(?<c>//[^\n]*|/\*[\s\S]*?\*/)" +
            @"|(?<s>@""(?:[^""]|"""")*""|\$?""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)" +
            @"|(?<m>^[ \t]*#[a-z]+[^\n]*|\[[A-Z][A-Za-z0-9_.]*(?=[\](])|@[A-Za-z_]\w*)" +
            @"|(?<n>\b(?:0[xX][0-9a-fA-F_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*)\b)" +
            @"|(?<st>" + Words(CStorage) + ")" +
            @"|(?<k>" + Words(CKeywords) + ")" +
            @"|(?<f>\b[A-Za-z_]\w*(?=\s*(?:<[^<>()]*>)?\())" +
            @"|(?<t>\b[A-Z][A-Za-z0-9_]*\b)",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Python] = new(
            @"(?<c>#[^\n]*|--[^\n]*)" +
            @"|(?<s>[rbfuRBFU]{0,2}(?:""""""[\s\S]*?""""""|'''[\s\S]*?'''|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'))" +
            @"|(?<m>^[ \t]*@[\w.]+|:[a-z_]\w*)" +
            @"|(?<n>\b\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?j?\b)" +
            @"|(?<st>" + Words(PyStorage) + ")" +
            @"|(?<k>" + Words(PyKeywords) + ")" +
            @"|(?<f>\b[A-Za-z_]\w*(?=\s*\())" +
            @"|(?<t>\b[A-Z][A-Za-z0-9_]*\b)",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Shell] = new(
            @"(?<c>(?<![\w$])#[^\n]*)" +
            @"|(?<s>""(?:\\.|[^""\\])*""|'[^']*')" +
            @"|(?<nm>\$\{?[A-Za-z_][\w]*\}?|\$[0-9@#?*!$-])" +
            @"|(?<n>\b\d+\b)" +
            @"|(?<k>" + Words(ShKeywords) + ")" +
            @"|(?<m>(?<=\s)--?[A-Za-z][\w-]*)",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.PowerShell] = new(
            @"(?<c><#[\s\S]*?#>|#[^\n]*|(?i:^\s*(?:rem|::)\b[^\n]*))" +
            @"|(?<s>""(?:`.|[^""`])*""|'(?:''|[^'])*')" +
            @"|(?<nm>\$[A-Za-z_][\w:]*|%[A-Za-z_]\w*%|\$\{[^}]+\})" +
            @"|(?<n>\b\d+(?:\.\d+)?\b)" +
            @"|(?<k>(?i:" + Words(PsKeywords) + "))" +
            @"|(?<f>\b[A-Z][a-z]+-[A-Z][A-Za-z]+\b)" +
            @"|(?<m>(?<=\s)-[A-Za-z]+\b|\[[A-Za-z.]+\])",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Json] = new(
            @"(?<c>//[^\n]*|/\*[\s\S]*?\*/)" +
            @"|(?<nm>""(?:\\.|[^""\\])*""(?=\s*:))" +
            @"|(?<s>""(?:\\.|[^""\\])*"")" +
            @"|(?<n>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)" +
            @"|(?<st>\b(?:true|false|null)\b)",
            RegexOptions.Compiled),
        [Lang.Xml] = new(
            @"(?<c><!--[\s\S]*?-->)" +
            @"|(?<m><\?[\s\S]*?\?>|<!\w[^>]*>|<!\[CDATA\[[\s\S]*?\]\]>)" +
            @"|(?<st></?[A-Za-z_][\w:.-]*|/?>)" +
            @"|(?<nm>\b[A-Za-z_:][\w:.-]*(?==))" +
            @"|(?<s>""[^""]*""|'[^']*')" +
            @"|(?<n>&\w+;|&#\d+;)",
            RegexOptions.Compiled),
        [Lang.Css] = new(
            @"(?<c>/\*[\s\S]*?\*/|//[^\n]*)" +
            @"|(?<s>""[^""\n]*""|'[^'\n]*')" +
            @"|(?<m>@[\w-]+|![a-z]+)" +
            @"|(?<n>#[0-9a-fA-F]{3,8}\b|-?\b\d+(?:\.\d+)?(?:px|em|rem|%|vh|vw|s|ms|deg|fr)?\b)" +
            @"|(?<nm>[\w-]+(?=\s*:[^{;]*[;}\n]))" +
            @"|(?<nmv>\$[\w-]+|--[\w-]+)" +
            @"|(?<t>[.#][\w-]+|::?[\w-]+)",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Sql] = new(
            @"(?<c>--[^\n]*|/\*[\s\S]*?\*/)" +
            @"|(?<s>'(?:''|[^'])*')" +
            @"|(?<n>\b\d+(?:\.\d+)?\b)" +
            @"|(?<k>(?i:" + Words(SqlKeywords) + "))" +
            @"|(?<nm>@\w+|\[[^\]\n]+\])",
            RegexOptions.Compiled),
        [Lang.Yaml] = new(
            @"(?<c>#[^\n]*)" +
            @"|(?<nm>^[ \t]*-?[ \t]*[\w.""'/-]+(?=\s*:(?:\s|$)))" +
            @"|(?<s>""(?:\\.|[^""\\])*""|'[^']*')" +
            @"|(?<n>\b\d+(?:\.\d+)?\b)" +
            @"|(?<st>\b(?:true|false|null|yes|no|on|off)\b|~)" +
            @"|(?<m>^---|^\.\.\.|[&*][\w-]+|!\w+)",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Ini] = new(
            @"(?<c>^[ \t]*[#;][^\n]*)" +
            @"|(?<t>^[ \t]*\[[^\]\n]+\])" +
            @"|(?<nm>^[ \t]*[\w.\-""]+(?=\s*=))" +
            @"|(?<s>""(?:\\.|[^""\\])*""|'[^']*')" +
            @"|(?<n>\b\d+(?:\.\d+)?\b)" +
            @"|(?<st>\b(?:true|false)\b)",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Markdown] = new(
            @"(?<h>^#{1,6}[^\n]*)" +
            @"|(?<c>^>[^\n]*)" +
            @"|(?<s>```[\s\S]*?```|`[^`\n]+`)" +
            @"|(?<k>\*\*[^*\n]+\*\*|__[^_\n]+__)" +
            @"|(?<nm>\[[^\]\n]+\]\([^)\n]+\))" +
            @"|(?<m>^[ \t]*(?:[-*+]|\d+\.)(?=\s))",
            RegexOptions.Multiline | RegexOptions.Compiled),
        [Lang.Lua] = new(
            @"(?<c>--\[\[[\s\S]*?\]\]|--[^\n]*)" +
            @"|(?<s>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|\[\[[\s\S]*?\]\])" +
            @"|(?<n>\b\d+(?:\.\d+)?\b)" +
            @"|(?<st>\b(?:true|false|nil)\b)" +
            @"|(?<k>" + Words(PyKeywords) + ")" +
            @"|(?<f>\b[A-Za-z_]\w*(?=\s*\())",
            RegexOptions.Multiline | RegexOptions.Compiled),
    };

    static CodeView() => Grammars[Lang.Ruby] = Grammars[Lang.Python];

    private static IBrush BrushFor(Match m) =>
        m.Groups["c"].Success ? Comment :
        m.Groups["s"].Success ? Str :
        m.Groups["m"].Success ? Meta :
        m.Groups["n"].Success ? Number :
        m.Groups["st"].Success ? Storage :
        m.Groups["k"].Success ? Keyword :
        m.Groups["f"].Success ? Function :
        m.Groups["t"].Success ? Type :
        m.Groups["h"].Success ? Heading :
        m.Groups["nm"].Success || m.Groups["nmv"].Success ? Name :
        Plain;

    /// <summary>Lays out minified JSON/XML; anything already on several lines is left exactly as written.</summary>
    private static string Tidy(string text, Lang lang)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 4000));
        if (text.Length < 200 || head.Count('\n') > 2) return text;
        try
        {
            if (lang == Lang.Json)
                return JsonSerializer.Serialize(JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (lang == Lang.Xml && text.TrimStart().StartsWith('<'))
                return XDocument.Parse(text).ToString();
        }
        catch
        {
            // Not valid (or cut off by the preview limit): show it as it is.
        }
        return text;
    }

    /// <summary>A coloured, line-numbered view of <paramref name="text"/>, or null for a file type we don't colour.</summary>
    public static Control? Build(string text, string ext, bool large)
    {
        if (!ByExt.TryGetValue(ext, out var lang)) return null;
        text = Tidy(text.Replace("\r\n", "\n"), lang);
        text = ExpandTabs(text);
        var grammar = Grammars[lang];
        var fontSize = large ? 13 : 11.5;

        var code = new SelectableTextBlock { FontFamily = Skin.Mono, FontSize = fontSize, Foreground = Plain, TextWrapping = TextWrapping.NoWrap };
        var inlines = code.Inlines ??= new InlineCollection();
        var at = 0;
        foreach (Match m in grammar.Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text[at..m.Index]));
            inlines.Add(new Run(m.Value) { Foreground = BrushFor(m) });
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));

        var lines = text.Count(c => c == '\n') + 1;
        var numbers = new StringBuilder();
        for (var i = 1; i <= lines; i++) numbers.Append(i).Append('\n');
        var gutter = new TextBlock
        {
            Text = numbers.ToString(0, numbers.Length - 1),
            FontFamily = Skin.Mono,
            FontSize = fontSize,
            Foreground = Gutter,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 14, 0),
            MinWidth = 18,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 12) };
        grid.Children.Add(gutter);
        Grid.SetColumn(code, 1);
        grid.Children.Add(code);

        return new Border
        {
            Background = Skin.CodeBg,
            BorderBrush = Skin.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 10, 12, 10),
            MaxHeight = large ? double.PositiveInfinity : 560,
            Child = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };
    }

    /// <summary>Tabs as 4-column stops, so indentation lines up the way the author's editor showed it.</summary>
    private static string ExpandTabs(string text)
    {
        if (!text.Contains('\t')) return text;
        var sb = new StringBuilder(text.Length + 256);
        var col = 0;
        foreach (var ch in text)
        {
            if (ch == '\t')
            {
                var n = 4 - col % 4;
                sb.Append(' ', n);
                col += n;
            }
            else
            {
                sb.Append(ch);
                col = ch == '\n' ? 0 : col + 1;
            }
        }
        return sb.ToString();
    }
}
