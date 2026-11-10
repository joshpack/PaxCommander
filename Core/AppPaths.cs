namespace pxdCommander.Core;

/// <summary>Where the app keeps its own data (settings, history, labels, logs).</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\pxdCommander, or PXDCMD_DATA when set (tests run against a throwaway folder).</summary>
    public static readonly string Data =
        Environment.GetEnvironmentVariable("PXDCMD_DATA") is { Length: > 0 } custom
            ? custom
            : Migration.DataFolder(); // moves the folder from before the rename on first use, before anything reads it

    public static string File(string name) => Path.Combine(Data, name);
}

public static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Appends to errors.log; never throws (it runs inside crash handlers).</summary>
    public static void Write(string what, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Data);
                var path = AppPaths.File("errors.log");
                // Keep the log from growing without bound on a machine that hits the same error all day.
                if (System.IO.File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}: {ex}\n\n");
            }
        }
        catch
        {
            // Nothing sensible left to do.
        }
    }
}
