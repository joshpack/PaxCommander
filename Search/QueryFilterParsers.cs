using System.Globalization;

namespace pxdCommander.Search;

/// <summary>Value parsers for size:, modified: and created:. All of them report failure instead of throwing.</summary>
internal static class QueryFilterParsers
{
    private const long KB = 1024, MB = KB * 1024, GB = MB * 1024;

    /// <summary>
    /// Parses a size: value into an inclusive byte range. Forms: &gt;10mb, &gt;=10mb, &lt;1kb, &lt;=1kb, =1234 (exact),
    /// 1mb..5mb (inclusive; a unit on the right also applies to a bare left side, either side may be left open),
    /// a bare value (within ±10 %; a bare 0 means empty), and Explorer's buckets empty|tiny|small|medium|large|huge|gigantic.
    /// Units: b, kb, mb, gb, tb (1024-based; k, m, g, t and kib.. also accepted), decimals allowed; no unit = bytes.
    /// </summary>
    internal static bool TryParseSize(string value, out long min, out long max, out string description)
    {
        min = 0; max = long.MaxValue; description = "";
        var v = value.AsSpan().Trim();
        if (v.IsEmpty) return false;

        switch (v.ToString().ToLowerInvariant())
        {
            case "empty": max = 0; description = "empty"; return true;
            case "tiny": min = 1; max = 16 * KB; description = "tiny (up to 16 KB)"; return true;
            case "small": min = 16 * KB + 1; max = MB; description = "small (16 KB to 1 MB)"; return true;
            case "medium": min = MB + 1; max = 128 * MB; description = "medium (1 MB to 128 MB)"; return true;
            case "large": min = 128 * MB + 1; max = GB; description = "large (128 MB to 1 GB)"; return true;
            case "huge": min = GB + 1; max = 4 * GB; description = "huge (1 GB to 4 GB)"; return true;
            case "gigantic": min = 4 * GB + 1; description = "gigantic (over 4 GB)"; return true;
        }

        int range = v.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            var left = v[..range].Trim();
            var right = v[(range + 2)..].Trim();
            if (left.IsEmpty && right.IsEmpty) return false;
            Amount hi = default, lo = default;
            if (!right.IsEmpty && !TryParseAmount(right, null, out hi)) return false;
            if (!left.IsEmpty && !TryParseAmount(left, right.IsEmpty ? null : hi, out lo)) return false;
            if (!left.IsEmpty) min = ToBytes(lo.Bytes);
            if (!right.IsEmpty) max = ToBytes(hi.Bytes);
            bool swapped = min > max;
            if (swapped) (min, max) = (max, min);
            description = left.IsEmpty ? $"{hi.Text} or smaller"
                : right.IsEmpty ? $"{lo.Text} or larger"
                : swapped ? $"between {hi.Text} and {lo.Text}" : $"between {lo.Text} and {hi.Text}";
            return true;
        }

        string op = Operator(v);
        if (!TryParseAmount(v[op.Length..].Trim(), null, out var a)) return false;
        long bytes = ToBytes(a.Bytes);
        switch (op)
        {
            case ">": min = bytes == long.MaxValue ? long.MaxValue : bytes + 1; description = $"larger than {a.Text}"; break;
            case ">=": min = bytes; description = $"{a.Text} or larger"; break;
            case "<": max = bytes - 1; description = $"smaller than {a.Text}"; break;
            case "<=": max = bytes; description = $"{a.Text} or smaller"; break;
            case "=": min = max = bytes; description = $"exactly {a.Text}"; break;
            default:
                // A bare size means "about this big": listings round sizes, so an exact match would rarely hit.
                if (bytes == 0) { max = 0; description = "empty"; }
                else
                {
                    min = ToBytes(Math.Floor(a.Bytes * 0.9));
                    max = ToBytes(Math.Ceiling(a.Bytes * 1.1));
                    description = $"about {a.Text}";
                }
                break;
        }
        return true;
    }

    private static string Operator(ReadOnlySpan<char> v) =>
        v.StartsWith(">=") ? ">=" : v.StartsWith("<=") ? "<=" : v.StartsWith(">") ? ">"
        : v.StartsWith("<") ? "<" : v.StartsWith("=") ? "=" : "";

    private readonly record struct Amount(double Bytes, double Multiplier, string Text);

    private static bool TryParseAmount(ReadOnlySpan<char> s, Amount? unitFrom, out Amount amount)
    {
        amount = default;
        int k = 0;
        while (k < s.Length && (char.IsAsciiDigit(s[k]) || s[k] == '.')) k++;
        if (k == 0 || !double.TryParse(s[..k], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number))
            return false;
        var unit = s[k..].Trim();
        double multiplier;
        string label;
        if (unit.IsEmpty)
        {
            // "1..5mb": the left side borrows the right side's unit.
            multiplier = unitFrom?.Multiplier ?? 1;
            label = multiplier == 1 ? (number == 1 ? "byte" : "bytes") : UnitLabel(multiplier);
        }
        else if (!TryUnit(unit, out multiplier, out label)) return false;
        else if (multiplier == 1 && number == 1) label = "byte";
        double bytes = number * multiplier;
        if (!double.IsFinite(bytes) || bytes < 0) return false;
        amount = new Amount(bytes, multiplier, $"{number.ToString(CultureInfo.InvariantCulture)} {label}");
        return true;
    }

    private static bool TryUnit(ReadOnlySpan<char> unit, out double multiplier, out string label)
    {
        switch (unit.ToString().ToLowerInvariant())
        {
            case "b" or "byte" or "bytes": multiplier = 1; label = "bytes"; return true;
            case "k" or "kb" or "kib": multiplier = KB; label = "KB"; return true;
            case "m" or "mb" or "mib": multiplier = MB; label = "MB"; return true;
            case "g" or "gb" or "gib": multiplier = GB; label = "GB"; return true;
            case "t" or "tb" or "tib": multiplier = GB * 1024.0; label = "TB"; return true;
            default: multiplier = 0; label = ""; return false;
        }
    }

    private static string UnitLabel(double multiplier) => multiplier switch
    {
        KB => "KB", MB => "MB", GB => "GB", >= GB * 1024.0 => "TB", _ => "bytes",
    };

    private static long ToBytes(double bytes) =>
        bytes >= long.MaxValue ? long.MaxValue : bytes <= 0 ? 0 : (long)Math.Round(bytes, MidpointRounding.AwayFromZero);

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A point in time (an age such as 7d: Start == End) or a local calendar interval [Start, End) in UTC ticks.
    /// Text reads after "since/before/between" ("2026-01-01", "today", "7 days ago"); Alone reads on its own
    /// ("on 2026-01-01", "in 2026-01", "today", "in the last 7 days").
    /// </summary>
    private readonly record struct TimeSpec(bool IsAge, long Start, long End, string Text, string Alone);

    /// <summary>
    /// Parses a modified:/created: value into a UTC tick range [min, max). Forms: today, yesterday, thisweek, lastweek,
    /// thismonth, lastmonth, thisyear, lastyear; ages 12h 7d 2w 3mo 1y (also min, hours, days, weeks, months, years)
    /// meaning "within the last ..."; &lt;age = newer than, &gt;age = older than; dates 2026, 2026-01, 2026-01-01 (local
    /// time, whole period); &gt;date = after it, &gt;=date = since, &lt;date = before, &lt;=date = until; a..b ranges cover
    /// both ends' whole periods and may be open on one side.
    /// </summary>
    internal static bool TryParseTime(string value, DateTime nowUtc, out long min, out long max, out string description)
    {
        min = long.MinValue; max = long.MaxValue; description = "";
        var v = value.AsSpan().Trim();
        if (v.IsEmpty) return false;

        int range = v.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            var left = v[..range].Trim();
            var right = v[(range + 2)..].Trim();
            if (left.IsEmpty && right.IsEmpty) return false;
            TimeSpec a = default, b = default;
            if (!left.IsEmpty && !TryParseSpec(left, nowUtc, out a)) return false;
            if (!right.IsEmpty && !TryParseSpec(right, nowUtc, out b)) return false;
            if (left.IsEmpty) { max = b.End; description = $"until {b.Text}"; }
            else if (right.IsEmpty) { min = a.Start; description = $"since {a.Text}"; }
            else
            {
                // Either order works; the range spans from the earlier start to the later end.
                min = Math.Min(a.Start, b.Start);
                max = Math.Max(a.End, b.End);
                var (first, second) = a.Start <= b.Start ? (a, b) : (b, a);
                description = $"between {first.Text} and {second.Text}";
            }
            return true;
        }

        string op = Operator(v);
        if (!TryParseSpec(v[op.Length..].Trim(), nowUtc, out var s)) return false;
        if (s.IsAge)
        {
            // Ages compare by age: <30d is newer than 30 days, >1y is older than a year.
            if (op is ">" or ">=") { max = s.Start; description = $"more than {s.Text}"; }
            else { min = s.Start; description = s.Alone; }
            return true;
        }
        switch (op)
        {
            case ">": min = s.End; description = $"after {s.Text}"; break;
            case ">=": min = s.Start; description = $"since {s.Text}"; break;
            case "<": max = s.Start; description = $"before {s.Text}"; break;
            case "<=": max = s.End; description = $"until {s.Text}"; break;
            default: min = s.Start; max = s.End; description = s.Alone; break;
        }
        return true;
    }

    private static bool TryParseSpec(ReadOnlySpan<char> s, DateTime nowUtc, out TimeSpec spec)
    {
        spec = default;
        if (s.IsEmpty) return false;
        DateTime today = nowUtc.ToLocalTime().Date;
        string word = s.ToString().ToLowerInvariant().Replace("-", "");
        bool last = word.StartsWith("last", StringComparison.Ordinal);
        switch (word)
        {
            case "today": spec = Named(today, today.AddDays(1), "today"); return true;
            case "yesterday": spec = Named(today.AddDays(-1), today, "yesterday"); return true;
            case "thisweek" or "lastweek":
            {
                var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
                var start = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7)).AddDays(last ? -7 : 0);
                spec = Named(start, start.AddDays(7), last ? "last week" : "this week");
                return true;
            }
            case "thismonth" or "lastmonth":
            {
                var start = new DateTime(today.Year, today.Month, 1).AddMonths(last ? -1 : 0);
                spec = Named(start, start.AddMonths(1), last ? "last month" : "this month");
                return true;
            }
            case "thisyear" or "lastyear":
            {
                var start = new DateTime(today.Year - (last ? 1 : 0), 1, 1);
                spec = Named(start, start.AddYears(1), last ? "last year" : "this year");
                return true;
            }
        }
        return TryParseAge(s, nowUtc, out spec) || TryParseDate(s, out spec);
    }

    private static TimeSpec Named(DateTime localStart, DateTime localEnd, string text) =>
        new(false, LocalToUtcTicks(localStart), LocalToUtcTicks(localEnd), text, text);

    private static long LocalToUtcTicks(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().Ticks;

    private static bool TryParseAge(ReadOnlySpan<char> s, DateTime nowUtc, out TimeSpec spec)
    {
        spec = default;
        int k = 0;
        while (k < s.Length && char.IsAsciiDigit(s[k])) k++;
        if (k == 0 || k > 6 || !int.TryParse(s[..k], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
        string name = s[k..].Trim().ToString().ToLowerInvariant() switch
        {
            // A bare "m" is ambiguous (minutes or months), so it is rejected: write min or mo.
            "min" or "mins" or "minute" or "minutes" => "minute",
            "h" or "hr" or "hrs" or "hour" or "hours" => "hour",
            "d" or "day" or "days" => "day",
            "w" or "wk" or "wks" or "week" or "weeks" => "week",
            "mo" or "mon" or "mos" or "month" or "months" => "month",
            "y" or "yr" or "yrs" or "year" or "years" => "year",
            _ => "",
        };
        if (name.Length == 0) return false;
        long point;
        try
        {
            DateTime at = name switch
            {
                "minute" => nowUtc.AddMinutes(-n),
                "hour" => nowUtc.AddHours(-n),
                "day" => nowUtc.AddDays(-n),
                "week" => nowUtc.AddDays(-7.0 * n),
                // Calendar months/years follow local dates (3mo before May 31 is Feb 28/29), like a date picker would.
                "month" => nowUtc.ToLocalTime().AddMonths(-n).ToUniversalTime(),
                _ => nowUtc.ToLocalTime().AddYears(-n).ToUniversalTime(),
            };
            point = at.Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            point = DateTime.MinValue.Ticks; // further back than the calendar goes: everything is newer
        }
        string amount = n == 1 ? $"1 {name}" : $"{n} {name}s";
        spec = new TimeSpec(true, point, point, $"{amount} ago", n == 1 ? $"in the last {name}" : $"in the last {amount}");
        return true;
    }

    private static bool TryParseDate(ReadOnlySpan<char> s, out TimeSpec spec)
    {
        spec = default;
        Span<int> parts = stackalloc int[3];
        int count = 0, i = 0;
        while (true)
        {
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            int len = i - start;
            if (len == 0 || len > 4 || (count == 0 && len != 4)) return false; // year first, always four digits
            parts[count++] = int.Parse(s[start..i], NumberStyles.None, CultureInfo.InvariantCulture);
            if (i == s.Length) break;
            if (count == 3 || s[i] is not ('-' or '/' or '.')) return false;
            i++;
        }

        int year = parts[0];
        if (year < 1 || year > 9998) return false;
        if (count == 1)
        {
            var y = new DateTime(year, 1, 1);
            string t = year.ToString(CultureInfo.InvariantCulture);
            spec = new TimeSpec(false, LocalToUtcTicks(y), LocalToUtcTicks(y.AddYears(1)), t, $"in {t}");
            return true;
        }
        int month = parts[1];
        if (month < 1 || month > 12) return false;
        if (count == 2)
        {
            var m = new DateTime(year, month, 1);
            string t = m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            spec = new TimeSpec(false, LocalToUtcTicks(m), LocalToUtcTicks(m.AddMonths(1)), t, $"in {t}");
            return true;
        }
        int day = parts[2];
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        var d = new DateTime(year, month, day);
        string text = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        spec = new TimeSpec(false, LocalToUtcTicks(d), LocalToUtcTicks(d.AddDays(1)), text, $"on {text}");
        return true;
    }
}
