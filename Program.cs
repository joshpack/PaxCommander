using Avalonia;
using PaxCommander.Core;

namespace PaxCommander;

internal static class Program
{
    /// <summary>Folders (or files) passed on the command line; each opens in a tab.</summary>
    public static IReadOnlyList<string> StartupPaths { get; private set; } = [];

    public enum StartMode { Normal, Install, Uninstall }

    /// <summary>Normal app, or the installer / uninstaller screen.</summary>
    public static StartMode Mode { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("unobserved task", e.Exception);
            e.SetObserved();
        };

        StartupPaths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var dirArg = Array.IndexOf(args, "--dir");
        if (dirArg >= 0 && dirArg + 1 < args.Length) StartupPaths = StartupPaths.Where(p => p != args[dirArg + 1]).ToArray();

        // Our own taskbar identity: Windows then takes the icon from our window instead of a stale cached copy
        // of the exe's old icon, and groups our windows together.
        if (OperatingSystem.IsWindows()) SetCurrentProcessExplicitAppUserModelID("PAX.Commander");

        // Build step: "--write-icon <file.ico>" renders the app mark with the real system fonts and exits, no window.
        if (args is ["--write-icon", var icoPath])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            AppIcon.WriteIco(icoPath);
            return;
        }

        // paxcomm-setup.exe (or --install / --uninstall): the installer screen instead of the app.
        if (OperatingSystem.IsWindows())
        {
            if (args.Contains("--uninstall")) Mode = StartMode.Uninstall;
            else if (args.Contains("--install") || Installer.LooksLikeSetup()) Mode = StartMode.Install;
            if (Mode == StartMode.Uninstall && args.Contains("--quiet"))
            {
                Installer.Uninstall(removeSettings: false);
                return;
            }
            // Silent install for scripts: paxcomm-setup.exe --quiet [--dir <folder>] [--no-desktop] [--no-folder-menu]
            if (Mode == StartMode.Install && args.Contains("--quiet"))
            {
                var at = Array.IndexOf(args, "--dir");
                var folder = at >= 0 && at + 1 < args.Length ? args[at + 1] : Installer.InstalledFolder ?? Installer.DefaultFolder;
                Installer.InstallAsync(folder, !args.Contains("--no-desktop"), !args.Contains("--no-folder-menu"), _ => { }).GetAwaiter().GetResult();
                return;
            }
            if (Mode != StartMode.Normal)
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return;
            }
        }

        // A second launch (taskbar, "Open with", a script) hands its paths to the running window instead of starting
        // a second copy with its own caches and watchers.
        if (SingleInstance.TryHandOff(StartupPaths))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
