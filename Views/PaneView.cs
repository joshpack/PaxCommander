using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>One side of the window: a strip of tabs, the view-mode switch and the active tab's folder view.</summary>
public sealed class PaneView : Border
{
    private readonly IAppHost _app;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _tabScroll;
    private readonly Grid _host = new();
    private readonly List<FolderView> _tabs = [];
    private readonly List<Border> _headers = [];
    private readonly ToggleButton _details, _columns, _tiles, _terminalToggle;
    private int _active = -1;

    public PaneView(IAppHost app, int index)
    {
        _app = app;
        Index = index;
        Classes.Add("pane");

        _tabScroll = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var add = new Button { Content = Glyphs.Icon("plus", 14), Width = 26, Height = 26 };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, "New tab (Ctrl+T)");
        add.Click += (_, _) => AddTab(Current?.ActiveFolder ?? PathUtil.Home);

        _details = ModeButton("details", "Details (Ctrl+Shift+1)", ViewMode.Details);
        _columns = ModeButton("columns", "Columns (Ctrl+Shift+2)", ViewMode.Columns);
        _tiles = ModeButton("tiles", "Thumbnails (Ctrl+Shift+3) · Ctrl+wheel to resize", ViewMode.Tiles);
        var seg = new Border
        {
            Background = Skin.Input,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { _details, _columns, _tiles } },
        };
        // Terminal for this tab's folder: SSH on a server, PowerShell on this PC.
        _terminalToggle = new ToggleButton { Content = Glyphs.Icon("terminal", 15), Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0) };
        _terminalToggle.Classes.Add("icon");
        ToolTip.SetTip(_terminalToggle, "Terminal here (Ctrl+`)");
        _terminalToggle.Click += (_, _) =>
        {
            Current?.ToggleTerminal();
            SyncMode();
        };
        var more = new Button { Content = Glyphs.Icon("more", 16), Width = 28, Height = 28 };
        more.Classes.Add("icon");
        ToolTip.SetTip(more, "Sort, hidden files, folder sizes…");
        more.Click += (_, _) => PaneMenu?.Invoke(this, more);

        var strip = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(6, 6, 6, 0),
            Height = 30,
        };
        strip.Children.Add(_tabScroll);
        Grid.SetColumn(add, 1);
        strip.Children.Add(add);
        Grid.SetColumn(seg, 3);
        strip.Children.Add(seg);
        Grid.SetColumn(_terminalToggle, 4);
        strip.Children.Add(_terminalToggle);
        Grid.SetColumn(more, 5);
        more.Margin = new Thickness(4, 0, 0, 0);
        strip.Children.Add(more);
        _tabScroll.MaxWidth = 10_000;
        strip.SizeChanged += (_, e) => _tabScroll.MaxWidth = Math.Max(80, e.NewSize.Width - 194);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(strip);
        Grid.SetRow(_host, 1);
        root.Children.Add(_host);
        Child = root;

        AddHandler(GotFocusEvent, (_, _) => Activated?.Invoke(this), handledEventsToo: true);
        PointerPressed += (_, _) => Activated?.Invoke(this);
    }

    public int Index { get; }
    public IReadOnlyList<FolderView> Tabs => _tabs;
    public FolderView? Current => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    public event Action<PaneView>? Activated;
    public event Action<PaneView>? CurrentChanged;
    public event Action<PaneView, Control>? PaneMenu;
    public event Action<PaneView, FolderView, Control>? TabMenu;
    public event Action<FolderView>? TabClosed;
    /// <summary>Closing the last tab: return true if the window took care of it (hid the pane).</summary>
    public Func<PaneView, bool>? LastTabClosing;

    public bool IsActivePane
    {
        get => Classes.Contains("active");
        set
        {
            if (value == IsActivePane) return;
            Classes.Set("active", value);
            Current?.InvalidateRows();
        }
    }

    private ToggleButton ModeButton(string glyph, string tip, ViewMode mode)
    {
        var b = new ToggleButton { Content = Glyphs.Icon(glyph, 14) };
        b.Classes.Add("seg");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            Current?.SetMode(mode);
            SyncMode();
        };
        return b;
    }

    public void SyncMode()
    {
        var m = Current?.Mode ?? ViewMode.Details;
        _details.IsChecked = m == ViewMode.Details;
        _columns.IsChecked = m == ViewMode.Columns;
        _tiles.IsChecked = m == ViewMode.Tiles;
        _terminalToggle.IsChecked = Current?.HasTerminal == true;
        _terminalToggle.IsEnabled = Current is { } v && (v.HasTerminal || v.CanShowTerminal);
    }

    public FolderView AddTab(string path, ViewMode? mode = null, SortSpec? sort = null, bool activate = true, int? at = null)
    {
        var view = new FolderView(_app, path, mode ?? AppSettings.Current.DefaultView,
            sort ?? new SortSpec { FoldersFirst = AppSettings.Current.FoldersFirst }) { IsVisible = false };
        var index = Math.Clamp(at ?? _tabs.Count, 0, _tabs.Count);
        _tabs.Insert(index, view);
        var header = MakeHeader(view);
        _headers.Insert(index, header);
        _tabStrip.Children.Insert(index, header);
        _host.Children.Add(view);
        view.StateChanged += () =>
        {
            UpdateHeader(view);
            if (view == Current) SyncMode();
        };
        if (activate || _active < 0) Activate(index);
        else if (index <= _active) _active++;
        UpdateHeader(view);
        return view;
    }

    private Border MakeHeader(FolderView view)
    {
        var icon = Glyphs.Icon("folder", 14);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 0, 4, 0), FontSize = 12.5 };
        var close = new Button { Content = Glyphs.Icon("close", 11), Width = 20, Height = 20, CornerRadius = new CornerRadius(5) };
        close.Classes.Add("icon");
        close.Click += (_, _) => CloseTab(view);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, title, close } };
        var header = new Border { Child = row, Tag = view };
        header.Classes.Add("tab");
        header.PointerPressed += (_, e) =>
        {
            var kind = e.GetCurrentPoint(header).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.MiddleButtonPressed)
            {
                CloseTab(view);
                e.Handled = true;
            }
            else if (kind == PointerUpdateKind.LeftButtonPressed)
            {
                Activate(_tabs.IndexOf(view));
                view.FocusList();
            }
        };
        header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right) TabMenu?.Invoke(this, view, header);
        };

        // Dragging files over a tab switches to it after a moment; dropping on it moves/copies into its folder.
        DispatcherTimer? hover = null;
        DragDrop.SetAllowDrop(header, true);
        header.AddHandler(DragDrop.DragEnterEvent, (_, _) =>
        {
            hover?.Stop();
            hover = new DispatcherTimer(TimeSpan.FromMilliseconds(650), DispatcherPriority.Input, (_, _) =>
            {
                hover?.Stop();
                Activate(_tabs.IndexOf(view));
            });
            hover.Start();
        });
        header.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            hover?.Stop();
            header.Classes.Set("droptarget", false);
        });
        header.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var paths = ItemViewBase.PathsFrom(e.DataTransfer);
            e.DragEffects = PathUtil.IsVirtual(view.ActiveFolder) ? DragDropEffects.None
                : ItemViewBase.ChooseEffect(e.KeyModifiers, paths, view.ActiveFolder, e.DragEffects);
            header.Classes.Set("droptarget", e.DragEffects != DragDropEffects.None);
            e.Handled = true;
        });
        header.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            hover?.Stop();
            header.Classes.Set("droptarget", false);
            var paths = ItemViewBase.PathsFrom(e.DataTransfer);
            var effect = ItemViewBase.ChooseEffect(e.KeyModifiers, paths, view.ActiveFolder, e.DragEffects);
            e.DragEffects = effect;
            e.Handled = true;
            if (effect != DragDropEffects.None && !PathUtil.IsVirtual(view.ActiveFolder))
                _ = _app.DropAsync(view, view.ActiveFolder, paths, effect);
        });
        return header;
    }

    public void UpdateHeader(FolderView view)
    {
        var i = _tabs.IndexOf(view);
        if (i < 0) return;
        var header = _headers[i];
        var row = (StackPanel)header.Child!;
        var icon = (GlyphIcon)row.Children[0];
        var title = (TextBlock)row.Children[1];
        title.Text = view.Title;
        icon.Glyph = view.IsSearch ? "search" : PathUtil.IsVirtual(view.CurrentPath) ? "desktop" : PathUtil.IsRoot(view.CurrentPath) ? "drive" : "folder";
        var selected = i == _active;
        header.Classes.Set("selected", selected);
        icon.Brush = selected ? Skin.Accent : Skin.Faint;
        title.Foreground = selected ? Skin.Text : Skin.Muted;
        ToolTip.SetTip(header, view.IsSearch ? view.Title : PathUtil.IsVirtual(view.CurrentPath) ? "This PC" : view.CurrentPath);
        // Close buttons appear on the selected tab only (or when there are few tabs) to keep the strip calm.
        ((Button)row.Children[2]).Opacity = selected || _tabs.Count <= 3 ? 1 : 0.35;
    }

    public void Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var old = Current;
        _active = index;
        for (var i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].IsVisible = i == index;
            UpdateHeader(_tabs[i]);
        }
        SyncMode();
        BringHeaderIntoView(_headers[index]);
        if (old != Current) CurrentChanged?.Invoke(this);
    }

    private void BringHeaderIntoView(Border header) => Dispatcher.UIThread.Post(() =>
    {
        if (header.TranslatePoint(new Point(0, 0), _tabStrip) is not { } p) return;
        var x = _tabScroll.Offset.X;
        if (p.X < x) _tabScroll.Offset = new Vector(p.X, 0);
        else if (p.X + header.Bounds.Width > x + _tabScroll.Viewport.Width)
            _tabScroll.Offset = new Vector(p.X + header.Bounds.Width - _tabScroll.Viewport.Width, 0);
    }, DispatcherPriority.Background);

    public void Cycle(int delta)
    {
        if (_tabs.Count < 2) return;
        Activate(((_active + delta) % _tabs.Count + _tabs.Count) % _tabs.Count);
        Current?.FocusList();
    }

    public void CloseTab(FolderView view)
    {
        var i = _tabs.IndexOf(view);
        if (i < 0) return;
        TabClosed?.Invoke(view);
        if (_tabs.Count == 1)
        {
            // Closing the last tab hides this pane in two-pane mode; otherwise the tab goes home.
            if (LastTabClosing?.Invoke(this) == true) return;
            _ = view.NavigateAsync(PathUtil.Home);
            return;
        }
        _tabs.RemoveAt(i);
        _tabStrip.Children.Remove(_headers[i]);
        _headers.RemoveAt(i);
        _host.Children.Remove(view);
        view.Dispose();
        var next = i < _active || _active >= _tabs.Count ? _active - 1 : _active;
        if (i == _active) next = Math.Min(i, _tabs.Count - 1);
        _active = -1;
        Activate(Math.Max(0, next));
        Current?.FocusList();
    }

    public void CloseOthers(FolderView keep)
    {
        foreach (var t in _tabs.Where(t => t != keep).ToList()) CloseTab(t);
    }

    public void MoveTab(FolderView view, int delta)
    {
        var i = _tabs.IndexOf(view);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _tabs.Count) return;
        (_tabs[i], _tabs[j]) = (_tabs[j], _tabs[i]);
        (_headers[i], _headers[j]) = (_headers[j], _headers[i]);
        _tabStrip.Children.Clear();
        foreach (var h in _headers) _tabStrip.Children.Add(h);
        if (_active == i) _active = j;
        else if (_active == j) _active = i;
        foreach (var t in _tabs) UpdateHeader(t);
    }

    public PaneSnapshot Snapshot() => new() { Tabs = _tabs.Select(t => t.Snapshot()).ToList(), ActiveTab = Math.Max(0, _active) };

    public void Restore(PaneSnapshot snapshot)
    {
        foreach (var t in snapshot.Tabs)
        {
            var path = t.Path;
            // A folder on a drive that isn't there any more falls back to home rather than an error tab.
            if (!PathUtil.IsVirtual(path) && !SafeExists(path)) path = PathUtil.Home;
            AddTab(path, t.View, new SortSpec { Key = t.Sort, Descending = t.Descending, FoldersFirst = AppSettings.Current.FoldersFirst }, activate: false);
        }
        if (_tabs.Count > 0) Activate(Math.Clamp(snapshot.ActiveTab, 0, _tabs.Count - 1));
    }

    private static bool SafeExists(string path)
    {
        try
        {
            // Network roots can hang when the server is gone; don't hold up start-up for them, and don't throw away
            // a tab just because the share answered slowly.
            var t = Task.Run(() => Directory.Exists(path));
            return !t.Wait(TimeSpan.FromSeconds(1.5)) || t.Result;
        }
        catch
        {
            return false;
        }
    }

    public void ApplySettings()
    {
        foreach (var t in _tabs) t.ApplySettings();
    }
}
