using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pxdCommander.Core;
using pxdCommander.Ops;
using pxdCommander.Platform.Win32;

namespace pxdCommander.Views;

/// <summary>
/// The window: top bar, sidebar, one or two panes of tabs, preview pane and the operations queue, plus the overlay
/// layer for dialogs, Quick Look, the palette and toasts. Acts as the app controller for its panes.
/// </summary>
public sealed partial class MainWindow : Window, IAppHost
{
    private readonly Grid _main = new();
    private readonly Grid _panesGrid = new();
    private readonly PaneView[] _panes = new PaneView[2];
    private readonly GridSplitter _paneSplitter = new() { Background = Brushes.Transparent };
    private readonly Sidebar _sidebar = new();
    private readonly PreviewPane _preview = new();
    private readonly Border _previewHost;
    private readonly Panel _overlay = new();
    private readonly Panel _dialogLayer = new();
    private readonly Panel _toastLayer = new();
    private readonly OpsPanel _ops;
    private readonly JobQueue _queue;
    private readonly ToggleButton _sidebarToggle, _dualToggle, _previewToggle;
    private readonly Button _undoButton;
    private readonly bool _primary;
    private PaneView _activePane;
    private readonly List<TabSnapshot> _closedTabs = [];
    private static int _windowCount;

    public MainWindow(IReadOnlyList<string> startPaths, bool restoreSession)
    {
        _primary = Interlocked.Increment(ref _windowCount) == 1;
        Title = "pxd Commander";
        Icon = AppIcon.CreateWindowIcon();
        MinWidth = 760;
        MinHeight = 480;
        var s = AppSettings.Current;
        // The first window comes back where it was left (kept up to date as it moves, so it survives any exit).
        if (_primary) WindowChrome.RememberPlacement(this, () => AppSettings.Current.Window, p => AppSettings.Current.Window = p);
        else
        {
            Width = 1200;
            Height = 780;
        }

        _queue = new JobQueue(new JobUi(), OperatingSystem.IsWindows() ? RecycleBin.Recycle : null) { MaxConcurrentJobs = 3 };
        _queue.JobFinished += job => Dispatcher.UIThread.Post(() => OnJobFinished(job));
        _ops = new OpsPanel(_queue) { Margin = new Thickness(8, 0, 8, 8) };

        // ---- Top bar
        var mark = AppIcon.Mark(22);
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 18, 0),
            Children = { mark, new TextBlock { Text = "pxd Commander", Classes = { "neon" }, FontWeight = FontWeight.Bold, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center } },
        };
        var anywhere = new Button
        {
            Height = 32,
            MinWidth = 280,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 0),
            Background = Skin.Input,
            BorderBrush = Skin.InputBorder,
            Content = new DockPanel
            {
                Children =
                {
                    Kbd("Ctrl+K", Dock.Right),
                    Glyphs.Icon("search", 14, Skin.Faint),
                    new TextBlock { Text = "Go to a folder, find a file, run a command…", Foreground = Skin.Faint, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        anywhere.Click += (_, _) => ShowGoAnywhere("");

        _undoButton = TopButton("undo", "Undo (Ctrl+Z)", () => _ = UndoAsync());
        _sidebarToggle = TopToggle("sidebar", "Sidebar (Ctrl+B)", s.ShowSidebar, v => SetSidebar(v));
        _dualToggle = TopToggle("dual", "Two panes (Ctrl+Alt+D)", s.DualPane, v => SetDual(v));
        _previewToggle = TopToggle("preview", "Preview pane (Alt+P)", s.ShowPreview, v => SetPreview(v));
        var settings = TopButton("settings", "Settings (Ctrl+,)", () => _ = SettingsView.ShowAsync(ApplySettings));
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _undoButton, Separator(), _sidebarToggle, _dualToggle, _previewToggle, Separator(), settings },
        };

        // Our own title bar: the system one is gone, this row is the caption. Empty space drags the window (with snap),
        // double-click maximizes, and the caption buttons sit at the far right.
        var caption = WindowChrome.CaptionButtons(this);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Height = 48, Background = Brushes.Transparent };
        top.Children.Add(brand);
        Grid.SetColumn(anywhere, 1);
        top.Children.Add(anywhere);
        Grid.SetColumn(right, 2);
        top.Children.Add(right);
        Grid.SetColumn(caption, 3);
        top.Children.Add(caption);
        WindowChrome.MakeDraggable(this, top);

        // ---- Main area
        _main.ColumnDefinitions = new ColumnDefinitions($"{s.SidebarWidth},5,*,5,{s.PreviewWidth}");
        _main.Margin = new Thickness(0, 0, 8, 8);
        _main.Children.Add(_sidebar);
        var leftSplit = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(leftSplit, 1);
        _main.Children.Add(leftSplit);
        Grid.SetColumn(_panesGrid, 2);
        _main.Children.Add(_panesGrid);
        var rightSplit = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(rightSplit, 3);
        _main.Children.Add(rightSplit);
        _previewHost = new Border { Classes = { "pane" }, Child = _preview };
        Grid.SetColumn(_previewHost, 4);
        _main.Children.Add(_previewHost);
        leftSplit.DragCompleted += (_, _) => SaveColumnWidths();
        rightSplit.DragCompleted += (_, _) => SaveColumnWidths();

        _panes[0] = MakePane(0);
        _panes[1] = MakePane(1);
        _activePane = _panes[0];
        _panes[0].IsActivePane = true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(top);
        Grid.SetRow(_main, 1);
        root.Children.Add(_main);
        Grid.SetRow(_ops, 2);
        root.Children.Add(_ops);
        _overlay.Children.Add(_toastLayer);
        _overlay.Children.Add(_dialogLayer);
        Grid.SetRowSpan(_overlay, 3);
        root.Children.Add(_overlay);
        Content = root;
        Dialogs.Attach(_dialogLayer);
        Toasts.Attach(_toastLayer);

        _sidebar.Open += (path, target) => OpenFromSidebar(path, target);
        _sidebar.Drop += (target, paths, effect) => _ = StartTransfer(paths, target, effect == DragDropEffects.Move);
        _sidebar.ShowLabel += label => ShowLabelled(label);
        _sidebar.EntryMenu += (path, anchor) => ShowSidebarMenu(path, anchor);
        _sidebar.Unmount += root => _ = UnmountAsync(root);
        _sidebar.ConnectionOpen += (c, newTab) => OpenConnection(c, newTab);
        _sidebar.ConnectionMenu += (c, anchor) => ShowConnectionMenu(c, anchor);
        _sidebar.NewConnection += () => _ = EditConnectionAsync(null);
        _sidebar.NewConnectionMenu += anchor => ShowNewConnectionMenu(anchor);
        _sidebar.MapDrive += share => _ = MapDriveAsync(share);
        _sidebar.M365Open += p => OpenM365(p);
        _sidebar.M365Menu += (p, anchor) => ShowM365Menu(p, anchor);
        WirePrompts();
        _preview.OpenRequested += item =>
        {
            if (CurrentView is not { } v) return;
            // Folders and drives open here, not in Explorer.
            if (item.IsDirectory) _ = v.NavigateAsync(item.Path);
            else OpenItems(v, [item]);
        };
        _preview.UnmountRequested += root => _ = UnmountAsync(root);
        _preview.MountRequested += path => _ = MountAsync(path);

        // Restore the last session, or start in Home + Downloads.
        var restored = false;
        if (restoreSession && _primary && s.RestoreSession && s.Session is { Panes.Count: > 0 } session && startPaths.Count == 0)
        {
            for (var i = 0; i < Math.Min(2, session.Panes.Count); i++)
                _panes[i].Restore(session.Panes[i]);
            restored = _panes[0].Tabs.Count > 0;
            if (session.ActivePane == 1 && _panes[1].Tabs.Count > 0) SetActive(_panes[1]);
        }
        if (!restored)
        {
            if (startPaths.Count > 0)
                foreach (var p in startPaths) _panes[0].AddTab(p);
            else
                _panes[0].AddTab(PathUtil.Home);
        }
        else
        {
            foreach (var p in startPaths) _panes[0].AddTab(p);
        }
        if (_panes[1].Tabs.Count == 0) _panes[1].AddTab(PathUtil.Downloads);

        SetSidebar(s.ShowSidebar);
        SetPreview(s.ShowPreview);
        SetDual(s.DualPane);

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        UndoStack.Changed += UpdateUndoButton;
        UpdateUndoButton();
        BuildCommands();
        Closing += (_, _) => SaveSession(closing: true);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => CurrentView?.FocusList(), DispatcherPriority.Background);
        Opened += (_, _) => _ = UpdatePrompt.CheckOnStartAsync(this);
        Activated += (_, _) => _activePane.Current?.InvalidateRows();
        AccentTheme.Changed += OnAccentChanged;
        Closed += (_, _) => AccentTheme.Changed -= OnAccentChanged;
        FolderView.FolderSizesSwitched += RefreshAllFolderSizes;
        Closed += (_, _) => FolderView.FolderSizesSwitched -= RefreshAllFolderSizes;
    }

    private static Border Kbd(string text, Dock dock)
    {
        var b = new Border
        {
            Background = Skin.Line,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = Skin.Muted },
        };
        DockPanel.SetDock(b, dock);
        return b;
    }

    private static Border Separator() => new() { Width = 1, Height = 18, Background = Skin.Separator, Margin = new Thickness(6, 0) };

    private static Button TopButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = Glyphs.Icon(glyph, 17) };
        b.Classes.Add("icon");
        b.Width = b.Height = 32;
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static ToggleButton TopToggle(string glyph, string tip, bool value, Action<bool> changed)
    {
        var b = new ToggleButton { Content = Glyphs.Icon(glyph, 17), IsChecked = value };
        b.Classes.Add("icon");
        b.Width = b.Height = 32;
        ToolTip.SetTip(b, tip);
        b.IsCheckedChanged += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    private PaneView MakePane(int index)
    {
        var pane = new PaneView(this, index);
        pane.Activated += SetActive;
        pane.CurrentChanged += p =>
        {
            if (p == _activePane) OnActiveViewChanged();
            SaveSession();
        };
        pane.PaneMenu += (p, anchor) => ShowPaneMenu(p, anchor);
        pane.TabMenu += (p, tab, anchor) => ShowTabMenu(p, tab, anchor);
        pane.TabClosed += tab =>
        {
            _closedTabs.Add(tab.Snapshot());
            if (_closedTabs.Count > 25) _closedTabs.RemoveAt(0);
        };
        pane.LastTabClosing = p =>
        {
            if (!AppSettings.Current.DualPane) return false;
            ShowOnlyPane(p == _panes[0] ? _panes[1] : _panes[0]);
            return true;
        };
        return pane;
    }

    /// <summary>Switches to one pane, keeping <paramref name="keep"/> (whichever side it was on).</summary>
    private void ShowOnlyPane(PaneView keep)
    {
        if (keep == _panes[1]) (_panes[0], _panes[1]) = (_panes[1], _panes[0]);
        SetActive(_panes[0]);
        SetDual(false);
    }

    // ---- Layout toggles ------------------------------------------------------------------------------------------------

    private void SetDual(bool dual)
    {
        AppSettings.Current.DualPane = dual;
        AppSettings.SaveSoon();
        _dualToggle.IsChecked = dual;
        _panesGrid.Children.Clear();
        _panesGrid.ColumnDefinitions.Clear();
        _panesGrid.RowDefinitions.Clear();
        if (!dual)
        {
            if (_activePane == _panes[1]) SetActive(_panes[0]);
            _panesGrid.Children.Add(_panes[0]);
            return;
        }
        var stacked = AppSettings.Current.StackedPanes;
        if (stacked) _panesGrid.RowDefinitions = new RowDefinitions("*,6,*");
        else _panesGrid.ColumnDefinitions = new ColumnDefinitions("*,6,*");
        _paneSplitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        _panesGrid.Children.Add(_panes[0]);
        _panesGrid.Children.Add(_paneSplitter);
        _panesGrid.Children.Add(_panes[1]);
        Grid.SetColumn(_paneSplitter, stacked ? 0 : 1);
        Grid.SetRow(_paneSplitter, stacked ? 1 : 0);
        Grid.SetColumn(_panes[1], stacked ? 0 : 2);
        Grid.SetRow(_panes[1], stacked ? 2 : 0);
        Grid.SetColumn(_panes[0], 0);
        Grid.SetRow(_panes[0], 0);
    }

    private void SetSidebar(bool show)
    {
        AppSettings.Current.ShowSidebar = show;
        AppSettings.SaveSoon();
        _sidebarToggle.IsChecked = show;
        _sidebar.IsVisible = show;
        _main.ColumnDefinitions[0].Width = show ? new GridLength(AppSettings.Current.SidebarWidth) : new GridLength(0);
        _main.ColumnDefinitions[1].Width = show ? new GridLength(5) : new GridLength(8);
    }

    private void SetPreview(bool show)
    {
        AppSettings.Current.ShowPreview = show;
        AppSettings.SaveSoon();
        _previewToggle.IsChecked = show;
        UpdatePreviewVisibility();
    }

    private void SetAutoPreview(bool auto)
    {
        AppSettings.Current.AutoPreview = auto;
        SetPreview(true);
    }

    /// <summary>
    /// What auto-preview opens the pane for: anything it can actually show — images, videos, audio, documents (PDF,
    /// Office), archives (their file list) and every kind of text (code, logs, .txt, .gitignore, config files). Not
    /// folders, programs or unknown binary files.
    /// </summary>
    public static bool WorthPreviewing(FileItem? item) =>
        item is { Kind: ItemKind.File } &&
        (FileKinds.IsTextual(item.Extension) ||
         FileKinds.Of(item) is FileKind.Image or FileKind.Video or FileKind.Audio or FileKind.Document or FileKind.Archive or FileKind.Text or FileKind.Code);

    /// <summary>Shows the preview column when it's on (and, with auto-preview, only for something worth previewing).</summary>
    private void UpdatePreviewVisibility()
    {
        var s = AppSettings.Current;
        var item = CurrentView?.FocusedItem;
        var show = s.ShowPreview && (!s.AutoPreview || WorthPreviewing(item));
        if (_previewHost.IsVisible != show)
        {
            if (!show && _previewHost.Bounds.Width > 60) s.PreviewWidth = Math.Round(_main.ColumnDefinitions[4].ActualWidth);
            _previewHost.IsVisible = show;
            _preview.IsVisible = show;
            _main.ColumnDefinitions[3].Width = new GridLength(show ? 5 : 0);
            _main.ColumnDefinitions[4].Width = show ? new GridLength(s.PreviewWidth) : new GridLength(0);
        }
        if (show) _preview.Show(item);
    }

    private void SaveColumnWidths()
    {
        if (_sidebar.IsVisible) AppSettings.Current.SidebarWidth = Math.Round(_main.ColumnDefinitions[0].ActualWidth);
        if (_previewHost.IsVisible) AppSettings.Current.PreviewWidth = Math.Round(_main.ColumnDefinitions[4].ActualWidth);
        AppSettings.SaveSoon();
    }

    private void ApplySettings()
    {
        foreach (var p in _panes) p.ApplySettings();
        RefreshAllFolderSizes();
        _sidebar.Rebuild();
        _preview.Reload();
    }

    /// <summary>Every open tab measures or clears its folder sizes to match the setting.</summary>
    private void RefreshAllFolderSizes()
    {
        foreach (var p in _panes)
            foreach (var t in p.Tabs) t.RefreshFolderSizes();
    }

    // ---- Panes and activation ------------------------------------------------------------------------------------------

    public FolderView? CurrentView => _activePane.Current;

    private PaneView OtherPane => _activePane == _panes[0] ? _panes[1] : _panes[0];

    /// <summary>The folder view in the other pane, when two panes are showing.</summary>
    private FolderView? OtherView => AppSettings.Current.DualPane ? OtherPane.Current : null;

    private void SetActive(PaneView pane)
    {
        if (_activePane == pane) return;
        _activePane.IsActivePane = false;
        _activePane = pane;
        pane.IsActivePane = true;
        OnActiveViewChanged();
    }

    private void OnActiveViewChanged()
    {
        var view = CurrentView;
        Title = view is null ? "pxd Commander" : $"{view.Title} — pxd Commander";
        _sidebar.SetCurrent(view?.CurrentPath);
        UpdatePreviewVisibility();
    }

    public bool IsActivePaneView(FolderView view) => _activePane.Current == view && IsActive;

    public void ViewActivated(FolderView view)
    {
        var pane = _panes.FirstOrDefault(p => p.Tabs.Contains(view));
        if (pane is not null) SetActive(pane);
    }

    public void SelectionChanged(FolderView view)
    {
        if (view == CurrentView) UpdatePreviewVisibility();
    }

    public void PathChanged(FolderView view)
    {
        foreach (var p in _panes) p.UpdateHeader(view);
        if (view == CurrentView) OnActiveViewChanged();
        SaveSession();
    }

    private void FocusPane(PaneView pane)
    {
        if (!AppSettings.Current.DualPane && pane == _panes[1]) return;
        SetActive(pane);
        pane.Current?.FocusList();
    }

    public void OpenInNewTab(string path, bool otherPane)
    {
        var pane = otherPane && AppSettings.Current.DualPane ? OtherPane : _activePane;
        var at = pane.Current is { } cur ? pane.Tabs.ToList().IndexOf(cur) + 1 : (int?)null;
        pane.AddTab(path, at: at);
        if (pane != _activePane) SetActive(pane);
    }

    private void OpenFromSidebar(string path, OpenTarget target)
    {
        switch (target)
        {
            case OpenTarget.NewTab:
                OpenInNewTab(path, false);
                break;
            case OpenTarget.OtherPane:
                if (!AppSettings.Current.DualPane) SetDual(true);
                _ = OtherPane.Current?.NavigateAsync(path);
                break;
            default:
                if (CurrentView is { } v)
                {
                    _ = v.NavigateAsync(path);
                    v.FocusList();
                }
                break;
        }
    }

    // ---- Quick Look ----------------------------------------------------------------------------------------------------

    public void QuickLook(FolderView view)
    {
        if (view.ActiveListing is not { } listing || view.ActiveSelection is not { } sel) return;
        var item = view.FocusedItem ?? sel.Caret;
        if (item is null) return;
        new QuickLook(_dialogLayer, listing.Items, item,
            i =>
            {
                sel.SelectOnly(i);
                view.ActiveView?.BringIntoView(i);
            },
            i => OpenItems(view, [i])).Show();
    }

    // ---- Session -------------------------------------------------------------------------------------------------------

    private void SaveSession(bool closing = false)
    {
        if (!_primary) return;
        var s = AppSettings.Current;
        s.Session = new SessionSnapshot
        {
            Panes = _panes.Select(p => p.Snapshot()).ToList(),
            ActivePane = Array.IndexOf(_panes, _activePane),
        };
        if (closing)
        {
            SaveColumnWidths();
            foreach (var p in _panes)
                foreach (var t in p.Tabs) t.Dispose();
            AppSettings.SaveNow();
        }
        else AppSettings.SaveSoon();
    }

    private void UpdateUndoButton()
    {
        _undoButton.IsEnabled = UndoStack.NextUndo is not null;
        ToolTip.SetTip(_undoButton, UndoStack.NextUndo is { } u ? $"Undo {u} (Ctrl+Z)" : "Nothing to undo");
    }

    private TopLevel Top => this;

    private IntPtr Hwnd => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
}
