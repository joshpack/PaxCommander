using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>One column of the Miller view: a folder, its own listing and selection.</summary>
public sealed class MillerColumn : IDisposable
{
    public MillerColumn(string path, SortSpec sort, IItemViewHost host)
    {
        Path = path;
        Listing = new FolderListing(path, sort);
        Selection = new SelectionModel(Listing.Items);
        View = new DetailsList(columnMode: true) { Host = host };
        View.Attach(Listing, Selection);
        Status = new TextBlock
        {
            Classes = { "faint" },
            FontSize = 12,
            Margin = new Thickness(16, 14),
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        Listing.StateChanged += UpdateStatus;
        var grid = new Grid();
        grid.Children.Add(View);
        grid.Children.Add(Status);
        Container = new Border
        {
            Width = AppSettings.Current.MillerColumnWidth,
            BorderBrush = Skin.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = grid,
        };
    }

    public string Path { get; }
    public FolderListing Listing { get; }
    public SelectionModel Selection { get; }
    public DetailsList View { get; }
    public Border Container { get; }
    /// <summary>Sized by hand: keeps its width when the pane is resized.</summary>
    public bool Resized { get; set; }
    private TextBlock Status { get; }

    private void UpdateStatus()
    {
        Status.Text = Listing.Error ?? (Listing.IsLoaded && Listing.Items.Count == 0 ? "Empty folder" : "");
        Status.IsVisible = Status.Text.Length > 0;
    }

    public void Dispose()
    {
        View.Detach();
        Listing.Dispose();
    }
}

/// <summary>
/// Miller columns (OneCommander's signature view): every folder on the path gets a column; selecting a folder opens
/// the next column, so a whole tree can be browsed with the arrow keys.
/// </summary>
public sealed class ColumnsBrowser : UserControl
{
    private readonly IItemViewHost _host;
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;
    private readonly List<MillerColumn> _columns = [];
    private readonly List<Control> _grips = [];
    private SortSpec _sort;
    private bool _suppress;
    private DispatcherTimer? _openTimer;
    private (int Index, string Path)? _pendingOpen;

    public ColumnsBrowser(IItemViewHost host, SortSpec sort)
    {
        _host = host;
        _sort = sort;
        _scroll = new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Content = _scroll;
        _scroll.SizeChanged += (_, _) =>
        {
            foreach (var c in _columns.Where(c => !c.Resized)) c.Container.Width = ColumnWidth;
        };
    }

    /// <summary>The chosen column width, narrowed in a slim pane so at least two columns show side by side.</summary>
    private double ColumnWidth
    {
        get
        {
            var chosen = AppSettings.Current.MillerColumnWidth;
            var viewport = _scroll.Bounds.Width;
            return viewport > 0 ? Math.Min(chosen, Math.Max(170, (viewport - 12) / 2)) : chosen;
        }
    }

    public IReadOnlyList<MillerColumn> Columns => _columns;

    /// <summary>The column with keyboard focus, else the deepest one.</summary>
    public MillerColumn? Active => _columns.FirstOrDefault(c => c.View.IsKeyboardFocusWithin) ?? _lastActive ?? _columns.LastOrDefault();
    private MillerColumn? _lastActive;

    public string? DeepestPath => _columns.LastOrDefault()?.Path;

    /// <summary>The deepest open folder changed because of a selection inside the columns.</summary>
    public event Action<string>? DeepestChanged;

    public MillerColumn? ColumnOf(ItemViewBase view) => _columns.FirstOrDefault(c => c.View == view);

    public void NoteFocused(ItemViewBase view)
    {
        if (ColumnOf(view) is { } c) _lastActive = c;
    }

    /// <summary>Rebuilds the chain of columns from the top down to <paramref name="path"/>.</summary>
    public async Task ShowAsync(string path, string? selectName)
    {
        var crumbs = PathUtil.Crumbs(path);
        // Reuse the columns that are already showing the right folders.
        var keep = 0;
        while (keep < _columns.Count && keep < crumbs.Count && PathUtil.Same(_columns[keep].Path, crumbs[keep].Path))
            keep++;
        TruncateAfter(keep - 1);

        _suppress = true;
        try
        {
            for (var i = keep; i < crumbs.Count; i++)
                AddColumn(crumbs[i].Path);
            var loads = _columns.Where(c => !c.Listing.IsLoaded && !c.Listing.IsLoading).Select(c => c.Listing.LoadAsync()).ToList();
            await Task.WhenAll(loads);

            // Each column highlights the folder that the next column shows.
            for (var i = 0; i < _columns.Count - 1; i++)
            {
                var next = _columns[i + 1].Path;
                var item = _columns[i].Listing.Items.FirstOrDefault(x => PathUtil.Same(x.Path, next));
                if (item is not null && !(item.IsSelected && _columns[i].Selection.Count == 1))
                    _columns[i].Selection.SelectOnly(item);
                if (item is not null) _columns[i].View.BringIntoView(item);
            }
            var last = _columns.LastOrDefault();
            if (last is not null && selectName is not null && last.Listing.Find(selectName) is { } sel)
            {
                last.Selection.SelectOnly(sel);
                last.View.BringIntoView(sel);
            }
        }
        finally
        {
            _suppress = false;
        }
        ScrollToEnd();
    }

    private MillerColumn AddColumn(string path)
    {
        var column = new MillerColumn(path, _sort, _host);
        column.Container.Width = ColumnWidth;
        _columns.Add(column);
        _strip.Children.Add(column.Container);
        var grip = MakeGrip(column);
        _grips.Add(grip);
        _strip.Children.Add(grip);
        return column;
    }

    private Control MakeGrip(MillerColumn column)
    {
        var grip = new Border
        {
            Width = 5,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        double startX = 0, startW = 0;
        var dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            dragging = true;
            startX = e.GetPosition(this).X;
            startW = column.Container.Width;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            column.Container.Width = Math.Clamp(startW + e.GetPosition(this).X - startX, 140, 900);
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            // New columns open at the width last chosen.
            AppSettings.Current.MillerColumnWidth = column.Container.Width;
            column.Resized = true;
            AppSettings.SaveSoon();
        };
        return grip;
    }

    private void TruncateAfter(int index)
    {
        for (var i = _columns.Count - 1; i > index; i--)
        {
            var c = _columns[i];
            _strip.Children.Remove(c.Container);
            _strip.Children.Remove(_grips[i]);
            _grips.RemoveAt(i);
            _columns.RemoveAt(i);
            if (_lastActive == c) _lastActive = null;
            c.Dispose();
        }
    }

    /// <summary>Selection changed inside a column: open or close the columns to its right.</summary>
    public void OnSelectionChanged(ItemViewBase view)
    {
        if (_suppress || ColumnOf(view) is not { } column) return;
        var index = _columns.IndexOf(column);
        var sel = column.Selection;
        if (sel.Count == 1 && sel.Set.First() is { IsDirectory: true } folder)
        {
            if (index + 1 < _columns.Count && PathUtil.Same(_columns[index + 1].Path, folder.Path)) return;
            // Holding an arrow key through a list of folders shouldn't load every one of them.
            _pendingOpen = (index, folder.Path);
            _openTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(110), DispatcherPriority.Input, (_, _) => OpenPending());
            _openTimer.Stop();
            _openTimer.Start();
        }
        else
        {
            _pendingOpen = null;
            if (index < _columns.Count - 1)
            {
                TruncateAfter(index);
                DeepestChanged?.Invoke(column.Path);
            }
        }
    }

    private async void OpenPending()
    {
        _openTimer?.Stop();
        if (_pendingOpen is not { } p) return;
        _pendingOpen = null;
        if (p.Index >= _columns.Count) return;
        TruncateAfter(p.Index);
        var column = AddColumn(p.Path);
        DeepestChanged?.Invoke(p.Path);
        ScrollToEnd();
        await column.Listing.LoadAsync();
    }

    /// <summary>Right arrow: step into the next column; Left: back to the parent column.</summary>
    public void MoveFocus(ItemViewBase from, bool right)
    {
        if (ColumnOf(from) is not { } column) return;
        if (right && _pendingOpen is not null) OpenPending();
        var index = _columns.IndexOf(column) + (right ? 1 : -1);
        if (index < 0 || index >= _columns.Count) return;
        var target = _columns[index];
        if (right && target.Selection.Count == 0 && target.Listing.Items.Count > 0)
            target.Selection.SelectOnly(target.Listing.Items[0]);
        if (!right)
        {
            // Leaving a column clears it, so the parent's highlighted folder is the "current" one again.
            column.Selection.Clear();
            column.Selection.SetCaret(null);
        }
        target.View.Focus();
        _lastActive = target;
        BringColumnIntoView(target);
    }

    public void FocusDeepest()
    {
        var c = _lastActive ?? _columns.LastOrDefault();
        c?.View.Focus();
    }

    public void SetSort(SortSpec sort)
    {
        _sort = sort;
        foreach (var c in _columns) c.Listing.SetSort(sort);
    }

    public void ApplyView()
    {
        foreach (var c in _columns) c.Listing.ApplyView();
    }

    public async Task RefreshAsync()
    {
        foreach (var c in _columns.ToList()) await c.Listing.RefreshAsync();
    }

    public void InvalidateRows()
    {
        foreach (var c in _columns) c.View.InvalidateRows();
    }

    /// <summary>
    /// Shows the deepest columns, starting on a column edge: as many whole columns as fit, rather than a sliver of
    /// one cut off at the left.
    /// </summary>
    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        var viewport = _scroll.Viewport.Width;
        if (_columns.Count == 0 || viewport <= 0) return;
        // Keep the column with the keyboard in view, plus as many deeper ones as fit; then fill leftwards.
        var active = _columns.IndexOf(Active ?? _columns[^1]);
        if (active < 0) active = _columns.Count - 1;
        var start = active;
        var used = 0.0;
        for (var i = active; i < _columns.Count; i++) used += _columns[i].Container.Bounds.Width + 5;
        while (start > 0 && used + _columns[start - 1].Container.Bounds.Width + 5 <= viewport)
        {
            start--;
            used += _columns[start].Container.Bounds.Width + 5;
        }
        var x = _columns[start].Container.TranslatePoint(new Point(0, 0), _strip)?.X ?? 0;
        // Room after the last column so the scroll can stop exactly on that edge.
        _strip.Margin = new Thickness(0, 0, start > 0 ? Math.Max(0, viewport - used) : 0, 0);
        Dispatcher.UIThread.Post(() => _scroll.Offset = new Vector(Math.Max(0, Math.Min(x, _scroll.Extent.Width - viewport)), 0), DispatcherPriority.Background);
    }, DispatcherPriority.Background);

    private void BringColumnIntoView(MillerColumn c) => Dispatcher.UIThread.Post(() =>
    {
        if (c.Container.TranslatePoint(new Point(0, 0), _strip) is not { } p) return;
        var x = _scroll.Offset.X;
        if (p.X < x) _scroll.Offset = new Vector(p.X, 0);
        else if (p.X + c.Container.Bounds.Width > x + _scroll.Viewport.Width)
            _scroll.Offset = new Vector(p.X + c.Container.Bounds.Width - _scroll.Viewport.Width, 0);
    }, DispatcherPriority.Background);

    public void DisposeAll() => TruncateAfter(-1);
}
