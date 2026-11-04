using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;
using PaxCommander.Search;

namespace PaxCommander.Views;

/// <summary>What a folder tab needs from the main window.</summary>
public interface IAppHost
{
    bool IsActivePaneView(FolderView view);
    void ViewActivated(FolderView view);
    void SelectionChanged(FolderView view);
    void PathChanged(FolderView view);
    void OpenItems(FolderView view, IReadOnlyList<FileItem> items);
    void OpenInNewTab(string path, bool otherPane);
    void ShowContextMenu(FolderView view, Control anchor, FileItem? item, Point position, bool shellMenu);
    void QuickLook(FolderView view);
    Task<bool> RenameAsync(FolderView view, FileItem item, string newName);
    Task DropAsync(FolderView view, string target, IReadOnlyList<string> paths, DragDropEffects effect);
    void OpenExternalTerminal(Core.Remote.Connection connection);
}

public sealed record HistoryEntry(string Path, string? Selected);

/// <summary>
/// One tab: navigation bar (back/forward/up, path bar, filter), the item view in the chosen mode, and a status line.
/// Owns the folder listing, history, recursive search and background folder sizing for that tab.
/// </summary>
public sealed class FolderView : UserControl, IItemViewHost
{
    private readonly IAppHost _app;
    private readonly Button _backBtn, _forwardBtn, _upBtn, _refreshBtn;
    private readonly AddressBar _address = new();
    private readonly TextBox _filter;
    private readonly Grid _content = new();
    private readonly TextBlock _message;
    private readonly ProgressBar _loading;
    private readonly TextBlock _statusLeft, _statusRight;

    private DetailsList? _details;
    private TileGrid? _tiles;
    private ColumnsBrowser? _columns;

    private FolderListing? _listing;
    private SelectionModel? _selection;
    private readonly List<HistoryEntry> _back = [];
    private readonly List<HistoryEntry> _forward = [];
    private CancellationTokenSource? _sizeCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _gitCts;
    private string? _pendingSelect;
    private (string Path, ViewMode Mode)? _beforeSearch;
    private DispatcherTimer? _filterTimer;
    private GitInfo.Repo? _repo;
    private string _freeText = "";
    private SearchProgress? _searchProgress;
    private string? _searchText;

    // Terminal docked under the files: SSH on a server, PowerShell on this PC.
    private readonly RowDefinition _terminalRow = new(GridLength.Auto);
    private readonly GridSplitter _terminalSplitter = new() { Height = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, IsVisible = false };
    private readonly Border _terminalHost = new();
    private TerminalPane? _terminal;
    private double _terminalHeight = 260;
    private readonly HashSet<string> _terminalDismissed = [];
    private bool _localTerminalWanted;
    private readonly RowDefinition _contentRow = new(GridLength.Star);
    private Control _contentHost = null!;
    private bool _terminalMaximized;

    public FolderView(IAppHost app, string path, ViewMode mode, SortSpec sort)
    {
        _app = app;
        CurrentPath = path;
        Mode = mode;
        Sort = sort;

        _backBtn = NavButton("back", "Back (Alt+Left)", GoBack);
        _forwardBtn = NavButton("forward", "Forward (Alt+Right)", GoForward);
        _upBtn = NavButton("up", "Up one level (Alt+Up)", GoUp);
        _refreshBtn = NavButton("refresh", "Refresh (F5)", () => _ = RefreshAsync());

        _address.NavigateRequested += text => _ = NavigateTextAsync(text);
        _address.DropOnCrumb += (target, paths, effect) => _ = _app.DropAsync(this, target, paths, effect);
        _address.EditFinished += () =>
        {
            if (!IsKeyboardFocusWithin || FocusManagerElement() is null) FocusList();
        };

        _filter = new TextBox
        {
            Width = 200,
            Height = 32,
            PlaceholderText = "Filter · Enter = search",
            InnerLeftContent = new GlyphIcon { Glyph = "search", Width = 14, Height = 14, Margin = new Thickness(9, 0, 0, 0), Brush = Skin.Faint },
            Transitions = [new Avalonia.Animation.DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(140) }],
        };
        ToolTip.SetTip(_filter, "Type to filter this folder (Ctrl+F). Press Enter to search all subfolders — try ext:pdf, size:>100mb, modified:7d");
        _filter.TextChanged += (_, _) =>
        {
            QueueFilter();
            UpdateFilterWidth();
        };
        _filter.KeyDown += OnFilterKeyDown;
        _filter.GotFocus += (_, _) => UpdateFilterWidth();
        _filter.LostFocus += (_, _) => UpdateFilterWidth();

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { _backBtn, _forwardBtn, _upBtn, _refreshBtn } };
        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(6, 6, 6, 4),
        };
        bar.SizeChanged += (_, e) =>
        {
            _narrow = e.NewSize.Width < 680;
            UpdateFilterWidth();
        };
        bar.Children.Add(nav);
        Grid.SetColumn(_address, 1);
        _address.Margin = new Thickness(6, 0);
        bar.Children.Add(_address);
        Grid.SetColumn(_filter, 2);
        bar.Children.Add(_filter);

        _message = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 0),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Skin.Faint,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        _loading = new ProgressBar
        {
            IsIndeterminate = true,
            VerticalAlignment = VerticalAlignment.Top,
            Height = 2,
            MinHeight = 2,
            IsVisible = false,
            Margin = new Thickness(8, 0),
        };
        var contentHost = new Grid();
        contentHost.Children.Add(_content);
        contentHost.Children.Add(_message);
        contentHost.Children.Add(_loading);

        _statusLeft = new TextBlock { FontSize = 11.5, Foreground = Skin.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _statusRight = new TextBlock { FontSize = 11.5, Foreground = Skin.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var status = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Height = 24,
            Margin = new Thickness(12, 0, 12, 2),
        };
        status.Children.Add(_statusLeft);
        Grid.SetColumn(_statusRight, 1);
        status.Children.Add(_statusRight);

        var root = new Grid { RowDefinitions = [new(GridLength.Auto), _contentRow, new(GridLength.Auto), _terminalRow, new(GridLength.Auto)] };
        root.Children.Add(bar);
        _contentHost = contentHost;
        Grid.SetRow(contentHost, 1);
        root.Children.Add(contentHost);
        Grid.SetRow(_terminalSplitter, 2);
        root.Children.Add(_terminalSplitter);
        Grid.SetRow(_terminalHost, 3);
        root.Children.Add(_terminalHost);
        Grid.SetRow(status, 4);
        root.Children.Add(status);
        Content = root;

        // Mouse back/forward buttons work anywhere in the tab.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed) { GoBack(); e.Handled = true; }
            else if (kind == PointerUpdateKind.XButton2Pressed) { GoForward(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);

        BuildView();
        _ = LoadAsync(path, null, recordVisit: false);
    }

    private bool _narrow;

    /// <summary>In a narrow pane the filter shrinks to its icon until it's used, leaving room for the path.</summary>
    private void UpdateFilterWidth()
    {
        var compact = _narrow && !_filter.IsKeyboardFocusWithin && string.IsNullOrEmpty(_filter.Text);
        _filter.MinWidth = 0;
        _filter.Width = compact ? 34 : _narrow ? 170 : 200;
        _filter.PlaceholderText = compact ? "" : "Filter · Enter = search";
    }

    private static Button NavButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = Glyphs.Icon(glyph, 16) };
        b.Classes.Add("icon");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private IInputElement? FocusManagerElement() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    // ---- Public state --------------------------------------------------------------------------------------------------

    public string CurrentPath { get; private set; }
    public ViewMode Mode { get; private set; }
    public SortSpec Sort { get; private set; }
    public bool IsSearch => _listing?.IsSearch == true;
    public string? SearchText => IsSearch ? _searchText : null;
    public bool CanGoBack => _back.Count > 0 || IsSearch;
    public bool CanGoForward => _forward.Count > 0;

    public event Action? StateChanged;

    public FolderListing? ActiveListing => Mode == ViewMode.Columns && !IsSearch ? _columns?.Active?.Listing : _listing;
    public SelectionModel? ActiveSelection => Mode == ViewMode.Columns && !IsSearch ? _columns?.Active?.Selection : _selection;
    public ItemViewBase? ActiveView => Mode == ViewMode.Columns && !IsSearch ? _columns?.Active?.View : Mode == ViewMode.Tiles ? _tiles : _details;

    /// <summary>The folder that new items, pastes and drops go into.</summary>
    public string ActiveFolder => ActiveListing is { IsSearch: false } l ? l.Path : CurrentPath;

    public IReadOnlyList<FileItem> SelectedItems => ActiveSelection?.Ordered() ?? [];

    /// <summary>The item the preview pane and status line describe.</summary>
    public FileItem? FocusedItem
    {
        get
        {
            var s = ActiveSelection;
            if (s is null) return null;
            if (s.Caret is { IsSelected: true } c) return c;
            return s.Count > 0 ? s.Set.First() : null;
        }
    }

    public string Title => IsSearch ? $"“{_searchText}”" : PathUtil.DisplayName(CurrentPath);

    public TabSnapshot Snapshot() => new()
    {
        Path = _beforeSearch?.Path ?? CurrentPath,
        View = _beforeSearch?.Mode ?? Mode,
        Sort = Sort.Key,
        Descending = Sort.Descending,
    };

    // ---- Views ---------------------------------------------------------------------------------------------------------

    private void BuildView()
    {
        _content.Children.Clear();
        if (Mode == ViewMode.Columns && !IsSearch)
        {
            _columns ??= CreateColumns();
            _content.Children.Add(_columns);
            return;
        }
        ItemViewBase view = Mode == ViewMode.Tiles ? (_tiles ??= CreateTiles()) : (_details ??= CreateDetails());
        _content.Children.Add(view);
        if (_listing is not null && _selection is not null && view.Listing != _listing)
            view.Attach(_listing, _selection);
        if (view is DetailsList d) d.ShowLocationColumn(IsSearch);
    }

    private DetailsList CreateDetails()
    {
        var d = new DetailsList { Host = this };
        d.SortRequested += key => SetSort(key);
        return d;
    }

    private TileGrid CreateTiles() => new() { Host = this };

    private ColumnsBrowser CreateColumns()
    {
        var c = new ColumnsBrowser(this, Sort);
        c.DeepestChanged += path =>
        {
            CurrentPath = path;
            UpdateChrome();
            _app.PathChanged(this);
        };
        return c;
    }

    public void SetMode(ViewMode mode)
    {
        if (mode == Mode) return;
        var focusWasInside = IsKeyboardFocusWithin;
        var selectName = FocusedItem?.Name;
        var from = Mode;
        Mode = mode;
        if (IsSearch)
        {
            // Search results always use a flat list; remember the choice for when the search ends.
            if (_beforeSearch is { } b) _beforeSearch = (b.Path, mode == ViewMode.Columns ? mode : b.Mode);
            if (mode == ViewMode.Columns) Mode = from;
            BuildView();
            UpdateChrome();
            return;
        }

        if (mode == ViewMode.Columns)
        {
            DetachFlat();
            BuildView();
            _ = _columns!.ShowAsync(CurrentPath, selectName);
        }
        else if (from == ViewMode.Columns)
        {
            _columns?.DisposeAll();
            _columns = null;
            BuildView();
            _ = LoadAsync(CurrentPath, selectName, recordVisit: false);
        }
        else
        {
            (from == ViewMode.Tiles ? (ItemViewBase?)_tiles : _details)?.Detach();
            BuildView();
            if (FocusedItem is { } f) ActiveView?.BringIntoView(f);
        }
        if (focusWasInside) FocusList();
        UpdateChrome();
        StateChanged?.Invoke();
    }

    private void DetachFlat()
    {
        _details?.Detach();
        _tiles?.Detach();
        _sizeCts?.Cancel();
        _listing?.Dispose();
        _listing = null;
        _selection = null;
    }

    public void SetSort(SortKey key, bool? descending = null)
    {
        var desc = descending ?? (Sort.Key == key ? !Sort.Descending : key is SortKey.Modified or SortKey.Size or SortKey.Created);
        Sort = new SortSpec { Key = key, Descending = desc, FoldersFirst = AppSettings.Current.FoldersFirst };
        _listing?.SetSort(Sort);
        _columns?.SetSort(Sort);
        _details?.UpdateSortGlyph(Sort);
        if (FocusedItem is { } f) ActiveView?.BringIntoView(f);
        StateChanged?.Invoke();
    }

    /// <summary>Settings changed (hidden files, row density, dates…): redraw everything.</summary>
    public void ApplySettings()
    {
        Sort = new SortSpec { Key = Sort.Key, Descending = Sort.Descending, FoldersFirst = AppSettings.Current.FoldersFirst };
        _listing?.SetSort(Sort);
        _listing?.ApplyView();
        _columns?.SetSort(Sort);
        _columns?.ApplyView();
        _details?.Layout.Raise();
        _details?.InvalidateRows();
        _tiles?.InvalidateRows();
        UpdateStatus();
    }

    public void InvalidateRows()
    {
        _details?.InvalidateRows();
        _tiles?.InvalidateRows();
        _columns?.InvalidateRows();
    }

    public void FocusList()
    {
        if (Mode == ViewMode.Columns && !IsSearch) _columns?.FocusDeepest();
        else ActiveView?.Focus();
    }

    public void FocusFilter()
    {
        _filter.Focus();
        _filter.SelectAll();
    }

    public void EditPath() => _address.BeginEdit();

    // ---- Navigation ----------------------------------------------------------------------------------------------------

    private async Task NavigateTextAsync(string text)
    {
        var resolved = PathUtil.Resolve(text, IsSearch ? _beforeSearch?.Path : CurrentPath);
        if (resolved is null)
        {
            Toasts.Show($"Can't find “{text}”.", ToastKind.Error);
            return;
        }
        await NavigateAsync(resolved);
        FocusList();
    }

    /// <summary>Goes to a folder; a file path opens its folder with the file selected.</summary>
    public async Task NavigateAsync(string path, string? select = null, bool addHistory = true)
    {
        if (!PathUtil.IsVirtual(path) && !PathUtil.IsRemote(path))
        {
            var exists = await Task.Run(() => Directory.Exists(path) ? 1 : File.Exists(path) ? 2 : 0);
            if (exists == 2)
            {
                select = Path.GetFileName(path);
                path = Path.GetDirectoryName(path) ?? path;
            }
            else if (exists == 0)
            {
                Toasts.Show($"“{path}” doesn't exist or isn't reachable.", ToastKind.Error);
                return;
            }
        }
        if (addHistory)
        {
            _back.Add(CurrentEntry());
            if (_back.Count > 100) _back.RemoveAt(0);
            _forward.Clear();
        }
        await LoadAsync(path, select, recordVisit: true);
    }

    private HistoryEntry CurrentEntry() => new(_beforeSearch?.Path ?? CurrentPath, IsSearch ? null : FocusedItem?.Name);

    public void GoBack()
    {
        if (IsSearch)
        {
            ExitSearch();
            return;
        }
        if (_back.Count == 0) return;
        var entry = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentEntry());
        _ = LoadAsync(entry.Path, entry.Selected, recordVisit: false);
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        var entry = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentEntry());
        _ = LoadAsync(entry.Path, entry.Selected, recordVisit: false);
    }

    public void GoUp()
    {
        if (IsSearch)
        {
            ExitSearch();
            return;
        }
        var from = Mode == ViewMode.Columns ? (_columns?.Active?.Path ?? CurrentPath) : CurrentPath;
        var parent = PathUtil.Parent(from);
        if (parent is null) return;
        // Land on the folder we came out of, like Explorer.
        var name = PathUtil.IsRoot(from) ? null : Path.GetFileName(PathUtil.Trim(from));
        _back.Add(CurrentEntry());
        _forward.Clear();
        _ = LoadAsync(parent, PathUtil.IsRoot(from) ? from : name, recordVisit: true, selectByPath: PathUtil.IsRoot(from));
    }

    public async Task RefreshAsync()
    {
        if (IsSearch)
        {
            if (_searchText is not null) await SearchAsync(_searchText);
            return;
        }
        FolderSizes.Invalidate(CurrentPath);
        if (Mode == ViewMode.Columns && _columns is not null) await _columns.RefreshAsync();
        else if (_listing is not null)
        {
            await _listing.RefreshAsync();
            if (FolderSizes.ShouldAutoMeasure(CurrentPath)) MeasureSizes();
        }
        UpdateGitAndSpace();
    }

    private async Task LoadAsync(string path, string? select, bool recordVisit, bool selectByPath = false)
    {
        CancelSearch();
        _beforeSearch = null;
        _filter.Text = "";
        CurrentPath = PathUtil.IsComputer(path) ? path : PathUtil.Trim(path);
        if (recordVisit) Frecency.Visit(CurrentPath);
        UpdateChrome();
        _app.PathChanged(this);
        SyncTerminal();

        if (Mode == ViewMode.Columns)
        {
            DetachFlat();
            BuildView();
            await _columns!.ShowAsync(CurrentPath, select);
            UpdateGitAndSpace();
            UpdateStatus();
            return;
        }

        _sizeCts?.Cancel();
        var listing = new FolderListing(CurrentPath, Sort);
        var selection = new SelectionModel(listing.Items);
        var old = _listing;
        _listing = listing;
        _selection = selection;
        listing.StateChanged += () =>
        {
            if (_listing == listing) UpdateStatus();
        };
        listing.Changed += (added, _) =>
        {
            if (_listing != listing) return;
            selection.Prune();
            ResolvePending(added);
            if (added.Any(a => a.IsDirectory) && FolderSizes.ShouldAutoMeasure(CurrentPath)) MeasureSizes();
        };
        listing.Reset += () =>
        {
            if (_listing != listing) return;
            selection.Prune();
        };
        BuildView();
        old?.Dispose();
        _details?.ScrollToTop();

        await listing.LoadAsync();
        if (_listing != listing) return;

        FileItem? target = null;
        if (select is not null)
            target = selectByPath
                ? listing.Items.FirstOrDefault(i => PathUtil.Same(i.Path, select))
                : listing.Find(select);
        if (target is not null) SelectAndReveal(target);
        else if (listing.Items.Count > 0) selection.SetCaret(listing.Items[0]);

        if (FolderSizes.ShouldAutoMeasure(CurrentPath)) MeasureSizes();
        UpdateGitAndSpace();
        UpdateStatus();
        _app.SelectionChanged(this);
    }

    private void SelectAndReveal(FileItem item)
    {
        _pendingSelect = null;
        ActiveSelection?.SelectOnly(item);
        ActiveView?.BringIntoView(item);
    }

    /// <summary>Selects an item by name as soon as it shows up (after a paste, rename or new folder).</summary>
    public void SelectWhenItAppears(string name, bool rename = false)
    {
        _pendingSelect = name;
        _renameWhenAppears = rename ? name : null;
        if (ActiveListing?.Find(name) is { } existing)
        {
            ResolvePending([existing]);
            return;
        }
        // Network shares may not send change notifications: look for it ourselves shortly.
        DispatcherTimer.RunOnce(async () =>
        {
            if (_pendingSelect != name || ActiveListing is not { } l) return;
            await l.RefreshAsync();
            if (l.Find(name) is { } item) ResolvePending([item]);
        }, TimeSpan.FromMilliseconds(500));
    }

    private string? _renameWhenAppears;

    /// <summary>The item we were waiting for arrived: select it, and open the rename box if that was asked for.</summary>
    private void ResolvePending(IEnumerable<FileItem> arrived)
    {
        if (_pendingSelect is null) return;
        if (arrived.FirstOrDefault(a => PathUtil.Comparer.Equals(a.Name, _pendingSelect)) is not { } hit) return;
        var rename = _renameWhenAppears is not null && PathUtil.Comparer.Equals(_renameWhenAppears, hit.Name);
        _renameWhenAppears = null;
        SelectAndReveal(hit);
        if (rename) ActiveView?.BeginRename(hit);
    }

    public void MeasureSizes(bool force = false)
    {
        if (_listing is null || _listing.IsVirtual || IsSearch) return;
        _sizeCts?.Cancel();
        _sizeCts = new CancellationTokenSource();
        if (force) FolderSizes.Invalidate(CurrentPath);
        FolderSizes.MeasureAll(_listing, _sizeCts.Token);
    }

    // ---- Filter and search ---------------------------------------------------------------------------------------------

    private void QueueFilter()
    {
        if (IsSearch) return;
        _filterTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Input, (_, _) =>
        {
            _filterTimer!.Stop();
            ActiveListing?.SetFilter(_filter.Text ?? "");
            UpdateStatus();
        });
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var text = (_filter.Text ?? "").Trim();
                if (text.Length > 0) _ = SearchAsync(text);
                e.Handled = true;
                break;
            case Key.Escape:
                if (IsSearch) ExitSearch();
                else _filter.Text = "";
                FocusList();
                e.Handled = true;
                break;
            case Key.Down:
                FocusList();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Recursive search under the current folder; results stream into a flat list with a Folder column.</summary>
    public async Task SearchAsync(string text)
    {
        var query = SearchQuery.Parse(text);
        if (query.IsEmpty) return;
        var root = _beforeSearch?.Path ?? ActiveFolder;
        if (PathUtil.IsRemote(root))
        {
            Toasts.Show("Searching inside servers isn't supported yet — the filter box still filters this folder.");
            return;
        }
        if (PathUtil.IsComputer(root))
        {
            Toasts.Show("Open a drive or folder first, then search inside it.");
            return;
        }
        CancelSearch();
        _beforeSearch ??= (CurrentPath, Mode);
        if (Mode == ViewMode.Columns)
        {
            _columns?.DisposeAll();
            _columns = null;
            Mode = ViewMode.Details;
        }
        _sizeCts?.Cancel();
        _details?.Detach();
        _tiles?.Detach();
        _listing?.Dispose();

        var listing = FolderListing.ForSearch(root, Sort);
        var selection = new SelectionModel(listing.Items);
        _listing = listing;
        _selection = selection;
        _searchText = text;
        _searchProgress = null;
        listing.StateChanged += () =>
        {
            if (_listing == listing) UpdateStatus();
        };
        BuildView();
        _filter.Text = text;
        UpdateChrome();
        _app.PathChanged(this);

        var cts = _searchCts = new CancellationTokenSource();
        var rootTrim = PathUtil.Trim(root);
        try
        {
            await SearchEngine.RunAsync(root, query, new SearchOptions(IncludeHidden: AppSettings.Current.ShowHidden),
                hits =>
                {
                    var items = hits.Select(h => new FileItem(h.Path, h.Name, h.IsDirectory ? ItemKind.Folder : ItemKind.File, h.Size,
                        h.ModifiedUtc.ToLocalTime(), h.CreatedUtc.ToLocalTime(), h.Attributes)
                    {
                        Location = RelativeFolder(rootTrim, h.Path),
                    }).ToList();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cts.IsCancellationRequested) listing.AppendResults(items);
                    });
                },
                progress => Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _searchProgress = progress;
                    UpdateStatus();
                }),
                cts.Token);
        }
        catch (Exception ex)
        {
            CrashLog.Write("search", ex);
            Dispatcher.UIThread.Post(() => listing.SearchFinished(ex.Message));
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_listing == listing) listing.SearchFinished();
        });
    }

    private static string RelativeFolder(string root, string path)
    {
        var parent = Path.GetDirectoryName(path) ?? "";
        var rel = Path.GetRelativePath(root, parent);
        return rel == "." ? "(this folder)" : rel;
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    public void ExitSearch()
    {
        if (!IsSearch) return;
        CancelSearch();
        var before = _beforeSearch;
        _beforeSearch = null;
        _filter.Text = "";
        _details?.Detach();
        _listing?.Dispose();
        _listing = null;
        _selection = null;
        if (before is { } b)
        {
            Mode = b.Mode;
            BuildView();
            _ = LoadAsync(b.Path, null, recordVisit: false);
        }
        FocusList();
    }

    // ---- Chrome --------------------------------------------------------------------------------------------------------

    private void UpdateChrome()
    {
        if (IsSearch)
            _address.SetPath(CurrentPath, $"Results for “{_searchText}” in {PathUtil.DisplayName(_beforeSearch?.Path ?? CurrentPath)}");
        else
            _address.SetPath(CurrentPath);
        _backBtn.IsEnabled = CanGoBack;
        _forwardBtn.IsEnabled = CanGoForward;
        _upBtn.IsEnabled = IsSearch || PathUtil.Parent(CurrentPath) is not null;
        StateChanged?.Invoke();
    }

    private void UpdateGitAndSpace()
    {
        _gitCts?.Cancel();
        var cts = _gitCts = new CancellationTokenSource();
        var path = CurrentPath;
        var listing = _listing;
        _ = Task.Run(async () =>
        {
            var remote = PathUtil.IsRemote(path);
            var repo = remote ? null : GitInfo.Find(path);
            string free = "";
            try
            {
                if (remote && Core.Remote.RemotePath.TryParse(path, out var conn, out _) && conn is not null)
                    free = $"{conn.Scheme.ToUpperInvariant()} · {conn.User}{(conn.User.Length > 0 ? "@" : "")}{conn.Host}";
                else if (!PathUtil.IsVirtual(path) && Path.GetPathRoot(path) is { } root)
                {
                    var d = new DriveInfo(root);
                    free = $"{Formatting.Size(d.AvailableFreeSpace)} free";
                }
            }
            catch
            {
                // UNC roots and disconnected drives have no answer.
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _repo = repo;
                _freeText = free;
                UpdateStatus();
            });
            if (repo is null || listing is null || cts.IsCancellationRequested) return;
            var status = await GitInfo.StatusAsync(repo.Root, cts.Token);
            if (status is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || _listing != listing) return;
                GitInfo.Apply(repo, status, listing.All);
            });
        }, cts.Token);
    }

    public void UpdateStatus()
    {
        var listing = ActiveListing;
        var sel = ActiveSelection;
        _loading.IsVisible = listing?.IsLoading == true;
        if (listing is null)
        {
            _statusLeft.Text = "";
            return;
        }

        if (listing.Error is { } err)
        {
            _message.Text = err;
            _message.Foreground = Skin.Bad;
            _message.IsVisible = true;
        }
        else if (listing.IsLoaded && !listing.IsLoading && listing.Items.Count == 0 && Mode != ViewMode.Columns)
        {
            _message.Text = IsSearch ? "No matches." : listing.Filter.Length > 0 ? "Nothing matches the filter." : "This folder is empty.";
            _message.Foreground = Skin.Faint;
            _message.IsVisible = true;
        }
        else _message.IsVisible = false;

        var parts = new List<string>();
        if (IsSearch)
        {
            parts.Add(Formatting.Count(listing.Items.Count, "result"));
            if (listing.IsLoading && _searchProgress is { } p)
                parts.Add($"searching… {p.FoldersScanned:N0} folders");
            else if (_searchProgress is { } done)
                parts.Add($"{done.FoldersScanned:N0} folders searched");
        }
        else
        {
            parts.Add(Formatting.Count(listing.Items.Count, "item"));
            if (listing.HiddenCount > 0 && listing.Filter.Length > 0)
                parts.Add($"{listing.HiddenCount:N0} filtered out");
        }
        if (sel is { Count: > 0 })
        {
            long bytes = 0;
            var exact = true;
            foreach (var i in sel.Set)
            {
                var s = i.EffectiveSize;
                if (i.Kind == ItemKind.Folder && (s < 0 || i.FolderSizePartial)) exact = false;
                if (s > 0) bytes += s;
            }
            parts.Add($"{sel.Count:N0} selected" + (bytes > 0 ? $" ({(exact ? "" : "≥ ")}{Formatting.Size(bytes)})" : ""));
        }
        _statusLeft.Text = string.Join("  ·  ", parts);

        var right = new List<string>();
        if (_repo is not null) right.Add("⎇ " + _repo.Branch);
        if (_freeText.Length > 0) right.Add(_freeText);
        _statusRight.Text = string.Join("   ", right);
    }

    // ---- IItemViewHost -------------------------------------------------------------------------------------------------

    public bool IsActivePane => _app.IsActivePaneView(this);

    public void ItemsActivated(ItemViewBase view, IReadOnlyList<FileItem> items, bool inNewTab)
    {
        if (items.Count == 1 && items[0].IsDirectory)
        {
            var folder = items[0];
            if (inNewTab) _app.OpenInNewTab(folder.Path, false);
            else if (Mode == ViewMode.Columns && !IsSearch) _columns?.MoveFocus(view, right: true);
            else _ = NavigateAsync(folder.Path);
            return;
        }
        foreach (var f in items.Where(i => i.IsDirectory))
            _app.OpenInNewTab(f.Path, false);
        var files = items.Where(i => !i.IsDirectory).ToList();
        if (files.Count > 0) _app.OpenItems(this, files);
    }

    public void ItemContextRequested(ItemViewBase view, FileItem? item, Point positionInView, bool shellMenu) =>
        _app.ShowContextMenu(this, view, item, positionInView, shellMenu);

    public void SelectionChanged(ItemViewBase view)
    {
        if (Mode == ViewMode.Columns && !IsSearch) _columns?.OnSelectionChanged(view);
        UpdateStatus();
        _app.SelectionChanged(this);
    }

    public void ViewFocused(ItemViewBase view)
    {
        _columns?.NoteFocused(view);
        _app.ViewActivated(this);
        UpdateStatus();
        _app.SelectionChanged(this);
    }

    public void NavigateUp(ItemViewBase view)
    {
        if (Mode == ViewMode.Columns && !IsSearch) _columns?.MoveFocus(view, right: false);
        else GoUp();
    }

    public void HorizontalKey(ItemViewBase view, bool right)
    {
        if (Mode == ViewMode.Columns && !IsSearch) _columns?.MoveFocus(view, right);
    }

    public void QuickLook(ItemViewBase view) => _app.QuickLook(this);

    public Task<bool> CommitRenameAsync(FileItem item, string newName) => _app.RenameAsync(this, item, newName);

    public Task DropAsync(ItemViewBase view, string targetFolder, IReadOnlyList<string> paths, DragDropEffects effect) =>
        _app.DropAsync(this, targetFolder, paths, effect);

    public void BeginRename(FileItem item) => ActiveView?.BeginRename(item);

    // ---- Docked terminal ----------------------------------------------------------------------------------------------

    /// <summary>Where a terminal for this folder runs: the SSH server it's on, or PowerShell for a folder on this PC.</summary>
    private sealed record TerminalPlace(TerminalSpec Spec, string ShellFolder, Func<string, string> ViewPath, Core.Remote.Connection? Connection);

    private static TerminalPlace? PlaceFor(string path)
    {
        if (Core.Remote.RemotePath.TryParse(path, out var c, out var remote))
            return c is { Protocol: Core.Remote.RemoteProtocol.Sftp } ? new(TerminalSpec.Ssh(c), remote, r => Core.Remote.RemotePath.Make(c, r), c) : null;
        if (PathUtil.IsVirtual(path) || !Directory.Exists(path)) return null;
        return new(TerminalSpec.PowerShell(), path, p => p, null);
    }

    public bool HasTerminal => _terminal is not null;
    /// <summary>Whether this tab can show a terminal where it is (an SSH server or a folder on this PC).</summary>
    public bool CanShowTerminal => PlaceFor(CurrentPath) is not null;

    /// <summary>
    /// Keeps the terminal with the tab: an SSH server opens its terminal on arrival; the PowerShell one stays on while
    /// browsing this PC if you turned it on. Either way the shell's folder follows the files.
    /// </summary>
    private void SyncTerminal()
    {
        var here = PlaceFor(CurrentPath);
        if (_terminal is not null && _terminal.ShellKey != here?.Spec.Key) CloseTerminal(dismiss: false);
        if (here is null)
        {
            StateChanged?.Invoke();
            return;
        }
        if (_terminal is null)
        {
            var wanted = here.Spec.Local ? _localTerminalWanted : !_terminalDismissed.Contains(here.Spec.Key);
            if (wanted) OpenTerminal(here, focus: false);
        }
        else _terminal.ChangeDirectory(here.ShellFolder);
        StateChanged?.Invoke();
    }

    /// <summary>Ctrl+` and View ▸ Terminal: show or hide the terminal here. False when there's nowhere to run one.</summary>
    /// <summary>
    /// Opens the PowerShell terminal here (this tab must be on a folder on this PC) and runs a saved connection's
    /// steps in it. False when this tab can't have a local terminal.
    /// </summary>
    public bool RunInLocalTerminal(string note, IEnumerable<M365.Step> steps, string? buttonLabel = null, Action? buttonAction = null, string? buttonTip = null)
    {
        if (PlaceFor(CurrentPath) is not { Spec.Local: true } here) return false;
        if (_terminal is null)
        {
            _localTerminalWanted = true;
            OpenTerminal(here, focus: true);
        }
        _terminal!.SetHeaderAction(buttonLabel, buttonAction, buttonTip);
        _terminal.Run(note, steps);
        _terminal.FocusInput();
        return true;
    }

    public bool ToggleTerminal()
    {
        if (_terminal is not null)
        {
            CloseTerminal(dismiss: true);
            FocusList();
            return true;
        }
        if (PlaceFor(CurrentPath) is not { } here) return false;
        if (here.Spec.Local) _localTerminalWanted = true;
        else _terminalDismissed.Remove(here.Spec.Key);
        OpenTerminal(here, focus: true);
        return true;
    }

    private void OpenTerminal(TerminalPlace place, bool focus)
    {
        Action external = place.Connection is { } conn
            ? () => _app.OpenExternalTerminal(conn)
            : () =>
            {
                try { Shell.OpenTerminal(_terminal?.Cwd ?? place.ShellFolder); }
                catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
            };
        var pane = new TerminalPane(place.Spec, place.ShellFolder, external, () => CloseTerminal(dismiss: true));
        pane.MaximizeRequested += ToggleTerminalMaximized;
        pane.FolderReported += (folder, changed) =>
        {
            if (_terminal != pane || PlaceFor(CurrentPath) is not { } cur || cur.Spec.Key != place.Spec.Key) return;
            var same = place.Spec.Local ? PathUtil.Same(cur.ShellFolder, folder) : cur.ShellFolder == folder;
            if (changed && !same) _ = NavigateAsync(place.ViewPath(folder));
            else if (same && !place.Spec.Local) _ = RefreshAsync(); // a command may have changed files (local folders are watched)
        };
        _terminal = pane;
        _terminalHost.Child = pane;
        _terminalRow.Height = new GridLength(_terminalHeight);
        _terminalSplitter.IsVisible = true;
        if (focus) Dispatcher.UIThread.Post(pane.FocusInput, DispatcherPriority.Background);
        StateChanged?.Invoke();
    }

    /// <summary>Full size: the terminal takes the whole tab (the files hide), or goes back to its docked height.</summary>
    public void ToggleTerminalMaximized()
    {
        if (_terminal is null) return;
        _terminalMaximized = !_terminalMaximized;
        if (_terminalMaximized)
        {
            if (_terminalHost.Bounds.Height > 60) _terminalHeight = _terminalHost.Bounds.Height;
            _contentHost.IsVisible = false;
            _contentRow.Height = new GridLength(0);
            _terminalRow.Height = new GridLength(1, GridUnitType.Star);
            _terminalSplitter.IsVisible = false;
        }
        else
        {
            _contentHost.IsVisible = true;
            _contentRow.Height = new GridLength(1, GridUnitType.Star);
            _terminalRow.Height = new GridLength(_terminalHeight);
            _terminalSplitter.IsVisible = true;
        }
        _terminal.SetMaximized(_terminalMaximized);
        _terminal.FocusInput();
    }

    private void CloseTerminal(bool dismiss)
    {
        if (_terminal is null) return;
        if (_terminalMaximized) ToggleTerminalMaximized();
        if (dismiss)
        {
            if (_terminal.IsLocal) _localTerminalWanted = false;
            else _terminalDismissed.Add(_terminal.ShellKey);
        }
        if (_terminalHost.Bounds.Height > 60) _terminalHeight = _terminalHost.Bounds.Height;
        _terminal.Dispose();
        _terminal = null;
        _terminalHost.Child = null;
        _terminalRow.Height = GridLength.Auto;
        _terminalSplitter.IsVisible = false;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        CloseTerminal(dismiss: false);
        CancelSearch();
        _sizeCts?.Cancel();
        _gitCts?.Cancel();
        _details?.Detach();
        _tiles?.Detach();
        _listing?.Dispose();
        _columns?.DisposeAll();
    }
}
