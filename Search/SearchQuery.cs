using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace pxdCommander.Search;

/// <summary>
/// A parsed query. Plain words must all appear in the name (case-insensitive, any order); "quoted text" is one term;
/// a term with * or ? is a whole-name wildcard; -term excludes (also -ext:, -type:, -size: … exclude what the filter
/// matches). Filters: ext:jpg,png  type:image|video|audio|doc|archive|code|folder|file (comma list = any of)
/// size:&gt;10mb size:&lt;1kb size:1mb..5mb size:10mb (±10 %) size:empty  modified:today|yesterday|7d|&lt;30d|&gt;1y|
/// 2026-01-01..2026-02-01  created:(same)  content:"text" (inside text-like files)  re:regex  hidden:yes|no|only.
/// Unknown or malformed filters are searched for as plain words. Relative dates are fixed when the query is parsed.
/// </summary>
public sealed class SearchQuery
{
    private const FileAttributes HiddenOrSystem = FileAttributes.Hidden | FileAttributes.System;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private enum HiddenMode { Default, Include, Exclude, Only }

    private readonly record struct RegexTerm(Regex Regex, bool Negate);
    private readonly record struct ExtTerm(string[] DottedExtensions, bool Negate);
    private readonly record struct TypeTerm(QueryFileTypes.Group Groups, bool Negate);
    private readonly record struct RangeTerm(long Min, long Max, bool Negate); // size: inclusive; times: [Min, Max)

    private readonly string[] _contains, _excludes, _globs, _excludeGlobs;
    private readonly RegexTerm[] _regexes;
    private readonly ExtTerm[] _exts;
    private readonly TypeTerm[] _types;
    private readonly RangeTerm[] _sizes, _modified, _created;
    private readonly ContentTerm[] _content;
    private readonly HiddenMode _hidden;
    private readonly string _description;

    private SearchQuery(Builder b, string text)
    {
        Text = text;
        _contains = [.. b.Contains];
        _excludes = [.. b.Excludes];
        _globs = [.. b.Globs];
        _excludeGlobs = [.. b.ExcludeGlobs];
        _regexes = [.. b.Regexes];
        _exts = [.. b.Exts];
        _types = [.. b.Types];
        _sizes = [.. b.Sizes];
        _modified = [.. b.Modified];
        _created = [.. b.Created];
        _content = [.. b.Content];
        _hidden = b.Hidden;
        _description = BuildDescription(b);
    }

    /// <summary>Parses query text. Never throws: anything it cannot read as a filter becomes a plain word.</summary>
    public static SearchQuery Parse(string text)
    {
        text ??= "";
        try
        {
            var b = new Builder(DateTime.UtcNow);
            Tokenize(text, b);
            return new SearchQuery(b, text);
        }
        catch (Exception)
        {
            // Defensive only: the parser is written not to throw. Fall back to whitespace-separated plain words.
            var b = new Builder(DateTime.UtcNow);
            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) b.Contains.Add(word);
            return new SearchQuery(b, text);
        }
    }

    /// <summary>The text this query was parsed from.</summary>
    public string Text { get; }

    /// <summary>True when the query has nothing to match on.</summary>
    public bool IsEmpty =>
        _contains.Length == 0 && _excludes.Length == 0 && _globs.Length == 0 && _excludeGlobs.Length == 0 &&
        _regexes.Length == 0 && _exts.Length == 0 && _types.Length == 0 && _sizes.Length == 0 &&
        _modified.Length == 0 && _created.Length == 0 && _content.Length == 0 && _hidden != HiddenMode.Only;

    /// <summary>True when the query needs to open files (content:), so it is slow.</summary>
    public bool NeedsContent => _content.Length > 0;

    /// <summary>
    /// The hidden: token: true for hidden:yes/only, false for hidden:no, null when absent (then SearchOptions.IncludeHidden
    /// decides). The search engine uses this to decide whether hidden/system items are walked at all.
    /// </summary>
    public bool? IncludeHidden => _hidden switch
    {
        HiddenMode.Include or HiddenMode.Only => true,
        HiddenMode.Exclude => false,
        _ => null,
    };

    /// <summary>True for hidden:only (only hidden/system items match).</summary>
    public bool HiddenOnly => _hidden == HiddenMode.Only;

    /// <summary>Short human summary, e.g. "name contains “report”, .pdf, modified in the last 7 days".</summary>
    public string Describe() => _description;

    public override string ToString() => _description;

    internal ContentTerm[] ContentTerms => _content;

    /// <summary>
    /// Name/attribute/size/date test (everything except content:). Hidden items are not rejected here unless the query
    /// says hidden:no or hidden:only; skipping hidden items by default is the walker's job.
    /// </summary>
    public bool Matches(string name, bool isDirectory, long size, DateTime modifiedUtc, DateTime createdUtc, FileAttributes attributes)
    {
        if (name is null) return false;
        return MatchesName(name, isDirectory)
            && MatchesAttributes(attributes)
            && MatchesSize(size, isDirectory)
            && MatchesTime(_modified, UtcTicks(modifiedUtc))
            && MatchesTime(_created, UtcTicks(createdUtc));
    }

    /// <summary>
    /// The content: test for one item, as the search engine applies it: streams the file looking for the needles.
    /// Folders, binary-looking files, files over 50 MB and cloud-only placeholders contain nothing, so they pass only
    /// -content: terms. True when the query has no content: terms; false when cancelled mid-file.
    /// </summary>
    public bool MatchesContent(string path, CancellationToken ct = default)
    {
        if (_content.Length == 0) return true;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Directory.Exists(path) && Array.TrueForAll(_content, t => t.Negate);
            return new ContentScanner(_content).Matches(path, Path.GetFileName(path.AsSpan()), info.Length, info.Attributes, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The allocation-free test the walker runs on every directory entry. Fields are read only when needed.</summary>
    internal bool MatchesEntry(ref FileSystemEntry entry)
    {
        bool isDirectory = entry.IsDirectory;
        if (!MatchesName(entry.FileName, isDirectory)) return false;
        if (_hidden is HiddenMode.Only or HiddenMode.Exclude && !MatchesAttributes(entry.Attributes)) return false;
        if (_sizes.Length != 0 && !MatchesSize(isDirectory ? 0 : entry.Length, isDirectory)) return false;
        if (_modified.Length != 0 && !MatchesTime(_modified, entry.LastWriteTimeUtc.UtcTicks)) return false;
        if (_created.Length != 0 && !MatchesTime(_created, entry.CreationTimeUtc.UtcTicks)) return false;
        return true;
    }

    private bool MatchesName(ReadOnlySpan<char> name, bool isDirectory)
    {
        foreach (var t in _contains)
            if (!name.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var t in _excludes)
            if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var g in _globs)
            if (!FileSystemName.MatchesSimpleExpression(g, name, ignoreCase: true)) return false;
        foreach (var g in _excludeGlobs)
            if (FileSystemName.MatchesSimpleExpression(g, name, ignoreCase: true)) return false;
        foreach (var r in _regexes)
            if (IsMatch(r.Regex, name) == r.Negate) return false;

        // ext: and type: describe file formats, so a folder never matches them (a folder named "x.jpg" is no image).
        foreach (var e in _exts)
        {
            bool hit = false;
            if (!isDirectory)
                foreach (var dotted in e.DottedExtensions)
                    if (name.EndsWith(dotted, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
            if (hit == e.Negate) return false;
        }
        if (_types.Length != 0)
        {
            var ext = isDirectory ? default : QueryFileTypes.ExtensionOf(name);
            foreach (var t in _types)
            {
                bool hit = isDirectory
                    ? (t.Groups & QueryFileTypes.Group.Folder) != 0
                    : (t.Groups & QueryFileTypes.Group.File) != 0 || (QueryFileTypes.GroupOf(ext) & t.Groups) != 0;
                if (hit == t.Negate) return false;
            }
        }
        return true;
    }

    private static bool IsMatch(Regex regex, ReadOnlySpan<char> name)
    {
        try { return regex.IsMatch(name); }
        catch (RegexMatchTimeoutException) { return false; } // a pathological pattern must not stall a worker
    }

    private bool MatchesAttributes(FileAttributes attributes) => _hidden switch
    {
        HiddenMode.Only => (attributes & HiddenOrSystem) != 0,
        HiddenMode.Exclude => (attributes & HiddenOrSystem) == 0,
        _ => true,
    };

    private bool MatchesSize(long size, bool isDirectory)
    {
        foreach (var s in _sizes)
        {
            bool hit = !isDirectory && size >= s.Min && size <= s.Max; // folders have no size of their own
            if (hit == s.Negate) return false;
        }
        return true;
    }

    private static bool MatchesTime(RangeTerm[] terms, long utcTicks)
    {
        foreach (var t in terms)
        {
            bool hit = utcTicks >= t.Min && utcTicks < t.Max;
            if (hit == t.Negate) return false;
        }
        return true;
    }

    private static long UtcTicks(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime().Ticks : d.Ticks;

    // ---------------------------------------------------------------------------------------------------------------
    // Parsing

    private sealed class Builder(DateTime nowUtc)
    {
        public readonly DateTime NowUtc = nowUtc;
        public readonly List<string> Contains = [], Excludes = [], Globs = [], ExcludeGlobs = [];
        public readonly List<RegexTerm> Regexes = [];
        public readonly List<ExtTerm> Exts = [];
        public readonly List<TypeTerm> Types = [];
        public readonly List<RangeTerm> Sizes = [], Modified = [], Created = [];
        public readonly List<ContentTerm> Content = [];
        public HiddenMode Hidden;
        // Filter descriptions in the order typed, after the name terms.
        public readonly List<string> FilterText = [];
    }

    /// <summary>
    /// Splits on whitespace outside quotes. A quote toggles quoting anywhere in a token (so content:"a b" and
    /// -"a b" work); an unbalanced quote runs to the end. A token is a filter only when its key is plain letters
    /// before the first unquoted colon.
    /// </summary>
    private static void Tokenize(string s, Builder b)
    {
        var sb = new StringBuilder();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i >= n) break;

            bool negate = false;
            if (s[i] == '-' && i + 1 < n && !char.IsWhiteSpace(s[i + 1]))
            {
                negate = true;
                i++;
            }

            sb.Clear();
            bool inQuotes = false, sawQuote = false;
            int colon = -1;
            while (i < n)
            {
                char c = s[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    sawQuote = true;
                    i++;
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(c)) break;
                if (c == ':' && !inQuotes && !sawQuote && colon < 0 && sb.Length > 0) colon = sb.Length;
                sb.Append(c);
                i++;
            }
            if (sb.Length == 0) continue; // "" or a lone quote
            AddToken(b, sb.ToString(), negate, colon);
        }
    }

    private static void AddToken(Builder b, string token, bool negate, int colon)
    {
        if (colon > 0 && IsKey(token.AsSpan(0, colon)) &&
            TryAddFilter(b, token[..colon].ToLowerInvariant(), token[(colon + 1)..], negate))
            return;
        AddWord(b, token, negate);
    }

    private static bool IsKey(ReadOnlySpan<char> key)
    {
        foreach (char c in key)
            if (!char.IsAsciiLetter(c)) return false;
        return true;
    }

    private static void AddWord(Builder b, string word, bool negate)
    {
        bool glob = word.AsSpan().IndexOfAny('*', '?') >= 0;
        (glob ? (negate ? b.ExcludeGlobs : b.Globs) : (negate ? b.Excludes : b.Contains)).Add(word);
    }

    private static bool TryAddFilter(Builder b, string key, string value, bool negate)
    {
        string not = negate ? "not " : "";
        switch (key)
        {
            case "ext":
            {
                var list = new List<string>();
                foreach (var raw in value.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = raw.TrimStart('*').TrimStart('.');
                    if (e.Length == 0) continue;
                    if (e.AsSpan().IndexOfAny("*?\\/:<>\"| ") >= 0) return false;
                    if (!list.Contains("." + e, StringComparer.OrdinalIgnoreCase)) list.Add("." + e);
                }
                if (list.Count == 0) return false;
                b.Exts.Add(new ExtTerm([.. list], negate));
                b.FilterText.Add(not + string.Join("/", list.Select(x => x.ToLowerInvariant())));
                return true;
            }
            case "type" or "kind":
            {
                var groups = QueryFileTypes.Group.None;
                foreach (var raw in value.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!QueryFileTypes.TryParseGroup(raw, out var g)) return false;
                    groups |= g;
                }
                if (groups == QueryFileTypes.Group.None) return false;
                b.Types.Add(new TypeTerm(groups, negate));
                b.FilterText.Add(not + QueryFileTypes.Describe(groups, negate));
                return true;
            }
            case "size":
            {
                if (!QueryFilterParsers.TryParseSize(value, out long min, out long max, out var text)) return false;
                b.Sizes.Add(new RangeTerm(min, max, negate));
                b.FilterText.Add(not + text);
                return true;
            }
            case "modified" or "date" or "dm" or "datemodified" or "mtime":
            case "created" or "dc" or "datecreated" or "ctime":
            {
                if (!QueryFilterParsers.TryParseTime(value, b.NowUtc, out long min, out long max, out var text)) return false;
                bool modified = key is "modified" or "date" or "dm" or "datemodified" or "mtime";
                (modified ? b.Modified : b.Created).Add(new RangeTerm(min, max, negate));
                b.FilterText.Add(not + (modified ? "modified " : "created ") + text);
                return true;
            }
            case "content":
            {
                if (value.Length == 0) return false;
                b.Content.Add(new ContentTerm(value, negate));
                b.FilterText.Add((negate ? "not containing " : "containing ") + Quote(value));
                return true;
            }
            case "re" or "regex":
            {
                if (value.Length == 0) return false;
                Regex regex;
                try
                {
                    regex = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (ArgumentException)
                {
                    return false; // invalid pattern: becomes a plain word
                }
                b.Regexes.Add(new RegexTerm(regex, negate));
                b.FilterText.Add((negate ? "name doesn't match regex " : "name matches regex ") + Quote(value));
                return true;
            }
            case "hidden":
            {
                var mode = value.ToLowerInvariant() switch
                {
                    "yes" or "y" or "true" or "1" or "on" or "show" or "include" => HiddenMode.Include,
                    "no" or "n" or "false" or "0" or "off" or "hide" or "exclude" => HiddenMode.Exclude,
                    "only" => HiddenMode.Only,
                    _ => HiddenMode.Default,
                };
                if (mode == HiddenMode.Default) return false;
                if (negate) mode = mode == HiddenMode.Include ? HiddenMode.Exclude : HiddenMode.Include;
                b.Hidden = mode; // the last hidden: token wins
                return true;
            }
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Description

    private static string BuildDescription(Builder b)
    {
        var parts = new List<string>();
        if (b.Contains.Count > 0) parts.Add("name contains " + QuoteList(b.Contains, "and"));
        if (b.Globs.Count > 0) parts.Add("name matches " + QuoteList(b.Globs, "and"));
        if (b.Excludes.Count > 0) parts.Add("name doesn't contain " + QuoteList(b.Excludes, "or"));
        if (b.ExcludeGlobs.Count > 0) parts.Add("name doesn't match " + QuoteList(b.ExcludeGlobs, "or"));
        parts.AddRange(b.FilterText);
        switch (b.Hidden)
        {
            case HiddenMode.Include: parts.Add("including hidden"); break;
            case HiddenMode.Exclude: parts.Add("excluding hidden"); break;
            case HiddenMode.Only: parts.Add("hidden items only"); break;
        }
        if (parts.Count == 0) return "everything";
        if (b.Contains.Count == 0 && b.Globs.Count == 0 && b.Excludes.Count == 0 && b.ExcludeGlobs.Count == 0 &&
            b.FilterText.Count == 0)
            parts.Insert(0, "everything"); // e.g. "everything, including hidden"
        return string.Join(", ", parts);
    }

    private static string Quote(string s) => "\u201C" + s + "\u201D";

    private static string QuoteList(List<string> items, string conjunction) => items.Count switch
    {
        1 => Quote(items[0]),
        2 => $"{Quote(items[0])} {conjunction} {Quote(items[1])}",
        _ => string.Join(", ", items.Take(items.Count - 1).Select(Quote)) + $" {conjunction} {Quote(items[^1])}",
    };
}

/// <summary>One content: needle; Negate means the file must not contain it.</summary>
internal readonly record struct ContentTerm(string Needle, bool Negate);
