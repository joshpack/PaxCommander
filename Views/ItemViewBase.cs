using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pxdCommander.Core;

namespace pxdCommander.Views;

/// <summary>What an item view needs from the folder tab that owns it.</summary>
public interface IItemViewHost
{
    bool IsActivePane { get; }
    void ItemsActivated(ItemViewBase view, IReadOnlyList<FileItem> items, bool inNewTab);
    void ItemContextRequested(ItemViewBase view, FileItem? item, Point positionInView, bool shellMenu);
    void SelectionChanged(ItemViewBase view);
    void ViewFocused(ItemViewBase view);
    void NavigateUp(ItemViewBase view);
    void HorizontalKey(ItemViewBase view, bool right);
    void QuickLook(ItemViewBase view);
    Task<bool> CommitRenameAsync(FileItem item, string newName);
    Task DropAsync(ItemViewBase view, string targetFolder, IReadOnlyList<string> paths, DragDropEffects effect);
}

/// <summary>
/// Shared behaviour of the item views (details list, Miller column, tiles): Explorer-style mouse selection with
/// deferred click on multi-selections (so they can be dragged), rubber-band selection, keyboard navigation,
/// type-ahead, drag and drop in and out, and the inline rename box.
/// </summary>
public abstract class ItemViewBase : UserControl
{
    public static readonly DataFormat<string[]> PathsFormat = DataFormat.CreateInProcessFormat<string[]>("pxdCommander.Paths");

    protected readonly Grid Root = new();
    protected readonly Canvas Overlay = new() { ClipToBounds = true };
    private readonly Border _band = new()
    {
        Background = Skin.Band,
        BorderBrush = Skin.BandBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        IsVisible = false,
        IsHitTestVisible = false,
    };

    private TextBox? _renameBox;
    private FileItem? _renaming;
    private bool _committing;

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private FileItem? _pressItem;
    private FileItem? _deferredSelect;
    private bool _dragStarted;

    private bool _banding;
    private Point _bandStartContent;
    private Point _bandLastView;
    private IReadOnlyCollection<FileItem>? _bandKeep;
    private DispatcherTimer? _bandScroll;

    private FileItem? _dropHighlight;
    private string _typeBuffer = "";
    private long _typeTime;

    public FolderListing? Listing { get; private set; }
    public SelectionModel? Selection { get; private set; }
    public IItemViewHost? Host { get; set; }

    /// <summary>Selection is drawn in the accent colour only in the list that has the keyboard and the active pane.</summary>
    public bool IsActiveList => IsKeyboardFocusWithin && (Host?.IsActivePane ?? true);

    protected ItemViewBase()
    {
        Focusable = true;
        Content = Root;
        Overlay.Children.Add(_band);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(null));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public virtual void Attach(FolderListing listing, SelectionModel selection)
    {
        Detach();
        Listing = listing;
        Selection = selection;
        selection.Changed += OnSelectionChanged;
    }

    public virtual void Detach()
    {
        CancelRename();
        if (Selection is not null) Selection.Changed -= OnSelectionChanged;
        Listing = null;
        Selection = null;
    }

    private void OnSelectionChanged() => Host?.SelectionChanged(this);

    /// <summary>Converts a point from <see cref="PositionRoot"/> coordinates to this control's.</summary>
    protected Point ToView(Point p) => PositionRoot.TranslatePoint(p, this) ?? p;

    // ---- Layout-specific hooks ---------------------------------------------------------------------------------------

    /// <summary>The item under a point in this control's coordinates.</summary>
    protected abstract FileItem? ItemAt(Point p);
    /// <summary>A left click on an item, before selection: return true when the layout handled it (e.g. a heart).</summary>
    protected virtual bool ItemPressed(FileItem item, Point pos) => false;
    /// <summary>The name cell of a realized item, in this control's coordinates, for the rename box.</summary>
    protected abstract Rect? NameRect(FileItem item);
    /// <summary>Selects what a band (in content coordinates) covers.</summary>
    protected abstract void SelectInBand(Rect content, IReadOnlyCollection<FileItem>? keep);
    protected abstract Vector ScrollOffset { get; set; }
    /// <summary>The element whose coordinates item positions, the band and the rename box use (the scrolling area).</summary>
    protected abstract Visual PositionRoot { get; }
    protected abstract Size Viewport { get; }
    /// <summary>Items per row (Up/Down step); 1 for lists.</summary>
    protected virtual int Step => 1;
    protected abstract int PageStep { get; }
    public abstract void BringIntoView(FileItem item);
    public abstract void InvalidateRows();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty)
            InvalidateRows();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Host?.ViewFocused(this);
        // Tabbing into a list with nothing focused puts the caret on the first item, like Explorer.
        if (Selection is { Caret: null } && Listing is { Items.Count: > 0 } && e.Source == this)
            Selection.SetCaret(Listing.Items[0]);
    }

    // ---- Mouse ---------------------------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Listing is null || Selection is null) return;
        if (_renameBox is not null)
        {
            if (e.Source is Visual v && v.FindAncestorOfType<TextBox>(true) == _renameBox) return;
            _ = CommitRenameAsync();
        }

        var point = e.GetCurrentPoint(PositionRoot);
        var pos = point.Position;
        var item = ItemAt(pos);
        var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        Focus();

        switch (point.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                if (item is not null && !ctrl && !shift && ItemPressed(item, pos))
                {
                    e.Handled = true;
                    return;
                }
                if (e.ClickCount >= 2 && item is not null && !ctrl && !shift)
                {
                    Selection.SelectOnly(item);
                    Host?.ItemsActivated(this, [item], false);
                    e.Handled = true;
                    return;
                }
                if (item is null)
                {
                    if (!ctrl && !shift) Selection.Clear();
                    StartBand(pos, ctrl ? Selection.Set.ToList() : null);
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
                if (shift) Selection.SelectRange(item, ctrl);
                else if (ctrl) Selection.Toggle(item);
                else if (item.IsSelected)
                {
                    // Wait for the release: pressing a selected item may be the start of dragging the whole selection.
                    _deferredSelect = item;
                    Selection.SetCaret(item);
                }
                else Selection.SelectOnly(item);
                _pressArgs = e;
                _pressPoint = pos;
                _pressItem = item;
                _dragStarted = false;
                e.Handled = true;
                break;

            case PointerUpdateKind.RightButtonPressed:
                if (item is null)
                {
                    if (!ctrl) Selection.Clear();
                }
                else if (!item.IsSelected) Selection.SelectOnly(item);
                else Selection.SetCaret(item);
                e.Handled = true;
                break;

            case PointerUpdateKind.MiddleButtonPressed:
                if (item is { IsDirectory: true })
                    Host?.ItemsActivated(this, [item], true);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(PositionRoot);
        if (_banding)
        {
            _bandLastView = pos;
            UpdateBand();
            return;
        }
        if (_pressArgs is not null && _pressItem is not null && !_dragStarted &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            Math.Abs(pos.X - _pressPoint.X) + Math.Abs(pos.Y - _pressPoint.Y) > 6)
        {
            _dragStarted = true;
            _deferredSelect = null;
            var press = _pressArgs;
            _pressArgs = null;
            var items = Selection?.Ordered().Where(i => !i.IsDrive).ToList() ?? [];
            if (items.Count > 0) _ = StartDragAsync(press, items);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(PositionRoot);
        if (_banding)
        {
            EndBand();
            e.Pointer.Capture(null);
            return;
        }
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            if (_deferredSelect is not null && !_dragStarted && Selection is not null)
                Selection.SelectOnly(_deferredSelect);
            if (AppSettings.Current.SingleClickOpen && !_dragStarted && _pressItem is not null && e.KeyModifiers == KeyModifiers.None)
                Host?.ItemsActivated(this, [_pressItem], false);
            _deferredSelect = null;
            _pressArgs = null;
            _pressItem = null;
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            Host?.ItemContextRequested(this, ItemAt(pos), ToView(pos), (e.KeyModifiers & KeyModifiers.Shift) != 0);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_banding) EndBand();
    }

    // ---- Rubber band ---------------------------------------------------------------------------------------------------

    private void StartBand(Point viewPos, IReadOnlyCollection<FileItem>? keep)
    {
        _banding = true;
        _bandKeep = keep;
        _bandStartContent = viewPos + ScrollOffset;
        _bandLastView = viewPos;
        _bandScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScrollBand());
        _bandScroll.Start();
    }

    private void UpdateBand()
    {
        var current = _bandLastView + ScrollOffset;
        var content = new Rect(_bandStartContent, current).Normalize();
        // Ignore tiny wobbles so a click on empty space doesn't flash a band.
        if (content.Width < 3 && content.Height < 3)
        {
            _band.IsVisible = false;
            return;
        }
        var off = ScrollOffset;
        var view = new Rect(content.X - off.X, content.Y - off.Y, content.Width, content.Height);
        _band.IsVisible = true;
        Canvas.SetLeft(_band, view.X);
        Canvas.SetTop(_band, view.Y);
        _band.Width = view.Width;
        _band.Height = view.Height;
        SelectInBand(content, _bandKeep);
    }

    private void AutoScrollBand()
    {
        if (!_banding) return;
        var h = Viewport.Height;
        double dy = 0;
        if (_bandLastView.Y < 8) dy = -Math.Min(40, 8 - _bandLastView.Y + 6);
        else if (_bandLastView.Y > h - 8) dy = Math.Min(40, _bandLastView.Y - (h - 8) + 6);
        if (dy == 0) return;
        ScrollOffset = new Vector(ScrollOffset.X, Math.Max(0, ScrollOffset.Y + dy));
        UpdateBand();
    }

    private void EndBand()
    {
        _banding = false;
        _band.IsVisible = false;
        _bandKeep = null;
        _bandScroll?.Stop();
    }

    // ---- Keyboard ------------------------------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Listing is null || Selection is null) return;
        if (_renameBox is not null && _renameBox.IsKeyboardFocusWithin) return;

        var items = Listing.Items;
        var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var caret = Selection.Caret is { } c ? items.IndexOf(c) : -1;
        var target = -1;

        switch (e.Key)
        {
            case Key.Down: target = caret < 0 ? 0 : Math.Min(items.Count - 1, caret + Step); break;
            case Key.Up: target = caret < 0 ? 0 : Math.Max(0, caret - Step); break;
            case Key.Right when Step > 1: target = caret < 0 ? 0 : Math.Min(items.Count - 1, caret + 1); break;
            case Key.Left when Step > 1: target = caret < 0 ? 0 : Math.Max(0, caret - 1); break;
            case Key.Right or Key.Left when e.KeyModifiers == KeyModifiers.None:
                Host?.HorizontalKey(this, e.Key == Key.Right);
                e.Handled = true;
                return;
            case Key.PageDown: target = caret < 0 ? 0 : Math.Min(items.Count - 1, caret + PageStep); break;
            case Key.PageUp: target = caret < 0 ? 0 : Math.Max(0, caret - PageStep); break;
            case Key.Home when !ctrl || shift: target = 0; break;
            case Key.End when !ctrl || shift: target = items.Count - 1; break;
            case Key.Enter when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control:
            {
                var sel = Selection.Ordered();
                if (sel.Count == 0 && Selection.Caret is not null) sel = [Selection.Caret];
                if (sel.Count > 0) Host?.ItemsActivated(this, sel, ctrl);
                e.Handled = true;
                return;
            }
            case Key.Back when e.KeyModifiers == KeyModifiers.None:
                Host?.NavigateUp(this);
                e.Handled = true;
                return;
            case Key.Space when ctrl:
                if (Selection.Caret is not null) Selection.Toggle(Selection.Caret);
                e.Handled = true;
                return;
            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                Host?.QuickLook(this);
                e.Handled = true;
                return;
            case Key.Escape when Selection.Count > 0:
                Selection.Clear();
                e.Handled = true;
                return;
            case Key.Apps:
            case Key.F10 when shift:
            {
                var at = ToView(Selection.Caret is { } ci && NameRect(ci) is { } r ? r.BottomLeft : new Point(20, 20));
                Host?.ItemContextRequested(this, Selection.Caret, at, false);
                e.Handled = true;
                return;
            }
        }

        if (target < 0 || target >= items.Count) return;
        var item = items[target];
        if (shift) Selection.SelectRange(item, ctrl);
        else if (ctrl) Selection.SetCaret(item);
        else Selection.SelectOnly(item);
        BringIntoView(item);
        e.Handled = true;
    }

    /// <summary>Type-ahead: typing a name's first letters jumps to it; repeating one letter cycles through matches.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || Listing is null || Selection is null) return;
        if (_renameBox is not null) return;
        var now = Environment.TickCount64;
        if (now - _typeTime > 900) _typeBuffer = "";
        _typeTime = now;
        if (_typeBuffer.Length == 0 && string.IsNullOrWhiteSpace(e.Text)) return;
        if (e.Text.Any(char.IsControl)) return;
        _typeBuffer += e.Text;

        var items = Listing.Items;
        if (items.Count == 0) return;
        var start = Selection.Caret is { } c ? Math.Max(0, items.IndexOf(c)) : 0;
        var first = char.ToLowerInvariant(_typeBuffer[0]);
        var cycling = _typeBuffer.All(ch => char.ToLowerInvariant(ch) == first);
        var needle = cycling ? _typeBuffer[..1] : _typeBuffer;
        var from = cycling ? start + 1 : start;
        for (var k = 0; k < items.Count; k++)
        {
            var idx = (from + k) % items.Count;
            if (!items[idx].Name.StartsWith(needle, StringComparison.CurrentCultureIgnoreCase)) continue;
            Selection.SelectOnly(items[idx]);
            BringIntoView(items[idx]);
            break;
        }
        e.Handled = true;
    }

    // ---- Drag and drop -------------------------------------------------------------------------------------------------

    private async Task StartDragAsync(PointerPressedEventArgs press, List<FileItem> items)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(PathsFormat, items.Select(i => i.Path).ToArray()));
            foreach (var item in items)
            {
                IStorageItem? s = item.IsDirectory
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(item.Path)
                    : await top.StorageProvider.TryGetFileFromPathAsync(item.Path);
                if (s is not null) transfer.Add(DataTransferItem.CreateFile(s));
            }
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            CrashLog.Write("drag", ex);
        }
    }

    public static IReadOnlyList<string> PathsFrom(IDataTransfer data)
    {
        var own = data.TryGetValue(PathsFormat);
        if (own is { Length: > 0 }) return own;
        var files = data.TryGetFiles();
        if (files is null) return [];
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
    }

    /// <summary>Explorer's rules: Ctrl copies, Shift moves, otherwise move within a drive and copy across drives.</summary>
    public static DragDropEffects ChooseEffect(KeyModifiers mods, IReadOnlyList<string> paths, string target, DragDropEffects allowed)
    {
        if (paths.Count == 0) return DragDropEffects.None;
        DragDropEffects wanted;
        if ((mods & KeyModifiers.Control) != 0) wanted = DragDropEffects.Copy;
        else if ((mods & KeyModifiers.Shift) != 0) wanted = DragDropEffects.Move;
        else wanted = PathUtil.SameVolume(paths[0], target) ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((allowed & wanted) == 0) wanted = (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : allowed & DragDropEffects.Move;
        // Moving something into the folder it is already in does nothing; dropping a folder into itself is impossible.
        if (paths.All(p => PathUtil.Same(Path.GetDirectoryName(p), target)) && wanted == DragDropEffects.Move) return DragDropEffects.None;
        if (paths.Any(p => PathUtil.IsSameOrInside(p, target))) return DragDropEffects.None;
        return wanted;
    }

    private (string? Folder, FileItem? Item) DropTarget(DragEventArgs e, IReadOnlyList<string> paths)
    {
        var item = ItemAt(e.GetPosition(PositionRoot));
        if (item is { IsDirectory: true } && !paths.Any(p => PathUtil.Same(p, item.Path)))
            return (item.Path, item);
        var folder = Listing?.Path;
        return (folder is null || PathUtil.IsVirtual(folder) ? null : folder, null);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var paths = PathsFrom(e.DataTransfer);
        var (folder, item) = DropTarget(e, paths);
        if (folder is null)
        {
            e.DragEffects = DragDropEffects.None;
            SetDropHighlight(null);
            return;
        }
        e.DragEffects = ChooseEffect(e.KeyModifiers, paths, folder, e.DragEffects);
        SetDropHighlight(e.DragEffects == DragDropEffects.None ? null : item);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        var paths = PathsFrom(e.DataTransfer);
        var (folder, _) = DropTarget(e, paths);
        if (folder is null || Host is null) return;
        var effect = ChooseEffect(e.KeyModifiers, paths, folder, e.DragEffects);
        e.DragEffects = effect;
        e.Handled = true;
        if (effect == DragDropEffects.None) return;
        try
        {
            await Host.DropAsync(this, folder, paths, effect);
        }
        catch (Exception ex)
        {
            CrashLog.Write("drop", ex);
        }
    }

    private void SetDropHighlight(FileItem? item)
    {
        if (_dropHighlight == item) return;
        _dropHighlight = item;
        DropHighlight = item;
        InvalidateRows();
    }

    /// <summary>The folder row currently under a drag, drawn highlighted.</summary>
    public FileItem? DropHighlight { get; private set; }

    // ---- Inline rename -------------------------------------------------------------------------------------------------

    public bool IsRenaming => _renameBox is not null;

    public void BeginRename(FileItem item)
    {
        CancelRename();
        BringIntoView(item);
        // Wait for the scroll to realize the row before measuring where its name sits.
        Dispatcher.UIThread.Post(() => ShowRenameBox(item), DispatcherPriority.Background);
    }

    private void ShowRenameBox(FileItem item)
    {
        if (NameRect(item) is not { } rect) return;
        _renaming = item;
        var box = new TextBox
        {
            Text = item.Name,
            FontSize = 13,
            Height = Math.Max(22, rect.Height),
            MinWidth = Math.Max(140, rect.Width),
            MaxWidth = Math.Max(240, Bounds.Width - rect.X - 12),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        box.Classes.Add("inline");
        Canvas.SetLeft(box, rect.X - 4);
        Canvas.SetTop(box, rect.Y);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = CommitRenameAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRename();
                Focus();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) =>
        {
            if (_renameBox == box) _ = CommitRenameAsync();
        };
        _renameBox = box;
        Overlay.Children.Add(box);
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            // Select the name without its extension, so typing replaces just the name.
            var dot = item.IsDirectory ? -1 : item.Name.LastIndexOf('.');
            box.SelectionStart = 0;
            box.SelectionEnd = dot > 0 ? dot : item.Name.Length;
        }, DispatcherPriority.Input);
    }

    /// <summary>Keeps the rename box over its row while the list scrolls; commits once the row is gone.</summary>
    protected void RepositionRenameBox()
    {
        if (_renameBox is null || _renaming is null) return;
        if (NameRect(_renaming) is { } r)
        {
            Canvas.SetLeft(_renameBox, r.X - 4);
            Canvas.SetTop(_renameBox, r.Y);
        }
        else _ = CommitRenameAsync();
    }

    private async Task CommitRenameAsync()
    {
        if (_committing || _renameBox is null || _renaming is null || Host is null) return;
        var box = _renameBox;
        var item = _renaming;
        var text = (box.Text ?? "").Trim();
        if (text.Length == 0 || text == item.Name)
        {
            CancelRename();
            return;
        }
        _committing = true;
        try
        {
            var ok = await Host.CommitRenameAsync(item, text);
            if (ok)
            {
                CancelRename();
                Focus();
            }
            else if (_renameBox == box)
            {
                box.Focus();
            }
        }
        finally
        {
            _committing = false;
        }
    }

    public void CancelRename()
    {
        if (_renameBox is null) return;
        var box = _renameBox;
        _renameBox = null;
        _renaming = null;
        Overlay.Children.Remove(box);
    }
}
