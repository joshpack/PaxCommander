using System.Globalization;

namespace pxdCommander.Core;

public static class Formatting
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"0 bytes", "812 bytes", "4.2 KB", "13.7 GB" — three significant digits like Explorer.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes:N0} bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>Exact byte count for tooltips and properties: "1,234,567 bytes".</summary>
    public static string Bytes(long bytes) => bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";

    public static string Count(long n, string singular, string? plural = null) =>
        n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    /// <summary>
    /// Relative style: "Just now", "12 min ago", "Today 14:32", "Yesterday 09:10", "Mon 14:32" within a week, then the
    /// short date. Absolute style: short date + time.
    /// </summary>
    public static string Date(DateTime local, bool relative)
    {
        if (local == DateTime.MinValue || local.Year < 1981) return "";
        var culture = CultureInfo.CurrentCulture;
        var time = local.ToString("t", culture);
        if (!relative)
            return local.ToString("d", culture) + " " + time;

        var now = DateTime.Now;
        var age = now - local;
        if (age.TotalSeconds >= 0 && age.TotalSeconds < 60) return "Just now";
        if (age.TotalMinutes >= 0 && age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
        if (local.Date == now.Date) return "Today " + time;
        if (local.Date == now.Date.AddDays(-1)) return "Yesterday " + time;
        if (age.TotalDays > 0 && age.TotalDays < 6) return local.ToString("ddd", culture) + " " + time;
        return local.ToString("d", culture);
    }

    /// <summary>"1:05:07" / "3:42" style durations for job ETAs.</summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        if (t.TotalSeconds >= 1) return $"{t.Minutes}:{t.Seconds:00}";
        return "0:01";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Size((long)bytesPerSecond) + "/s";

    /// <summary>"412 GB free of 1.82 TB"; discs and mounted ISOs are read-only, so just their size.</summary>
    public static string DriveSpace(FileItem drive)
    {
        if (drive.Size <= 0) return drive.IsDisc ? "No disc" : "";
        return drive.IsDisc ? $"{Size(drive.Size)} · read-only" : $"{Size(drive.DriveFree)} free of {Size(drive.Size)}";
    }
}
