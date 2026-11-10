using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pxdCommander.Core;
using pxdCommander.Core.Remote;
using pxdCommander.Views;

namespace pxdCommander.Screenshots;

/// <summary>
/// pxdCommander.Screenshots.exe &lt;output folder&gt; &lt;sample folder&gt;
/// Builds the made-up sample folders in &lt;sample folder&gt; (pick a short path without your user name in it: the
/// screenshots show it), renders the six README screenshots into &lt;output folder&gt; with Avalonia.Headless and
/// real Skia drawing (no window on screen), then deletes the sample folder. Settings go to a throwaway folder
/// (PXDCMD_DATA); the sidebar lists only the system drive.
/// </summary>
internal static class Program
{
    private static readonly List<string> Log = [];

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    [STAThread]
    public static void Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
        var demo = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "Demo"));
        Directory.CreateDirectory(outDir);
        var data = Path.Combine(Path.GetTempPath(), "pxdCommander-shots-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("PXDCMD_DATA", data);
        var code = 0;
        try
        {
            Demo.Build(demo);
            Seed(demo);
            Sidebar.DriveSource = () => DriveInfo.GetDrives().Where(d => d.Name.StartsWith(Path.GetPathRoot(demo)![..1], StringComparison.OrdinalIgnoreCase)).ToArray();
            using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
            session.Dispatch(async () =>
            {
                await Shots(demo, outDir);
                return 0;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Add("FAILED " + ex);
            code = 1;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "screenshots.log"), Log);
            Thread.Sleep(500);
            foreach (var dir in new[] { demo, data })
                try { DeleteTree(dir); } catch (Exception ex) { File.AppendAllText(Path.Combine(outDir, "screenshots.log"), "cleanup " + dir + ": " + ex.Message + "\n"); }
        }
        Environment.Exit(code);
    }

    /// <summary>Favorites, frequent folders, labels and two saved servers (no passwords), as a user would have them.</summary>
    private static void Seed(string demo)
    {
        var s = AppSettings.Current;
        s.Favorites = [demo, .. new[] { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Projects" }.Select(d => Path.Combine(demo, d))];
        s.FavoritesSeeded = true;
        s.CheckForUpdates = false;
        s.RestoreSession = true;
        AppSettings.SaveNow();

        var app = Path.Combine(demo, "Projects", "weather-app");
        var visits = new (string Path, int Times)[]
        {
            (app, 12), (Path.Combine(app, "src"), 9), (Path.Combine(demo, "Pictures", "Iceland 2026"), 7),
            (Path.Combine(demo, "Documents", "Taxes 2026"), 5), (Path.Combine(demo, "Documents", "Recipes"), 3),
        };
        foreach (var (p, n) in visits)
            for (var i = 0; i < n; i++) Frecency.Visit(p);
        Frecency.Save();

        Labels.Set([Path.Combine(demo, "Documents", "Taxes 2026"), Path.Combine(demo, "Documents", "Invoice-1042.pdf")], 1);
        Labels.Set([Path.Combine(demo, "Documents", "Lease renewal.docx")], 3);
        Labels.Set([Path.Combine(app, "README.md")], 4);

        Connections.Save(new Connection { Name = "Web server", Protocol = RemoteProtocol.Sftp, Host = "web.example.com", User = "deploy", StartPath = "/var/www" });
        Connections.Save(new Connection { Name = "Backups", Protocol = RemoteProtocol.Ftps, Host = "backup.example.com", User = "backup" });
    }

    private static async Task Shots(string demo, string outDir)
    {
        var app = Path.Combine(demo, "Projects", "weather-app");
        var src = Path.Combine(app, "src");
        var album = Path.Combine(demo, "Pictures", "Iceland 2026");
        var docs = Path.Combine(demo, "Documents");
        var downloads = Path.Combine(demo, "Downloads");

        // Warm-up: the first window fills the icon and thumbnail caches from worker threads; render it once and throw it away.
        await Shot(outDir, "warm-up.png", "Dark", dual: true, preview: true,
            [[Tab(app), Tab(src), Tab(docs), Tab(downloads)], [Tab(album, ViewMode.Tiles), Tab(demo)]], 0, _ => Task.CompletedTask);
        File.Delete(Path.Combine(outDir, "warm-up.png"));
        Log.RemoveAt(Log.Count - 1);

        // 1. Two panes: the project with git status, and the photo album as tiles.
        await Shot(outDir, "dual-pane.png", "Dark", dual: true, preview: false,
            [[Tab(app)], [Tab(album, ViewMode.Tiles)]], 0,
            async w => await Select(Views(w)[0], "src"));

        // 2. Code preview beside the file list.
        await Shot(outDir, "code-preview.png", "Dark", dual: false, preview: true,
            [[Tab(docs), Tab(downloads), Tab(src)], [Tab(album)]], 0,
            async w => await Select(Views(w)[0], "forecast.js"),
            activeTab: 2);

        // 3. Search results with an image preview.
        await Shot(outDir, "search.png", "Dark", dual: false, preview: true,
            [[Tab(demo)], [Tab(album)]], 0,
            async w =>
            {
                var v = Views(w)[0];
                await v.SearchAsync("type:image size:>50kb");
                await Settle(3000);
                if (v.ActiveListing?.Items.FirstOrDefault(i => i.Name == "Glacier lagoon.png") is { } hit) v.ActiveSelection?.SelectOnly(hit);
                await Settle(1500);
            });

        // 4. The docked PowerShell terminal under the file list.
        await Shot(outDir, "terminal.png", "Dark", dual: false, preview: false,
            [[Tab(app)], [Tab(album)]], 0,
            async w =>
            {
                var v = Views(w)[0];
                await Select(v, "src");
                v.ToggleTerminal();
                await Settle(4000);
                var term = w.GetVisualDescendants().OfType<TerminalPane>().First();
                await Type(term, "git log --oneline -6");
                await Settle(3000);
                await Type(term, "git status --short");
                await Settle(3000);
            });

        // 5. The command palette, filtered to pane commands.
        await Shot(outDir, "command-palette.png", "Dark", dual: true, preview: false,
            [[Tab(docs)], [Tab(downloads)]], 0,
            async w =>
            {
                typeof(MainWindow).GetMethod("ShowGoAnywhere", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(w, [">pane", null, null]);
                await Settle(800);
            });

        // 6. Light theme, columns view and an image preview.
        await Shot(outDir, "light-theme-columns.png", "Light", dual: false, preview: true,
            [[Tab(album, ViewMode.Columns)], [Tab(docs)]], 0,
            async w => await Select(Views(w)[0], "Northern lights.png"));
    }

    private static TabSnapshot Tab(string path, ViewMode mode = ViewMode.Details) => new() { Path = path, View = mode, Sort = SortKey.Name };

    private static async Task Shot(string outDir, string file, string theme, bool dual, bool preview, List<TabSnapshot>[] panes,
        int activePane, Func<MainWindow, Task> act, int? activeTab = null)
    {
        var s = AppSettings.Current;
        s.DualPane = dual;
        s.ShowPreview = preview;
        s.Theme = theme;
        s.Window = new() ;
        s.Session = new SessionSnapshot
        {
            ActivePane = activePane,
            Panes = panes.Select((tabs, i) => new PaneSnapshot { Tabs = tabs, ActiveTab = i == activePane && activeTab is { } t ? t : 0 }).ToList(),
        };
        ThemeManager.Apply(theme);

        // Every window is "the first one" again, so it restores the session above.
        typeof(MainWindow).GetField("_windowCount", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 0);
        var w = new MainWindow([], restoreSession: true) { Width = 1440, Height = 900 };
        w.Show();
        w.Width = 1440;
        w.Height = 900;
        await Settle(2500);
        await act(w);
        await Settle(1000);
        // Icons arrive from worker threads: draw the rows once more now that they are all in.
        foreach (var v in w.GetVisualDescendants().OfType<FolderView>()) v.InvalidateRows();
        await Settle(800);
        await Settle(1500);
        var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame for " + file);
        frame.Save(Path.Combine(outDir, file));
        Log.Add($"{file}: {frame.PixelSize.Width}×{frame.PixelSize.Height}");
        w.Close();
        await Settle(300);
    }

    /// <summary>The visible folder views, left to right.</summary>
    private static List<FolderView> Views(Window w) =>
        w.GetVisualDescendants().OfType<FolderView>().Where(v => v.IsEffectivelyVisible)
            .OrderBy(v => v.TranslatePoint(default, w)?.X ?? 0).ToList();

    /// <summary>Deletes a folder, including git's read-only object files.</summary>
    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    private static async Task Select(FolderView v, string name)
    {
        v.SelectWhenItAppears(name);
        await Settle(1200);
    }

    private static async Task Type(TerminalPane term, string command)
    {
        var input = (TextBox)typeof(TerminalPane).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(term)!;
        input.Text = command;
        typeof(TerminalPane).GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(term, null);
        await Settle(200);
    }

    /// <summary>Lets loads, thumbnails and layout finish, rendering as it goes.</summary>
    private static async Task Settle(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(25);
        }
    }
}
