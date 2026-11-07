using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaxCommander.Core;

public enum ViewMode { Details, Columns, Tiles }

public enum SortKey { Name, Modified, Type, Size, Created }

public enum FolderSizeMode { Off, LocalDrives, Everywhere }

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; }
}

public sealed class TabSnapshot
{
    public string Path { get; set; } = "";
    public ViewMode View { get; set; }
    public SortKey Sort { get; set; }
    public bool Descending { get; set; }
}

public sealed class PaneSnapshot
{
    public List<TabSnapshot> Tabs { get; set; } = [];
    public int ActiveTab { get; set; }
}

public sealed class SessionSnapshot
{
    public List<PaneSnapshot> Panes { get; set; } = [];
    public int ActivePane { get; set; }
}

/// <summary>Everything the user can change, saved as JSON in %APPDATA%\PaxCommander\settings.json.</summary>
public sealed class AppSettings
{
    public static AppSettings Current { get; private set; } = new();

    // View
    public bool ShowHidden { get; set; }
    public bool ShowSystemFiles { get; set; }
    public bool DualPane { get; set; } = true;
    public bool StackedPanes { get; set; }
    public bool ShowPreview { get; set; } = true;
    /// <summary>With the preview on, only show it for things worth previewing: images, videos and code.</summary>
    public bool AutoPreview { get; set; }
    public bool ShowSidebar { get; set; } = true;
    /// <summary>Drives in the sidebar as compact squares (letter + fill) instead of full rows.</summary>
    public bool DriveTiles { get; set; }
    /// <summary>Mapped network drives (under NETWORK) as tiles instead of rows; separate from DriveTiles.</summary>
    public bool NetworkDriveTiles { get; set; }
    public ViewMode DefaultView { get; set; } = ViewMode.Details;
    public bool CompactRows { get; set; }
    /// <summary>Accent colour as #RRGGBB (see AccentTheme.Presets).</summary>
    public string AccentColor { get; set; } = "#89CFF0";
    /// <summary>"Dark", "Light" or "System" (follow Windows).</summary>
    public string Theme { get; set; } = "Dark";
    public bool RelativeDates { get; set; } = true;
    public bool FoldersFirst { get; set; } = true;
    public FolderSizeMode FolderSizes { get; set; } = FolderSizeMode.LocalDrives;
    public bool ShowSizeBars { get; set; } = true;
    /// <summary>A heart left of each folder's icon: click it to add or remove the folder from favorites.</summary>
    public bool FavoriteHearts { get; set; } = true;
    public int TileSize { get; set; } = 128;
    public double MillerColumnWidth { get; set; } = 250;
    public double NameColumnWidth { get; set; } = 340;
    public double DateColumnWidth { get; set; } = 128;
    public double TypeColumnWidth { get; set; } = 112;
    public double SizeColumnWidth { get; set; } = 96;

    // Behaviour
    public bool SingleClickOpen { get; set; }
    /// <summary>Opening an image shows it in the built-in viewer (PAX Image Viewer) instead of Windows' default app.</summary>
    public bool OpenImagesInViewer { get; set; } = true;
    public bool ConfirmRecycle { get; set; }
    public bool RestoreSession { get; set; } = true;
    /// <summary>Look for a newer release on GitHub once a day (it only installs when you say so).</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>A version the user chose "Skip this version" for.</summary>
    public string? SkippedUpdate { get; set; }
    /// <summary>Command for "Open terminal here"; empty = Windows Terminal, then PowerShell.</summary>
    public string TerminalCommand { get; set; } = "";

    // Places
    public List<string> Favorites { get; set; } = [];
    public bool FavoritesSeeded { get; set; }

    // Layout
    public double SidebarWidth { get; set; } = 230;
    public double PreviewWidth { get; set; } = 340;
    public double OpsHeight { get; set; } = 140;
    public WindowPlacement Window { get; set; } = new();
    /// <summary>Where popped-out terminal windows open (size, position, maximized), as last left.</summary>
    public WindowPlacement? TerminalWindow { get; set; }
    public SessionSnapshot? Session { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => AppPaths.File("settings.json");
    private static Timer? _saveTimer;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // A damaged file must not stop the app from starting; keep a copy so nothing is silently lost.
            CrashLog.Write("settings load", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            Current = new AppSettings();
        }

        if (!Current.FavoritesSeeded)
        {
            Current.Favorites = PathUtil.KnownPlaces().Select(p => p.Path).ToList();
            Current.FavoritesSeeded = true;
        }
    }

    /// <summary>Coalesces bursts of changes (column drags, toggles) into one write.</summary>
    public static void SaveSoon()
    {
        _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(800, Timeout.Infinite);
    }

    private static readonly object SaveGate = new();

    public static void SaveNow()
    {
        lock (SaveGate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Data);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                CrashLog.Write("settings save", ex);
            }
        }
    }
}
