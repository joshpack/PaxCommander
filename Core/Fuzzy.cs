namespace PaxCommander.Core;

/// <summary>
/// Subsequence matching with the usual bonuses (consecutive letters, word starts, the very start), so "dlpx" finds
/// "Downloads\pax-export". Score 0 = no match.
/// </summary>
public static class Fuzzy
{
    public static double Score(string query, string text)
    {
        if (query.Length == 0) return 1;
        if (text.Length == 0 || query.Length > text.Length) return 0;

        // Plain substring matches are what people expect first; rank them above scattered ones.
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var s = 100.0 + query.Length * 4;
            if (idx == 0) s += 40;
            else if (IsBoundary(text, idx)) s += 25;
            return s - text.Length * 0.15;
        }

        double score = 0;
        var qi = 0;
        var streak = 0;
        var lastMatch = -1;
        for (var ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(text[ti]) != char.ToLowerInvariant(query[qi])) continue;
            var bonus = 1.0;
            if (ti == 0) bonus += 8;
            else if (IsBoundary(text, ti)) bonus += 6;
            if (lastMatch == ti - 1)
            {
                streak++;
                bonus += 3 * streak;
            }
            else
            {
                streak = 0;
                if (lastMatch >= 0) bonus -= Math.Min(3, (ti - lastMatch) * 0.1);
            }
            score += bonus;
            lastMatch = ti;
            qi++;
        }
        if (qi < query.Length) return 0;
        return Math.Max(0.1, score - text.Length * 0.05);
    }

    private static bool IsBoundary(string text, int i)
    {
        var prev = text[i - 1];
        return prev is ' ' or '\\' or '/' or '-' or '_' or '.' or '(' || (char.IsUpper(text[i]) && char.IsLower(prev)) || (char.IsDigit(text[i]) && !char.IsDigit(prev));
    }
}
