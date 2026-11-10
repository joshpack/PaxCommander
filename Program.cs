using Avalonia;
using pxdCommander.Core;

namespace pxdCommander;

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

        // Check of a build: "--self-test <report.txt>" tests the rename migration and the installer against temp folders
        // and a private registry hive. No window, nothing real touched.
        if (args is ["--self-test", var reportPath])
        {
            File.WriteAllText(reportPath, SelfTest.Run());
            return;
        }

        // A quiet setup started by an update before start: what follows "--then" is what the app was started with.
        string[] relaunchArgs = [];
        var thenAt = Array.IndexOf(args, "--then");
        if (thenAt >= 0)
        {
            relaunchArgs = args[(thenAt + 1)..];
            args = args[..thenAt];
        }

        // Update before the app opens (no prompt): a newer release is installed and started with these same arguments.
        // Skipped for the setup / uninstall / --write-icon / --self-test modes, copies just updated and dev builds.
        if (Updater.BeforeStart(args))
            return;

        StartupPaths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var dirArg = Array.IndexOf(args, "--dir");
        if (dirArg >= 0 && dirArg + 1 < args.Length) StartupPaths = StartupPaths.Where(p => p != args[dirArg + 1]).ToArray();
        // Started by an in-place update (portable / pxd Toolkit copy): wait for the old copy to close first.
        var afterUpdate = Array.IndexOf(args, "--after-update");
        if (afterUpdate >= 0 && afterUpdate + 1 < args.Length)
        {
            StartupPaths = StartupPaths.Where(p => p != args[afterUpdate + 1]).ToArray();
            if (int.TryParse(args[afterUpdate + 1], out var oldPid)) Installer.WaitForExit(oldPid);
        }

        // Our own taskbar identity: Windows then takes the icon from our window instead of a stale cached copy
        // of the exe's old icon, and groups our windows together.
        if (OperatingSystem.IsWindows()) SetCurrentProcessExplicitAppUserModelID("pxd.Commander");

        // Build step: "--write-icon <file.ico>" renders the app mark with the real system fonts and exits, no window.
        if (args is ["--write-icon", var icoPath])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            AppIcon.WriteIco(icoPath);
            return;
        }

        // pxdcomm-setup.exe (or --install / --uninstall): the installer screen instead of the app.
        if (OperatingSystem.IsWindows())
        {
            if (args.Contains("--uninstall")) Mode = StartMode.Uninstall;
            else if (args.Contains("--install") || Installer.LooksLikeSetup()) Mode = StartMode.Install;
            if (Mode == StartMode.Uninstall && args.Contains("--quiet"))
            {
                Installer.Uninstall(removeSettings: false);
                return;
            }
            // Silent install for scripts: pxdcomm-setup.exe --quiet [--dir <folder>] [--no-desktop] [--no-folder-menu]
            if (Mode == StartMode.Install && args.Contains("--quiet"))
            {
                var at = Array.IndexOf(args, "--dir");
                var requested = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
                // An update started by a version from before the rename passes its own folder: its default name becomes the new one.
                var folder = Migration.MapFolder(requested) ?? Installer.SuggestedFolder;
                // An update started from the app: let that copy close first, then reopen the new one.
                var wait = Array.IndexOf(args, "--wait-pid");
                if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out var pid)) Installer.WaitForExit(pid);
                try
                {
                    var exe = Installer.InstallAsync(folder, !args.Contains("--no-desktop"), !args.Contains("--no-folder-menu"), _ => { }).GetAwaiter().GetResult();
                    if (args.Contains("--relaunch")) Installer.Relaunch(exe, relaunchArgs);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("quiet install", ex);
                    // Don't leave the user with no app: reopen whatever is there.
                    if (args.Contains("--relaunch"))
                    {
                        var installed = Path.Combine(folder, "pxdCommander.exe");
                        var former = requested is null ? null : Path.Combine(requested, Migration.FormerExeName);
                        Installer.Relaunch(!File.Exists(installed) && former is not null && File.Exists(former) ? former : installed, relaunchArgs);
                    }
                }
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
