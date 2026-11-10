using System.IO.Pipes;
using System.Text.Json;

namespace pxdCommander.Core;

/// <summary>
/// One running copy per user. A later launch passes its paths over a named pipe and exits; the running copy opens them
/// in tabs (or a new window when no paths were given).
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static string PipeName => "pxdCommander-" + Environment.UserName;
    internal static string MutexName => @"Local\pxdCommander.Instance." + Environment.UserName;

    /// <summary>Whether a copy is already open for this user (without becoming the open copy).</summary>
    public static bool AnotherCopyRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>Raised on a background thread with the paths another launch asked for (empty = just show a window).</summary>
    public static event Action<IReadOnlyList<string>>? Received;

    /// <returns>True when another copy took the request and this process should exit.</returns>
    public static bool TryHandOff(IReadOnlyList<string> paths)
    {
        _mutex = new Mutex(true, MutexName, out var first);
        if (first)
            return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            // Relative paths mean something only in this process's working folder.
            writer.WriteLine(JsonSerializer.Serialize(paths.Select(SafeFullPath).ToArray()));
            writer.Flush();
            return true;
        }
        catch
        {
            // The other copy is shutting down or hung: run on our own rather than doing nothing.
            return false;
        }
    }

    public static void StartServer()
    {
        var thread = new Thread(ServerLoop) { IsBackground = true, Name = "pxdCommander instance pipe" };
        thread.Start();
    }

    private static void ServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                var line = reader.ReadLine();
                var paths = string.IsNullOrWhiteSpace(line) ? [] : JsonSerializer.Deserialize<string[]>(line) ?? [];
                Received?.Invoke(paths);
            }
            catch (Exception ex)
            {
                CrashLog.Write("instance pipe", ex);
                Thread.Sleep(1000);
            }
        }
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }
}
