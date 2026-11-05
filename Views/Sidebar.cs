using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;

namespace PaxCommander.Views;

public enum OpenTarget { Here, NewTab, OtherPane }

/// <summary>Favorites, drives (with free-space bars), recently used folders and colour labels.</summary>
public sealed class Sidebar : UserControl
{
    private readonly StackPanel _root = new() { Margin = new Thickness(8, 6, 6, 12), Spacing = 1 };
    private readonly DispatcherTimer _drivesTimer;
    private string _driveSignature = "";
    private string? _current;
    private readonly List<(Button Button, string Path)> _entries = [];

    public event Action<string, OpenTarget>? Open;
    public event Action<string, IReadOnlyList<string>, DragDropEffects>? Drop;
    public event Action<int>? ShowLabel;
    public event Action<string, Control>? EntryMenu;
    /// <summary>The eject button on a mounted image was clicked (drive root).</summary>
    public event Action<string>? Unmount;
    public event Action<Core.Remote.Connection, bool>? ConnectionOpen;
    public event Action<Core.Remote.Connection, Control>? ConnectionMenu;
    public event Action? NewConnection;
    /// <summary>The + beside CONNECTIONS: choose a server or a Microsoft 365 connection.</summary>
    public event Action<Control>? NewConnectionMenu;
    public event Action<M365Profile>? M365Open;
    public event Action<M365Profile, Control>? M365Menu;
    /// <summary>Map a network drive (optionally for a given \\server\share).</summary>
    public event Action<string?>? MapDrive;

    public Sidebar()
    {
        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _drivesTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => CheckDrives());
        _drivesTimer.Start();
        Labels.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        Core.Remote.Connections.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        M365.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public void SetCurrent(string? path)
    {
        _current = path;
        foreach (var (b, p) in _entries)
            b.Classes.Set("current", path is not null && PathUtil.Same(p, path));
    }

    private void CheckDrives()
    {
        var sig = string.Join("|", DriveInfo.GetDrives().Select(d => d.Name));
        if (sig != _driveSignature) Rebuild();
    }

    public void Rebuild()
    {
        _root.Children.Clear();
        _entries.Clear();
        _driveSignature = string.Join("|", DriveInfo.GetDrives().Select(d => d.Name));

        var favHeader = Header("FAVORITES");
        DragDrop.SetAllowDrop(favHeader, true);
        favHeader.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = ItemViewBase.PathsFrom(e.DataTransfer).Any(Directory.Exists) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        });
        favHeader.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            // Dropping folders on the heading pins them.
            foreach (var p in ItemViewBase.PathsFrom(e.DataTransfer).Where(Directory.Exists))
                AddFavorite(p);
            e.Handled = true;
        });
        ToolTip.SetTip(favHeader, "Drop folders here to pin them");
        _root.Children.Add(favHeader);

        var known = PathUtil.KnownPlaces().ToDictionary(k => k.Path, k => k, PathUtil.Comparer);
        foreach (var fav in AppSettings.Current.Favorites)
        {
            var glyph = known.TryGetValue(fav, out var k) ? k.Glyph : "folder";
            var name = known.TryGetValue(fav, out var kn) ? kn.Name : PathUtil.DisplayName(fav);
            _root.Children.Add(Entry(name, fav, glyph, glyph == "folder" ? FallbackIcons.KindColor(FileKind.Folder) : (Color?)null));
        }

        // Drives as rows or as compact tiles; the button beside the header switches.
        var tiles = AppSettings.Current.DriveTiles;
        var layout = new Button { Content = Glyphs.Icon(tiles ? "details" : "tiles", 12), Width = 22, Height = 20, CornerRadius = new CornerRadius(5) };
        layout.Classes.Add("icon");
        ToolTip.SetTip(layout, tiles ? "Show drives as a list" : "Show drives as tiles");
        layout.Click += (_, _) =>
        {
            AppSettings.Current.DriveTiles = !AppSettings.Current.DriveTiles;
            AppSettings.SaveSoon();
            Rebuild();
        };
        var drivesHeader = new DockPanel { Margin = new Thickness(0, 6, 2, 0) };
        DockPanel.SetDock(layout, Dock.Right);
        drivesHeader.Children.Add(layout);
        drivesHeader.Children.Add(Header("DRIVES"));
        _root.Children.Add(drivesHeader);
        Panel NewDrivePanel() => tiles ? new WrapPanel { Margin = new Thickness(4, 2, 0, 2), ItemSpacing = AppSettings.Current.CompactRows ? 4 : 6, LineSpacing = AppSettings.Current.CompactRows ? 4 : 6 } : new StackPanel { Spacing = 1 };
        var drivesPanel = NewDrivePanel();
        // Mapped network drives are listed under NETWORK instead (filled by the same pass, placed further down).
        var networkDrivesPanel = NewDrivePanel();
        _root.Children.Add(drivesPanel);
        _ = FillDrivesAsync(drivesPanel, networkDrivesPanel, tiles);

        // Saved SFTP/FTP servers, with a + to add one.
        var add = new Button { Content = Glyphs.Icon("plus", 12), Width = 22, Height = 20, CornerRadius = new CornerRadius(5) };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, "Add a connection: SSH (SFTP), FTP / FTPS, or Microsoft 365 PowerShell");
        add.Click += (_, _) => NewConnectionMenu?.Invoke(add);
        var connHeader = new DockPanel { Margin = new Thickness(0, 6, 2, 0) };
        DockPanel.SetDock(add, Dock.Right);
        connHeader.Children.Add(add);
        connHeader.Children.Add(Header("CONNECTIONS"));
        _root.Children.Add(connHeader);
        var connections = Core.Remote.Connections.All;
        if (connections.Count == 0 && M365.All.Count == 0)
        {
            var hint = new Button { Classes = { "side" }, Content = new TextBlock { Text = "Add a connection…", Foreground = Skin.Faint } };
            hint.Click += (_, _) => NewConnectionMenu?.Invoke(hint);
            _root.Children.Add(hint);
        }
        // Saved Microsoft 365 admin PowerShell connections.
        foreach (var profile in M365.All)
        {
            var mp = profile;
            var b = new Button
            {
                Classes = { "side" },
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        Glyphs.Icon("shield", 16, Skin.Accent),
                        new TextBlock { Text = mp.Display, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            ToolTip.SetTip(b, $"Microsoft 365 PowerShell · {string.Join(", ", mp.Services.Select(M365.ServiceName))}\n{(mp.Upn.Length > 0 ? mp.Upn : mp.Tenant)}\nClick to connect in the terminal · right-click for options");
            b.Click += (_, _) => M365Open?.Invoke(mp);
            b.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Right) M365Menu?.Invoke(mp, b);
            };
            _root.Children.Add(b);
        }
        foreach (var c in connections)
        {
            var conn = c;
            var b = new Button
            {
                Classes = { "side" },
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        Glyphs.Icon(c.Protocol == Core.Remote.RemoteProtocol.Sftp ? "terminal" : "network", 16),
                        new TextBlock { Text = c.Display, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            ToolTip.SetTip(b, $"{c.Url}\nClick to connect · middle-click for a new tab · right-click for options");
            b.Click += (_, _) => ConnectionOpen?.Invoke(conn, false);
            b.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle) ConnectionOpen?.Invoke(conn, true);
                else if (e.InitialPressMouseButton == MouseButton.Right) ConnectionMenu?.Invoke(conn, b);
            };
            _root.Children.Add(b);
        }

        // The local network: browse computers and their shares; the + maps a share to a drive letter.
        var mapButton = new Button { Content = Glyphs.Icon("plus", 12), Width = 22, Height = 20, CornerRadius = new CornerRadius(5) };
        mapButton.Classes.Add("icon");
        ToolTip.SetTip(mapButton, "Map a network drive");
        mapButton.Click += (_, _) =>
        {
            // Network actions live under the + (Map network drive), not as entries in the list.
            var menu = new MenuFlyout();
            var map = new MenuItem { Header = "Map network drive…", Icon = Glyphs.Icon("drive", 14) };
            ToolTip.SetTip(map, @"Give a shared folder (\\server\share) its own drive letter");
            map.Click += (_, _) => MapDrive?.Invoke(null);
            menu.Items.Add(map);
            menu.ShowAt(mapButton);
        };

        var netHeader = new DockPanel { Margin = new Thickness(0, 6, 2, 0) };
        DockPanel.SetDock(mapButton, Dock.Right);
        netHeader.Children.Add(mapButton);
        netHeader.Children.Add(Header("NETWORK"));
        _root.Children.Add(netHeader);
        _root.Children.Add(Entry("Network", PathUtil.NetworkPath, "network", null, "Computers on your network, and the folders they share"));
        _root.Children.Add(networkDrivesPanel);

        var recent = Frecency.Ranked(40)
            .Select(r => r.Path)
            .Where(p => !AppSettings.Current.Favorites.Contains(p, PathUtil.Comparer) && !PathUtil.IsRoot(p))
            .Take(7)
            .ToList();
        if (recent.Count > 0)
        {
            var clear = new Button { Content = "Clear all", FontSize = 10.5, Padding = new Thickness(6, 1), MinHeight = 0, Height = 20, Width = double.NaN };
            clear.Classes.Add("icon");
            ToolTip.SetTip(clear, "Forget every recent folder");
            clear.Click += (_, _) =>
            {
                Frecency.Clear();
                Rebuild();
            };
            var header = new DockPanel { Margin = new Thickness(0, 6, 2, 0) };
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
            header.Children.Add(Header("FREQUENT"));
            _root.Children.Add(header);
            foreach (var r in recent)
                _root.Children.Add(Entry(PathUtil.DisplayName(r), r, "clock", null, tooltip: r, removable: true));
        }

        if (Labels.Any)
        {
            _root.Children.Add(Header("LABELS"));
            var used = Labels.Snapshot().Select(x => x.Label).ToHashSet();
            for (var i = 1; i < Labels.All.Length; i++)
            {
                if (!used.Contains(i)) continue;
                var index = i;
                var b = new Button { Classes = { "side" } };
                b.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Skin.Label(i), Margin = new Thickness(3, 0) },
                        new TextBlock { Text = Labels.All[i].Name, VerticalAlignment = VerticalAlignment.Center },
                    },
                };
                b.Click += (_, _) => ShowLabel?.Invoke(index);
                ToolTip.SetTip(b, $"Everything labelled {Labels.All[i].Name}");
                _root.Children.Add(b);
            }
        }
        SetCurrent(_current);
    }

    public void AddFavorite(string path)
    {
        var favs = AppSettings.Current.Favorites;
        if (favs.Contains(path, PathUtil.Comparer)) return;
        favs.Add(path);
        AppSettings.SaveSoon();
        Rebuild();
    }

    public void RemoveFavorite(string path)
    {
        AppSettings.Current.Favorites.RemoveAll(p => PathUtil.Same(p, path));
        AppSettings.SaveSoon();
        Rebuild();
    }

    public void MoveFavorite(string path, int delta)
    {
        var favs = AppSettings.Current.Favorites;
        var i = favs.FindIndex(p => PathUtil.Same(p, path));
        var j = i + delta;
        if (i < 0 || j < 0 || j >= favs.Count) return;
        (favs[i], favs[j]) = (favs[j], favs[i]);
        AppSettings.SaveSoon();
        Rebuild();
    }

    public static bool IsFavorite(string path) => AppSettings.Current.Favorites.Contains(path, PathUtil.Comparer);

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        Classes = { "label" },
        FontSize = 10.5,
        Margin = new Thickness(8, 14, 0, 4),
        Background = Brushes.Transparent,
    };

    private Button Entry(string name, string path, string glyph, Color? tint, string? tooltip = null, bool removable = false)
    {
        var icon = Glyphs.Icon(glyph, 16, tint is { } t ? new SolidColorBrush(t) : null);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(icon);
        var text = new TextBlock { Text = name, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var b = new Button { Classes = { "side" }, Content = row };
        if (removable)
        {
            // A small × that shows on hover removes the folder from the list.
            var remove = new Button { Content = Glyphs.Icon("close", 11), Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Opacity = 0 };
            remove.Classes.Add("icon");
            ToolTip.SetTip(remove, "Remove from Frequent");
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                Frecency.Forget(path);
                Rebuild();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            b.PointerEntered += (_, _) => remove.Opacity = 1;
            b.PointerExited += (_, _) => remove.Opacity = 0;
        }
        Wire(b, path);
        ToolTip.SetTip(b, tooltip ?? path);
        _entries.Add((b, path));
        return b;
    }

    /// <summary>
    /// Drive details come from a background thread: asking a disconnected network drive for its size can block for
    /// seconds, and the sidebar must never freeze the window.
    /// </summary>
    /// <param name="panel">Local, removable and disc drives (DRIVES).</param>
    /// <param name="networkPanel">Mapped network drives, shown under NETWORK below "Network".</param>
    private async Task FillDrivesAsync(Panel panel, Panel networkPanel, bool tiles)
    {
        var drives = DriveInfo.GetDrives();
        var lookups = drives.Select(d => Task.Run(() => FolderListing.DriveItem(d))).ToArray();
        await Task.WhenAny(Task.WhenAll(lookups), Task.Delay(2500));
        for (var i = 0; i < drives.Length; i++)
        {
            var item = lookups[i].IsCompletedSuccessfully ? lookups[i].Result : null;
            var target = drives[i].DriveType == DriveType.Network ? networkPanel : panel;
            target.Children.Add(tiles ? DriveTile(drives[i], item) : DriveEntry(drives[i], item));
        }
        SetCurrent(_current);
    }

    private static string DriveTip(DriveInfo d, FileItem? item)
    {
        var letter = d.Name.TrimEnd('\\', '/');
        var lines = new List<string> { item?.Name ?? letter };
        if (item?.Image is { } im) lines.Add($"Mounted {im.Kind} from {im.DisplayPath}");
        else if (item?.DriveDescription is { } desc) lines.Add(desc);
        if (item is { Size: > 0 })
        {
            var used = item.Size - item.DriveFree;
            lines.Add($"{Formatting.Size(used)} used of {Formatting.Size(item.Size)} ({used * 100.0 / item.Size:0}%)");
            lines.Add($"{Formatting.Size(item.DriveFree)} free");
        }
        if (item?.DriveDescription is null) try { if (d.IsReady) lines.Add($"{d.DriveFormat} · {d.DriveType switch { DriveType.Network => "network drive", DriveType.Removable => "removable", DriveType.CDRom => "disc", _ => "local disk" }}"); }
        catch { }
        return string.Join('\n', lines);
    }

    /// <summary>A compact square: the drive letter, filled from the bottom as far as the drive is full.</summary>
    private Button DriveTile(DriveInfo d, FileItem? item)
    {
        // Compact mode: a third smaller, and the fill alone tells how full the drive is (no percentage).
        var compact = AppSettings.Current.CompactRows;
        var size = compact ? 36.0 : 54.0;
        var letter = d.Name.TrimEnd('\\', '/', ':');
        var used = item is { Size: > 0 } ? Math.Clamp((double)(item.Size - item.DriveFree) / item.Size, 0, 1) : 0;
        var grid = new Grid { Width = size, Height = size, ClipToBounds = true };
        if (used > 0)
        {
            var brush = Skin.DriveBar(item!);
            // The fill: a soft wash of the drive-bar colour with a bright line along its top, like a liquid level.
            grid.Children.Add(new Border { Height = size * used, VerticalAlignment = VerticalAlignment.Bottom, Background = brush, Opacity = 0.28 });
            grid.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, Math.Max(0, size * used - 2)), Background = brush });
        }
        grid.Children.Add(new TextBlock
        {
            Text = letter,
            FontSize = compact ? 15 : 21,
            FontWeight = FontWeight.SemiBold,
            Foreground = Skin.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, compact ? 0 : 8),
        });
        if (!compact)
            grid.Children.Add(new TextBlock
        {
            Text = item is { Size: > 0 } ? $"{used * 100:0}%" : "—",
            FontSize = 10,
            Foreground = Skin.Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 5),
        });
        var badge = item?.Image is not null ? "disc" : d.DriveType == DriveType.Network ? "network" : d.DriveType == DriveType.CDRom ? "disc" : null;
        if (badge is not null)
            grid.Children.Add(new GlyphIcon
            {
                Glyph = badge,
                Width = compact ? 8 : 10,
                Height = compact ? 8 : 10,
                Brush = item?.Image is not null ? Skin.Accent : Skin.Faint,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = compact ? new Thickness(0, 3, 3, 0) : new Thickness(0, 4, 4, 0),
            });
        var b = new Button
        {
            Classes = { "side", "drivetile" },
            Content = grid,
            Padding = new Thickness(0),
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(compact ? 6 : 8),
            BorderThickness = new Thickness(1),
            BorderBrush = Skin.Separator,
            Background = Skin.TileBg,
        };
        Wire(b, d.RootDirectory.FullName);
        ToolTip.SetTip(b, DriveTip(d, item) + (item?.Image is not null ? "\nRight-click to unmount" : ""));
        ToolTip.SetShowDelay(b, 250);
        _entries.Add((b, d.RootDirectory.FullName));
        return b;
    }

    private Button DriveEntry(DriveInfo d, FileItem? item)
    {
        var letter = d.Name.TrimEnd('\\', '/');
        var network = d.DriveType == DriveType.Network;
        var label = item?.Name ?? letter;
        var stack = new StackPanel { Spacing = 3 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(Glyphs.Icon(network ? "network" : d.DriveType == DriveType.CDRom ? "disc" : "drive", 16, item?.Image is not null ? Skin.Accent : null));
        var name = new TextBlock { Text = label, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        top.Children.Add(name);
        if (item?.Image is { } image)
        {
            // Mounted images get an eject button right on the entry.
            var eject = new Button { Content = Glyphs.Icon("eject", 13), Width = 22, Height = 22, CornerRadius = new CornerRadius(5) };
            eject.Classes.Add("icon");
            ToolTip.SetTip(eject, $"Unmount {image.Kind} ({letter})");
            eject.Click += (_, e) =>
            {
                e.Handled = true;
                Unmount?.Invoke(d.RootDirectory.FullName);
            };
            Grid.SetColumn(eject, 2);
            top.Children.Add(eject);
        }
        stack.Children.Add(top);
        if (item is { Size: > 0 })
        {
            var used = Math.Clamp((double)(item.Size - item.DriveFree) / item.Size, 0.01, 1);
            var bar = new Grid { Margin = new Thickness(26, 0, 2, 0), Height = 3 };
            bar.ColumnDefinitions.Add(new ColumnDefinition(used, GridUnitType.Star));
            bar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(0.0001, 1 - used), GridUnitType.Star));
            var track = new Border { Background = Skin.Track, CornerRadius = new CornerRadius(1.5) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(new Border { Background = Skin.DriveBar(item), CornerRadius = new CornerRadius(1.5) });
            stack.Children.Add(bar);
            stack.Children.Add(new TextBlock
            {
                Text = item.Image is { } img ? $"{img.Kind} · {Path.GetFileName(img.DisplayPath)}" : Formatting.DriveSpace(item),
                FontSize = 10.5,
                Foreground = Skin.Faint,
                Margin = new Thickness(26, 0, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        var b = new Button { Classes = { "side" }, Content = stack, Padding = new Thickness(8, 5) };
        Wire(b, d.RootDirectory.FullName);
        ToolTip.SetTip(b, item?.Image is { } im
            ? $"{label}\nMounted from {im.DisplayPath}\n{Formatting.DriveSpace(item)}"
            : item?.DriveDescription is { } desc ? $"{label} — {desc}" : label);
        _entries.Add((b, d.RootDirectory.FullName));
        return b;
    }

    private void Wire(Button b, string path)
    {
        b.Click += (_, _) => Open?.Invoke(path, OpenTarget.Here);
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle) Open?.Invoke(path, OpenTarget.NewTab);
            else if (e.InitialPressMouseButton == MouseButton.Right) EntryMenu?.Invoke(path, b);
        };
        DragDrop.SetAllowDrop(b, true);
        b.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var paths = ItemViewBase.PathsFrom(e.DataTransfer);
            e.DragEffects = ItemViewBase.ChooseEffect(e.KeyModifiers, paths, path, e.DragEffects);
            b.Classes.Set("droptarget", e.DragEffects != DragDropEffects.None);
            e.Handled = true;
        });
        b.AddHandler(DragDrop.DragLeaveEvent, (_, _) => b.Classes.Set("droptarget", false));
        b.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            b.Classes.Set("droptarget", false);
            var paths = ItemViewBase.PathsFrom(e.DataTransfer);
            var effect = ItemViewBase.ChooseEffect(e.KeyModifiers, paths, path, e.DragEffects);
            e.DragEffects = effect;
            e.Handled = true;
            if (effect != DragDropEffects.None) Drop?.Invoke(path, paths, effect);
        });
    }
}
