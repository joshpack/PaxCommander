using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaxCommander.Search;

/// <summary>
/// Explorer's name order (the semantics of Windows' StrCmpLogicalW), in portable managed code: case-insensitive, digit runs
/// compared as numbers ("file2" &lt; "file10"; equal numbers with more leading zeros first: "a01" &lt; "a1").
/// </summary>
/// <remarks>
/// StrCmpLogicalW is a multi-level Windows (NLS) comparison and this mirrors those levels:
/// 1) primary: symbols &lt; numbers &lt; letters &lt; other scripts, with hyphens and apostrophes ignored ("co-op" sorts
///    with "coop") and accents ignored ("é" with "e"); ß, æ, œ, þ count as ss, ae, oe, th;
/// 2) accents, left to right; 3) width variants (fullwidth "Ａ" after "A"); case is never significant ("a" == "A");
/// 4) the ignored hyphens/apostrophes: none &lt; some, a later one &lt; an earlier one.
/// Windows quirks kept on purpose: a number is at most 19 characters (longer digit runs split there), digits of
/// different scripts never join one number, and superscripts weigh like a single digit ("x²" &gt; "x10").
/// Character order comes from tables measured against StrCmpLogicalW (ASCII, Latin-1, Latin Extended, general
/// punctuation, currency, script order). In 240,000 random comparisons it disagreed once (Ɛ vs ȝ).
/// Compare is allocation-free and thread-safe; it sorts about 2.5× faster than calling StrCmpLogicalW.
/// </remarks>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return CompareSpans(x, y);
    }

    /// <summary>Span version of <see cref="Compare(string?, string?)"/>.</summary>
    public static int CompareSpans(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        int c = ComparePrimary(x, y);
        if (c != 0) return c;
        c = CompareLevel(x, y, secondary: true);
        if (c != 0) return c;
        c = CompareLevel(x, y, secondary: false);
        if (c != 0) return c;
        return CompareSpecials(x, y);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Weights

    private enum Kind : byte
    {
        Normal,     // one primary weight
        Digit,      // part of a number
        Ignorable,  // no weight at any level (format characters, soft hyphen)
        Special,    // hyphen/apostrophe class: only counted at the last level
        Mark,       // combining mark: adds to the accent weight of the character before it
        Expansion,  // two primary weights (ß = s s)
        DigitLike,  // superscript digit or fraction: weighs like one digit of its value (Primary = 4 × value)
    }

    private struct CharInfo
    {
        public Kind Kind;
        public byte Tertiary;
        public ushort Secondary, Secondary2;
        public int Primary, Primary2;
    }

    private const int End = 0;
    private const int UnknownSymbolBase = 1_000;      // + code point: symbols missing from the measured table
    private const int DigitLikeBase = 70_000;         // fractions, superscripts, circled/Roman numerals: before numbers
    private const int DigitRun = 140_000;
    private const int LetterBase = 200_000;           // + 4 * (letter - 'a') + slot
    private const int OtherBase = 400_000;            // + script rank * 0x20000 + 2 * upper-cased code unit
    private const int LowSurrogateBase = 4_000_000;   // second half of a surrogate pair (emoji etc.)
    private const int AsciiDigit = -1, AsciiSkip = -2;

    private const int LowEnd = 0x0530;                // dense table: Basic Latin .. Cyrillic Supplement
    private const int MidStart = 0x1E00, MidEnd = 0x2800; // Latin Extended Additional .. arrows/shapes

    private static readonly int[] s_ascii = new int[128];
    private static readonly CharInfo[] s_low = new CharInfo[LowEnd];
    private static readonly CharInfo[] s_mid = new CharInfo[MidEnd - MidStart];
    private static readonly FrozenDictionary<char, CharInfo> s_extra;

    // Primary groups of symbols in StrCmpLogicalW order (measured). Characters within one group differ only at the
    // width/variant level, in the listed order.
    private static readonly string[] SymbolGroups =
    [
        " \u205F\u3000\u202F", "\u00A0", "\u2002\u2000", "\u2003\u2001", "\u2004", "\u2005", "\u2006", "\u2007", "\u2008",
        "\u2009", "\u200A", "\u200B", "\u2028", "\u2029", "\u2055\u2056\u2058\u2059\u205A\u205B",
        "!\uFF01\u203C\u2049", "\"\u204D\uFF02", "\u204E", "#\u2057\uFF03", "\u2054", "\u2052$\uFF04", "\u2050",
        "%\u204F\uFF05", "\u204C", "&\u204B\uFF06", "(\uFF08", ")\uFF09", "*\uFF0A\u2051", ",\uFF0C", "\u3001",
        ".\u2024\uFF0E", "\u3002", "/\uFF0F", ":", ";", "?\u2047\u2048", "@", "[\u2045", "\\", "]\u2046", "^", "_", "`",
        "{", "|", "}", "~",
        "\u00A1", "\u00A6", "\u00A8", "\u00AF", "\u00B4", "\u00B8", "\u00BF", "\u2016", "\u2017", "\u2018", "\u2019",
        "\u201A", "\u201B", "\u201C", "\u201D", "\u201E", "\u201F", "\u2032\u2033\u2036\u2034\u2037", "\u2035",
        "\u2038\u204A", "\u2039\u2053", "\u203A", "\u203D", "\u203E", "\u203F",
        "\u00A2", "\u00A3", "\u00A4", "\u00A5", "\u20B1", "\u20A0", "\u20A3", "\u20A4", "\u20A5\u20AD", "\u20A6", "\u20A7",
        "\u20A8", "\u20A9", "\u20AF", "\u20B0", "\u20AE", "\u20A2", "\u20AC", "\u20A1", "\u20B2", "\u20B3", "\u20B4",
        "\u20B5", "\u20AA", "\u20AB", "\u20B9", "\u20BA", "\u3003",
        "+\uFF0B", "\u2044", "<", "=", ">", "\u00B1", "\u00AB", "\u00BB", "\u00D7", "\u00F7", "\u25A0\u25A1", "\u2191",
        "\u2197", "\u2192", "\u2198", "\u2193", "\u2199", "\u2190", "\u2196", "\u2194", "\u2195",
        "\u00A7", "\u00A9", "\u00AC", "\u00AE", "\u00B0", "\u00B5", "\u00B6", "\u00B7", "\u2025", "\u2026", "\u2606",
        "\u2605", "\u2117", "\u2020", "\u2021", "\u2022", "\u2023", "\u2030\u2031", "\u203B", "\u2040", "\u2041", "\u2042",
        "\u205C", "\u205D", "\u205E",
    ];

    // The hyphen/apostrophe class, lightest first. "co-op" == "coop" until the very last level.
    private const string Specials = "'\uFF07-\uFF0D\u2212\u2010\u2011\u2027\u2043\u2012\u2013\u2014\u2015\uFE58\uFE63";

    // Fractions, superscript and subscript digits: not numbers, but Windows sorts each like a single digit of its value.
    private const string DigitLike = "\u00BC\u00BD\u00BE\u00B9\u00B2\u00B3\u2070\u2074\u2075\u2076\u2077\u2078\u2079" +
        "\u2080\u2081\u2082\u2083\u2084\u2085\u2086\u2087\u2088\u2089";

    // Letters without a canonical decomposition, sorted by Windows with a base letter: (characters, base, accent weight).
    private static readonly (string Chars, char Base, ushort Accent)[] LetterVariants =
    [
        ("\u023A", 'a', 67),
        ("\u0243\u0180", 'b', 67), ("\u0181", 'b', 69), ("\u0182\u0183", 'b', 71), ("\u0184\u0185", 'b', 73),
        ("\u023B\u023C", 'c', 87), ("\u0187\u0188", 'c', 89), ("\u0186", 'c', 91),
        ("\u0110\u0111", 'd', 53), ("\u018B\u018C", 'd', 55), ("\u018A", 'd', 57), ("\u00D0\u00F0", 'd', 59), ("\u018D", 'd', 61),
        ("\u0246\u0247", 'e', 87), ("\u01DD\u018E", 'e', 147), ("\u018F", 'e', 149),
        ("\u0191\u0192", 'f', 67),
        ("\u01E4\u01E5", 'g', 87), ("\u0193", 'g', 89), ("\u0194", 'g', 91),
        ("\u0126\u0127", 'h', 53),
        ("\u0131", 'i', 1), ("\u0197", 'i', 67), ("\u0196", 'i', 147),
        ("\u0237", 'j', 1), ("\u0248\u0249", 'j', 53),
        ("\u0138", 'k', 1), ("\u0198\u0199", 'k', 87),
        ("\u013F\u0140", 'l', 41), ("\u019A\u023D", 'l', 87), ("\u0141\u0142", 'l', 89), ("\u019B", 'l', 91),
        ("\u019C", 'm', 67),
        ("\u019D", 'n', 87), ("\u0149", 'n', 89), ("\u019E\u0220", 'n', 91),
        ("\u019F", 'o', 69), ("\u00D8\u00F8", 'o', 93), ("\u01FE\u01FF", 'o', 111), ("\u01A2\u01A3", 'o', 381),
        ("\u01A4\u01A5", 'p', 67),
        ("\u024A\u024B", 'q', 67),
        ("\u01A6", 'r', 1), ("\u024C\u024D", 'r', 87),
        ("\u023F", 's', 207), ("\u01A9", 's', 209), ("\u01A7\u01A8", 's', 211), ("\u01AA", 's', 213),
        ("\u01AC\u01AD", 't', 87), ("\u01AB", 't', 89), ("\u01AE", 't', 91), ("\u023E", 't', 207), ("\u0236", 't', 209),
        ("\u0244", 'u', 69), ("\u01B1", 'u', 181),
        ("\u01B2", 'v', 67), ("\u0245", 'v', 69),
        ("\u01BF\u01F7", 'w', 49),
        ("\u024E\u024F", 'y', 57), ("\u01B3\u01B4", 'y', 59),
        ("\u01B5\u01B6", 'z', 53), ("\u0224\u0225", 'z', 55), ("\u0240", 'z', 57),
    ];

    // Letters Windows gives their own primary weight right after a base letter: (characters, base, slot 1..3).
    private static readonly (string Chars, char Base, int Slot)[] LetterSlots =
    [
        ("\u0189", 'd', 1), ("\u0221", 'd', 2), ("\u0190\u021C\u021D", 'e', 1), ("\u0195\u01F6", 'h', 1),
        ("\u0234", 'l', 1), ("\u0235", 'n', 1), ("\u014A\u014B", 'n', 2), ("\u0222\u0223", 'o', 1), ("\u017F", 's', 1),
        ("\u0166\u0167", 't', 1), ("\u01B7\u0292\u01B8\u01B9\u01BA", 'z', 1), ("\u01BE\u0241\u0242", 'z', 2),
    ];

    // Letters that compare exactly like two letters (StrCmpLogicalW("Straße", "Strasse") == 0).
    private static readonly (string Chars, char First, char Second, ushort SecondAccent)[] Expansions =
    [
        ("\u00DF\u1E9E", 's', 's', 0), ("\u00C6\u00E6", 'a', 'e', 0), ("\u0152\u0153", 'o', 'e', 0),
        ("\u00DE\u00FE", 't', 'h', 0), ("\u0132\u0133", 'i', 'j', 0), ("\u01F1\u01F2\u01F3", 'd', 'z', 0),
        ("\u01C4\u01C5\u01C6", 'd', 'z', 52), ("\u01C7\u01C8\u01C9", 'l', 'j', 0), ("\u01CA\u01CB\u01CC", 'n', 'j', 0),
        ("\u0238", 'd', 'b', 0), ("\u0239", 'q', 'p', 0), ("\u2122", 't', 'm', 0),
    ];

    // Click letters sort after the symbols and before numbers; tone letters between numbers and letters.
    private static readonly string[] Clicks = ["\u01C3", "\u01C0", "\u01C1", "\u01C2"];
    private static readonly string[] ToneLetters = ["\u01BB", "\u01BD\u01BC"];

    /// <summary>Accent weights in the order Windows sorts single accents (é &lt; è &lt; ė &lt; ê &lt; ë &lt; ě …).</summary>
    /// <remarks>
    /// Windows adds the weights of stacked accents (\u015F sorts equal to \u1E65: cedilla = acute + dot above), so these are
    /// additive weights solved from StrCmpLogicalW's order of every precomposed Latin letter (Vietnamese and pinyin
    /// letters included). Even values; the letter variants above take the odd values in between.
    /// </remarks>
    private static ushort MarkWeight(char mark) => mark switch
    {
        '\u0301' => 40,  // acute
        '\u0300' => 42,  // grave
        '\u0307' => 46,  // dot above
        '\u0302' => 48,  // circumflex
        '\u0308' => 50,  // diaeresis
        '\u030C' => 52,  // caron
        '\u0306' => 54,  // breve
        '\u0304' => 56,  // macron
        '\u0303' => 62,  // tilde
        '\u030A' => 64,  // ring above
        '\u0328' => 66,  // ogonek
        '\u030B' => 68,  // double acute
        '\u0327' => 86,  // cedilla
        '\u0309' => 142, // hook above
        '\u030F' => 144, // double grave
        '\u0311' => 146, // inverted breve
        '\u031B' => 180, // horn
        '\u0323' => 200, // dot below
        '\u0324' => 202, // diaeresis below
        '\u0325' => 204, // ring below
        '\u0326' => 206, // comma below
        '\u032E' => 208, // breve below
        '\u032D' => 224, // circumflex below
        '\u0330' => 226, // tilde below
        '\u0331' => 228, // macron below
        _ => (ushort)(230 + 2 * (mark % 8)),
    };

    private static ushort AddMark(ushort weight, ushort mark) => (ushort)Math.Min(ushort.MaxValue, weight + mark);

    private static int Letter(char asciiLetter) => LetterBase + 4 * (char.ToLowerInvariant(asciiLetter) - 'a');

    static NaturalComparer()
    {
        var extra = new Dictionary<char, CharInfo>();
        var measured = new HashSet<char>(); // characters given weights below (the rest keep Generic's)

        void Set(char c, CharInfo info)
        {
            measured.Add(c);
            if (c < LowEnd) s_low[c] = info;
            else if (c >= MidStart && c < MidEnd) s_mid[c - MidStart] = info;
            else extra[c] = info;
        }
        CharInfo Get(char c) =>
            c < LowEnd ? s_low[c] : c >= MidStart && c < MidEnd ? s_mid[c - MidStart] : extra.TryGetValue(c, out var i) ? i : Generic(c);

        for (int c = 0; c < LowEnd; c++) s_low[c] = Generic((char)c);
        for (int c = MidStart; c < MidEnd; c++) s_mid[c - MidStart] = Generic((char)c);

        for (int g = 0; g < SymbolGroups.Length; g++)
            for (int t = 0; t < SymbolGroups[g].Length; t++)
                Set(SymbolGroups[g][t], new CharInfo { Kind = Kind.Normal, Primary = 1 + g, Tertiary = (byte)t });
        for (int k = 0; k < Specials.Length; k++)
            Set(Specials[k], new CharInfo { Kind = Kind.Special, Primary = 1 + k });
        foreach (char c in DigitLike)
            Set(c, new CharInfo { Kind = Kind.DigitLike, Primary = (int)(4 * CharUnicodeInfo.GetNumericValue(c)), Tertiary = 1 });
        Set('\u00AD', new CharInfo { Kind = Kind.Ignorable });

        for (char c = 'a'; c <= 'z'; c++)
        {
            Set(c, new CharInfo { Kind = Kind.Normal, Primary = Letter(c) });
            Set(char.ToUpperInvariant(c), new CharInfo { Kind = Kind.Normal, Primary = Letter(c) });
        }
        Set('\u00AA', new CharInfo { Kind = Kind.Normal, Primary = Letter('a'), Tertiary = 2 }); // ª
        Set('\u00BA', new CharInfo { Kind = Kind.Normal, Primary = Letter('o'), Tertiary = 2 }); // º
        for (int g = 0; g < Clicks.Length; g++)
            foreach (char c in Clicks[g]) Set(c, new CharInfo { Kind = Kind.Normal, Primary = DigitLikeBase - Clicks.Length + g });
        for (int g = 0; g < ToneLetters.Length; g++)
            foreach (char c in ToneLetters[g]) Set(c, new CharInfo { Kind = Kind.Normal, Primary = LetterBase - ToneLetters.Length + g });
        foreach (var (chars, b, accent) in LetterVariants)
            foreach (char c in chars) Set(c, new CharInfo { Kind = Kind.Normal, Primary = Letter(b), Secondary = accent });
        foreach (var (chars, b, slot) in LetterSlots)
            foreach (char c in chars) Set(c, new CharInfo { Kind = Kind.Normal, Primary = Letter(b) + slot });
        foreach (var (chars, first, second, accent) in Expansions)
            foreach (char c in chars)
                Set(c, new CharInfo { Kind = Kind.Expansion, Primary = Letter(first), Primary2 = Letter(second), Secondary2 = accent });

        // Accented letters: base letter's weights plus the decomposition's accents (é = e + acute, ǜ = u + ¨ + `).
        var manual = new HashSet<char>();
        foreach (var (chars, _, _) in LetterVariants) manual.UnionWith(chars);
        foreach (var (chars, _, _) in LetterSlots) manual.UnionWith(chars);
        foreach (var (chars, _, _, _) in Expansions) manual.UnionWith(chars);
        foreach (var g in Clicks.Concat(ToneLetters)) manual.UnionWith(g);
        void Decompose(int from, int to, bool latin)
        {
            for (int c = from; c < to; c++)
            {
                char ch = (char)c;
                if (!char.IsLetter(ch) || manual.Contains(ch) || (ch < 0x80)) continue;
                string d;
                try { d = ch.ToString().Normalize(NormalizationForm.FormD); }
                catch (Exception) { continue; } // no normalization data (invariant globalization): keep the generic weight
                if (d.Length < 2) continue;
                var info = Get(d[0]);
                if (info.Kind is not (Kind.Normal or Kind.Expansion)) continue;
                if (!latin && d[0] >= 0x80 && !manual.Contains(d[0]))
                    info = new CharInfo { Kind = Kind.Normal, Primary = Script(char.ToUpperInvariant(d[0])) };
                for (int k = 1; k < d.Length; k++) info.Secondary = AddMark(info.Secondary, MarkWeight(d[k]));
                Set(ch, info);
            }
        }
        Decompose(0x00C0, 0x0250, latin: true);
        Decompose(0x1E00, 0x1F00, latin: true);
        Decompose(0x0370, LowEnd, latin: false);
        Decompose(0x1F00, 0x2000, latin: false);

        // Case partners of measured letters (ɓ = Ɓ, ɵ = Ɵ): case never matters.
        void CopyCase(int from, int to)
        {
            for (int c = from; c < to; c++)
            {
                char ch = (char)c;
                if (!char.IsLetter(ch) || measured.Contains(ch)) continue;
                char upper = char.ToUpperInvariant(ch), lower = char.ToLowerInvariant(ch);
                if (upper != ch && measured.Contains(upper)) Set(ch, Get(upper));
                else if (lower != ch && measured.Contains(lower)) Set(ch, Get(lower));
            }
        }
        CopyCase(0, LowEnd);
        CopyCase(MidStart, MidEnd);
        CopyCase(0x2C60, 0x2C80); // Latin Extended-C (Ȿ, ⱥ …)
        CopyCase(0xA720, 0xA800); // Latin Extended-D

        // Fullwidth ASCII: the ASCII character's weights, one width step heavier, unless measured above.
        for (int c = 0xFF01; c <= 0xFF5E; c++)
        {
            char ascii = (char)(c - 0xFEE0);
            if (extra.ContainsKey((char)c) || char.IsDigit(ascii)) continue;
            var info = Get(ascii);
            info.Tertiary = (byte)(info.Tertiary + 1);
            extra[(char)c] = info;
        }
        // Halfwidth katakana sort with katakana, a width step lighter (ｱ < ア < あ).
        for (int c = 0xFF66; c <= 0xFF9D; c++)
        {
            string k;
            try { k = ((char)c).ToString().Normalize(NormalizationForm.FormKC); }
            catch (Exception) { continue; }
            if (k.Length != 1) continue;
            var info = Get(k[0]);
            info.Tertiary = 0;
            extra[(char)c] = info;
        }

        for (int c = 0; c < 128; c++)
        {
            var info = s_low[c];
            s_ascii[c] = info.Kind switch
            {
                Kind.Normal => info.Primary,
                Kind.Digit => AsciiDigit,
                _ => AsciiSkip,
            };
        }
        s_extra = extra.ToFrozenDictionary();
    }

    /// <summary>Weights for characters the tables don't list, by Unicode category. Allocation-free.</summary>
    private static CharInfo Generic(char c)
    {
        if (char.IsLowSurrogate(c)) return new CharInfo { Kind = Kind.Normal, Primary = LowSurrogateBase + (c - 0xDC00) };
        if (char.IsHighSurrogate(c)) return new CharInfo { Kind = Kind.Normal, Primary = Script(c) };
        // Currency signs newer than Windows' sort table carry no weight at all there.
        if (c is >= '\u20B6' and <= '\u20C0') return new CharInfo { Kind = Kind.Ignorable };
        // Spacing modifier letters (ʼ ˆ ˈ …) sort as symbols.
        if (c is >= '\u02B0' and <= '\u02FF') return new CharInfo { Kind = Kind.Normal, Primary = UnknownSymbolBase + c };
        // Hiragana sorts with the matching katakana, a variant step heavier.
        if (c is >= '\u3041' and <= '\u3096') return new CharInfo { Kind = Kind.Normal, Primary = Script((char)(c + 0x60)), Tertiary = 2 };
        if (c is >= '\u30A1' and <= '\u30F6') return new CharInfo { Kind = Kind.Normal, Primary = Script(c), Tertiary = 1 };
        switch (char.GetUnicodeCategory(c))
        {
            case UnicodeCategory.DecimalDigitNumber:
                return new CharInfo { Kind = Kind.Digit, Primary = DigitRun, Tertiary = (byte)(c < 0x80 ? 0 : 1 + ((c >> 4) & 0x7F)) };
            case UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark:
                return new CharInfo { Kind = Kind.Mark, Secondary = MarkWeight(c) };
            case UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned:
                return new CharInfo { Kind = Kind.Ignorable };
            case UnicodeCategory.SpaceSeparator:
                return new CharInfo { Kind = Kind.Normal, Primary = 1, Tertiary = 9 }; // with the space group
            case UnicodeCategory.OtherNumber or UnicodeCategory.LetterNumber:
                return new CharInfo { Kind = Kind.Normal, Primary = DigitLikeBase + 8 + c };
            case UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                return new CharInfo { Kind = Kind.Normal, Primary = UnknownSymbolBase + c };
            default:
                return new CharInfo { Kind = Kind.Normal, Primary = Script(char.ToUpperInvariant(c)) };
        }
    }

    /// <summary>Primary weight of a non-Latin letter: Windows' script order first, then code point within the script.</summary>
    private static int Script(char c) => OtherBase + ScriptRank(c) * 0x20000 + 2 * c;

    /// <summary>Script order measured from StrCmpLogicalW (Kana before Hebrew, emoji before Hangul and CJK, ...).</summary>
    private static int ScriptRank(char c) => c switch
    {
        >= '\u0370' and <= '\u03FF' or >= '\u1F00' and <= '\u1FFF' => 1,                              // Greek
        >= '\u0400' and <= '\u052F' or >= '\u1C80' and <= '\u1C8F' or >= '\u2DE0' and <= '\u2DFF'
            or >= '\uA640' and <= '\uA69F' => 2,                                                         // Cyrillic
        >= '\u0530' and <= '\u058F' => 3,                                                               // Armenian
        >= '\u10A0' and <= '\u10FF' or >= '\u2D00' and <= '\u2D2F' => 4,                              // Georgian
        >= '\u2C00' and <= '\u2C5F' => 5,                                                               // Glagolitic
        >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF' or >= '\uFF66' and <= '\uFF9F' => 6, // Kana
        >= '\u0590' and <= '\u05FF' or >= '\uFB1D' and <= '\uFB4F' => 7,                              // Hebrew
        >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\u08A0' and <= '\u08FF'
            or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF' => 8,                          // Arabic
        >= '\u0700' and <= '\u074F' => 9,                                                               // Syriac
        >= '\u0780' and <= '\u07BF' => 10,                                                              // Thaana
        >= '\u0900' and <= '\u0DFF' => 11,                                                              // Indic
        >= '\u0E00' and <= '\u0E7F' => 12,                                                              // Thai
        >= '\u1800' and <= '\u18AF' => 13,                                                              // Mongolian
        >= '\u0F00' and <= '\u0FFF' => 14,                                                              // Tibetan
        >= '\u0E80' and <= '\u0EFF' => 15,                                                              // Lao
        >= '\u1780' and <= '\u17FF' => 16,                                                              // Khmer
        >= '\u13A0' and <= '\u13FF' => 17,                                                              // Cherokee
        >= '\u1200' and <= '\u139F' => 18,                                                              // Ethiopic
        >= '\uA000' and <= '\uA4CF' => 20,                                                              // Yi
        >= '\u3100' and <= '\u312F' or >= '\u31A0' and <= '\u31BF' => 21,                             // Bopomofo
        >= '\uD800' and <= '\uDBFF' => 22,                                                              // emoji, other planes
        >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F' or >= '\uA960' and <= '\uA97F'
            or >= '\uAC00' and <= '\uD7FF' => 23,                                                        // Hangul
        >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' => 24, // CJK
        _ => 19,                                                                                         // anything else
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CharInfo Info(char c)
    {
        if (c < LowEnd) return s_low[c];
        if ((uint)(c - MidStart) < (uint)(MidEnd - MidStart)) return s_mid[c - MidStart];
        return InfoSlow(c);
    }

    private static CharInfo InfoSlow(char c) => s_extra.TryGetValue(c, out var info) ? info : Generic(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char c) => c < 0x80 ? (uint)(c - '0') <= 9 : Info(c).Kind == Kind.Digit;

    private static int DigitValue(char c) => c < 0x80 ? c - '0' : CharUnicodeInfo.GetDecimalDigitValue(c);

    // ---------------------------------------------------------------------------------------------------------------
    // Levels

    private static int ComparePrimary(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        // Names in one folder often share long prefixes ("IMG_2024..."): identical ASCII non-digits weigh the same, so
        // skip them. Stopping at the first digit keeps any number that starts here whole.
        int i = 0, n = Math.Min(x.Length, y.Length);
        while (i < n && x[i] == y[i] && x[i] < 0x80 && s_ascii[x[i]] != AsciiDigit) i++;
        int j = i, pendingX = 0, pendingY = 0;
        while (true)
        {
            // Fast path: both sides at a plain ASCII symbol or letter.
            if (i < x.Length && j < y.Length && (pendingX | pendingY) == 0 && (x[i] | y[j]) < 0x80)
            {
                int a = s_ascii[x[i]], b = s_ascii[y[j]];
                if (a > 0 && b > 0)
                {
                    if (a != b) return a < b ? -1 : 1;
                    i++;
                    j++;
                    continue;
                }
            }
            int wx = NextPrimary(x, ref i, ref pendingX, out int numX);
            int wy = NextPrimary(y, ref j, ref pendingY, out int numY);
            if (wx != wy) return wx < wy ? -1 : 1; // End is 0, so the shorter string sorts first
            if (wx == End) return 0;
            if (wx == DigitRun)
            {
                int c = numX > 0 && numY > 0
                    ? CompareNumbers(x.Slice(i - numX, numX), y.Slice(j - numY, numY))
                    : CompareDigitLike(numX > 0 ? DigitCode(x.Slice(i - numX, numX)) : -numX - 1, numX > 0,
                                       numY > 0 ? DigitCode(y.Slice(j - numY, numY)) : -numY - 1, numY > 0);
                if (c != 0) return c;
            }
        }
    }

    /// <summary>
    /// Next primary weight from <paramref name="i"/>. Numbers return DigitRun with <paramref name="number"/> = the run's
    /// length; superscript digits and fractions also return DigitRun, with number = -(4 × value + 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NextPrimary(ReadOnlySpan<char> s, ref int i, ref int pending, out int number)
    {
        number = 0;
        if (pending != 0)
        {
            int p = pending;
            pending = 0;
            return p;
        }
        while (i < s.Length)
        {
            char c = s[i];
            if (c < 0x80)
            {
                int w = s_ascii[c];
                if (w > 0)
                {
                    i++;
                    return w;
                }
                if (w == AsciiDigit) return DigitRunAt(s, ref i, out number);
                i++;
                continue;
            }
            var info = Info(c);
            switch (info.Kind)
            {
                case Kind.Normal:
                    i++;
                    return info.Primary;
                case Kind.Digit:
                    return DigitRunAt(s, ref i, out number);
                case Kind.DigitLike:
                    i++;
                    number = -info.Primary - 1;
                    return DigitRun;
                case Kind.Expansion:
                    i++;
                    pending = info.Primary2;
                    return info.Primary;
                default:
                    i++; // ignorable, special or combining mark: no primary weight
                    break;
            }
        }
        return End;
    }

    /// <summary>
    /// A number is a run of digits from one script ("1" + Arabic-Indic "٢" are two numbers) of at most 19 characters:
    /// StrCmpLogicalW cuts longer runs into 19-character numbers (leading zeros included), and so does this.
    /// </summary>
    private static int DigitRunAt(ReadOnlySpan<char> s, ref int i, out int runLength)
    {
        int start = i, end = Math.Min(s.Length, i + MaxNumberLength);
        if (s[i] < 0x80)
        {
            do i++;
            while (i < end && (uint)(s[i] - '0') <= 9);
        }
        else
        {
            int zero = s[i] - DigitValue(s[i]);
            do i++;
            while (i < end && IsDigit(s[i]) && s[i] - DigitValue(s[i]) == zero);
        }
        runLength = i - start;
        return DigitRun;
    }

    private const int MaxNumberLength = 19;

    /// <summary>4 × the leading significant digit of a run (0 for all zeros), comparable with a digit-like code.</summary>
    private static int DigitCode(ReadOnlySpan<char> run)
    {
        foreach (char c in run)
        {
            int v = DigitValue(c);
            if (v != 0) return 4 * v;
        }
        return 0;
    }

    /// <summary>
    /// A superscript digit or fraction against a number: Windows weighs it like a single digit of that value, so
    /// "x²" sorts after "x10" (2 &gt; 1) but before "x3". A tie is settled at the width level ("2" &lt; "²" &lt; "２").
    /// </summary>
    private static int CompareDigitLike(int codeX, bool runX, int codeY, bool runY) =>
        codeX == codeY ? 0 : codeX < codeY ? -1 : 1;

    /// <summary>Numeric order of two digit runs of any length; equal values: more leading zeros first.</summary>
    private static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int za = 0, zb = 0;
        while (za < a.Length && DigitValue(a[za]) == 0) za++;
        while (zb < b.Length && DigitValue(b[zb]) == 0) zb++;
        int la = a.Length - za, lb = b.Length - zb;
        if (la != lb) return la < lb ? -1 : 1;
        for (int k = 0; k < la; k++)
        {
            int da = DigitValue(a[za + k]), db = DigitValue(b[zb + k]);
            if (da != db) return da < db ? -1 : 1;
        }
        // StrCmpLogicalW settles leading zeros right here, before any later character: "a01b2" < "a1b1".
        return a.Length == b.Length ? 0 : a.Length > b.Length ? -1 : 1;
    }

    /// <summary>Accent (secondary) or width (tertiary) weights, element by element.</summary>
    private static int CompareLevel(ReadOnlySpan<char> x, ReadOnlySpan<char> y, bool secondary)
    {
        int i = 0, j = 0, pendingX = -1, pendingY = -1;
        while (true)
        {
            int wx = NextWeight(x, ref i, ref pendingX, secondary);
            int wy = NextWeight(y, ref j, ref pendingY, secondary);
            if (wx != wy) return wx < wy ? -1 : 1;
            if (wx < 0) return 0;
        }
    }

    private static int NextWeight(ReadOnlySpan<char> s, ref int i, ref int pending, bool secondary)
    {
        if (pending >= 0)
        {
            int p = pending;
            pending = -1;
            return p;
        }
        // A combining mark with no letter before it (string start, after a hyphen) adds its accent to the next letter,
        // or stands alone at the end; at the width level it still ranks the string after one without it.
        int stray = -1;
        while (i < s.Length)
        {
            var info = Info(s[i++]);
            if (info.Kind == Kind.Mark)
            {
                if (!secondary) return 1;
                stray = Math.Max(stray, 0) + info.Secondary;
                continue;
            }
            if (info.Kind is not (Kind.Normal or Kind.Digit or Kind.DigitLike or Kind.Expansion)) continue;
            int accent = info.Secondary + Math.Max(stray, 0);
            for (; i < s.Length && Info(s[i]).Kind == Kind.Mark; i++)
                if (secondary) accent += Info(s[i]).Secondary;
            if (info.Kind == Kind.Expansion) pending = secondary ? info.Secondary2 : 0;
            return secondary ? accent : info.Tertiary;
        }
        return stray;
    }

    /// <summary>The hyphens/apostrophes ignored so far: none first; a later one weighs less than an earlier one.</summary>
    private static int CompareSpecials(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        int i = 0, j = 0, positionX = 0, positionY = 0;
        while (true)
        {
            int kx = NextSpecial(x, ref i, ref positionX), ky = NextSpecial(y, ref j, ref positionY);
            if (kx == 0 || ky == 0) return kx == ky ? 0 : kx == 0 ? -1 : 1;
            if (positionX != positionY) return positionX > positionY ? -1 : 1;
            if (kx != ky) return kx < ky ? -1 : 1;
        }
    }

    /// <summary>Next special's weight; <paramref name="position"/> counts the characters that carry any weight.</summary>
    private static int NextSpecial(ReadOnlySpan<char> s, ref int i, ref int position)
    {
        while (i < s.Length)
        {
            var info = Info(s[i++]);
            if (info.Kind == Kind.Ignorable) continue;
            position++;
            if (info.Kind == Kind.Special) return info.Primary;
        }
        return 0;
    }
}
