using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using pxdCommander.Core;
using pxdCommander.Views;

namespace pxdCommander;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _lifetime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (Program.Mode != Program.StartMode.Normal && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime setup)
        {
            ThemeManager.Apply("Dark");
            setup.MainWindow = new SetupWindow(Program.Mode == Program.StartMode.Uninstall);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        AppSettings.Load();
        Frecency.Load();
        Labels.Load();
        ThemeManager.Apply(AppSettings.Current.Theme); // also applies the accent, mixed for the theme
        Core.Remote.Connections.Load();
        M365.Load();

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // A bug in one handler shouldn't take every open tab down with it.
            CrashLog.Write("ui", e.Exception);
            Toasts.Show("Something went wrong: " + e.Exception.Message, ToastKind.Error);
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            lifetime.ShutdownMode = ShutdownMode.OnLastWindowClose;
            var window = new MainWindow(Program.StartupPaths, restoreSession: true);
            lifetime.MainWindow = window;
            lifetime.ShutdownRequested += (_, _) =>
            {
                Frecency.Save();
                AppSettings.SaveNow();
                FolderSizes.SaveNow();
            };
            SingleInstance.Received += paths => Dispatcher.UIThread.Post(() => OnHandOff(paths));
            SingleInstance.StartServer();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Another launch sent us paths: open them as tabs in the most recent window, or open a new window.</summary>
    private void OnHandOff(IReadOnlyList<string> paths)
    {
        if (_lifetime is null) return;
        var target = _lifetime.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsActive) ?? _lifetime.Windows.OfType<MainWindow>().LastOrDefault();
        if (target is null || paths.Count == 0)
        {
            var w = new MainWindow(paths, restoreSession: false);
            w.Show();
            w.Activate();
            return;
        }
        foreach (var p in paths) target.OpenInNewTab(p, false);
        if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
        target.Activate();
    }

    public static void NewWindow(string? path)
    {
        var w = new MainWindow(path is null ? [] : [path], restoreSession: false);
        w.Show();
    }
}
