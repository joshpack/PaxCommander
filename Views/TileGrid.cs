using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>A run of tiles that share one row of the grid.</summary>
public sealed record TileRowData(int Start, int Count, int Row);

/// <summary>
/// Thumbnail grid. Avalonia's wrap panel doesn't virtualize, so the grid is a virtualized list of rows, each row a
/// custom-drawn strip of tiles — smooth even in folders with tens of thousands of photos. Ctrl+wheel resizes tiles.
/// </summary>
public sealed class TileGrid : ItemViewBase
{
    private const double PadX = 10;
    private const double PadTop = 6;
    private readonly ScrollViewer _scroll;
    private readonly ItemsControl _rows;
    private int _columns = 1;
    private bool _rebuildQueued;
    private ItemList? _observed;

    public TileGrid()
    {
        _rows = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<TileRowData>((_, _) => new TileRow(this), supportsRecycling: true),
            Padding = new Thickness(0, PadTop, 0, 12),
        };
        _scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
        };
        _scroll.ScrollChanged += (_, _) => RepositionRenameBox();
        _scroll.SizeChanged += (_, _) => QueueRebuild();
        Root.Children.Add(_scroll);
        Root.Children.Add(Overlay);
    }

    public double TileSize => Math.Clamp(AppSettings.Current.TileSize, 64, 320);
    public double TileWidth => TileSize + 28;
    public double TileHeight => TileSize + 48;
    public int Columns => _columns;

    public override void Attach(FolderListing listing, SelectionModel selection)
    {
        base.Attach(listing, selection);
        if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = listing.Items;
        _observed.CollectionChanged += OnItemsChanged;
        QueueRebuild();
    }

    public override void Detach()
    {
        if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = null;
        _rows.ItemsSource = null;
        base.Detach();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, DispatcherPriority.Render);
    }

    private void Rebuild()
    {
        if (Listing is null) return;
        var width = _scroll.Bounds.Width;
        _columns = Math.Max(1, (int)((width - PadX * 2) / TileWidth));
        var count = Listing.Items.Count;
        var rows = new List<TileRowData>((count + _columns - 1) / _columns);
        for (int start = 0, r = 0; start < count; start += _columns, r++)
            rows.Add(new TileRowData(start, Math.Min(_columns, count - start), r));
        _rows.ItemsSource = rows;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            AppSettings.Current.TileSize = (int)Math.Clamp(TileSize + (e.Delta.Y > 0 ? 16 : -16), 64, 320);
            AppSettings.SaveSoon();
            QueueRebuild();
            InvalidateRows();
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    protected override int Step => _columns;
    protected override int PageStep => _columns * Math.Max(1, (int)(_scroll.Viewport.Height / TileHeight) - 1);
    protected override Vector ScrollOffset
    {
        get => _scroll.Offset;
        set => _scroll.Offset = value;
    }
    protected override Size Viewport => _scroll.Viewport;
    protected override Visual PositionRoot => _scroll;

    internal double ColumnX(int column) => PadX + column * TileWidth;

    protected override FileItem? ItemAt(Point p)
    {
        if (Listing is null) return null;
        var row = (_scroll.InputHitTest(p) as Visual)?.FindAncestorOfType<TileRow>(includeSelf: true);
        if (row?.Data is not { } data || row.TranslatePoint(new Point(0, 0), _scroll) is not { } origin) return null;
        var x = p.X - origin.X - PadX;
        var col = (int)Math.Floor(x / TileWidth);
        if (x < 0 || col >= data.Count) return null;
        var inTile = x - col * TileWidth;
        if (inTile < 4 || inTile > TileWidth - 4) return null;
        var index = data.Start + col;
        return index < Listing.Items.Count ? Listing.Items[index] : null;
    }

    protected override Rect? NameRect(FileItem item)
    {
        if (Listing is null) return null;
        var index = Listing.Items.IndexOf(item);
        if (index < 0) return null;
        var row = index / _columns;
        var col = index % _columns;
        foreach (var c in _rows.GetRealizedContainers())
        {
            var tr = c.GetVisualDescendants().OfType<TileRow>().FirstOrDefault();
            if (tr?.Data?.Row != row || tr.TranslatePoint(new Point(0, 0), _scroll) is not { } origin) continue;
            return new Rect(origin.X + ColumnX(col) + 4, origin.Y + TileSize + 14, TileWidth - 8, 24);
        }
        return null;
    }

    protected override void SelectInBand(Rect content, IReadOnlyCollection<FileItem>? keep)
    {
        if (Selection is null || Listing is null) return;
        var items = Listing.Items;
        var r0 = (int)Math.Floor((content.Top - PadTop) / TileHeight);
        var r1 = (int)Math.Floor((content.Bottom - PadTop) / TileHeight);
        var c0 = (int)Math.Floor((content.Left - PadX) / TileWidth);
        var c1 = (int)Math.Floor((content.Right - PadX) / TileWidth);
        c0 = Math.Max(0, c0);
        c1 = Math.Min(_columns - 1, c1);
        var picked = new List<FileItem>();
        for (var r = Math.Max(0, r0); r <= r1; r++)
            for (var c = c0; c <= c1; c++)
            {
                var i = r * _columns + c;
                if (i >= 0 && i < items.Count) picked.Add(items[i]);
            }
        Selection.Clear(notify: false);
        if (keep is not null) Selection.Add(keep);
        Selection.Add(picked);
    }

    public override void BringIntoView(FileItem item)
    {
        if (Listing is null) return;
        var index = Listing.Items.IndexOf(item);
        if (index >= 0) _rows.ScrollIntoView(index / _columns);
    }

    public override void InvalidateRows()
    {
        foreach (var c in _rows.GetRealizedContainers())
            foreach (var row in c.GetVisualDescendants().OfType<TileRow>())
            {
                row.InvalidateMeasure();
                row.InvalidateVisual();
            }
    }
}

/// <summary>Draws one row of tiles: thumbnail (or big icon), name below, selection and label marks.</summary>
public sealed class TileRow : Control
{
    private readonly TileGrid _owner;
    private TileRowData? _data;
    private readonly List<FileItem> _watched = [];
    private int _hover = -1;

    public TileRow(TileGrid owner) => _owner = owner;

    public TileRowData? Data => _data;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _data = DataContext as TileRowData;
        Rewatch();
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rewatch();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var i in _watched) i.PropertyChanged -= OnItemChanged;
        _watched.Clear();
    }

    private void Rewatch()
    {
        foreach (var i in _watched) i.PropertyChanged -= OnItemChanged;
        _watched.Clear();
        if (_data is null || _owner.Listing is null || VisualRoot is null) return;
        var items = _owner.Listing.Items;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var px = (int)(_owner.TileSize * scale);
        for (var k = 0; k < _data.Count && _data.Start + k < items.Count; k++)
        {
            var item = items[_data.Start + k];
            _watched.Add(item);
            item.PropertyChanged += OnItemChanged;
            IconService.RequestIcon(item, Math.Min(_owner.TileSize, 96), scale);
            if (item.Kind == ItemKind.File)
                IconService.RequestThumbnail(item, px);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, _owner.TileHeight);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X - _owner.ColumnX(0);
        var col = x < 0 ? -1 : (int)(x / _owner.TileWidth);
        if (col != _hover)
        {
            _hover = col;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_data is null || _owner.Listing is null) return;
        var items = _owner.Listing.Items;
        var size = _owner.TileSize;
        var tw = _owner.TileWidth;
        var th = _owner.TileHeight;
        for (var k = 0; k < _data.Count && _data.Start + k < items.Count; k++)
        {
            var item = items[_data.Start + k];
            var x = _owner.ColumnX(k);
            var tile = new Rect(x + 3, 2, tw - 6, th - 4);
            var drop = _owner.DropHighlight == item;
            IBrush? bg = drop ? Skin.RowDrop
                : item.IsSelected ? (_owner.IsActiveList ? Skin.RowSelected : Skin.RowSelectedInactive)
                : k == _hover ? Skin.RowHover : null;
            if (bg is not null) ctx.DrawRectangle(bg, drop ? Skin.DropPen : null, tile, 8, 8);
            if (item.IsSelected && _owner.IsActiveList)
                ctx.DrawRectangle(null, Skin.SelectedTilePen, tile.Deflate(0.6), 8, 8);
            else if (item.IsCaret && _owner.IsActiveList)
                ctx.DrawRectangle(null, Skin.CaretPen, tile.Deflate(0.5), 8, 8);

            using var cut = ctx.PushOpacity(item.IsCut ? 0.45 : item.IsHidden ? 0.7 : 1);
            var box = new Rect(x + (tw - size) / 2, 8, size, size);
            if (item.Thumbnail is { } thumb)
            {
                var fit = Fit(thumb.Size, box);
                using (ctx.PushClip(new RoundedRect(fit, 6)))
                    ctx.DrawImage(thumb, fit);
            }
            else if (item.Icon is { } icon)
            {
                // Shell icons top out at 256 px but look best no bigger than ~70% of the tile.
                var s = Math.Min(size * 0.72, Math.Max(32, icon.Size.Width >= 48 ? size * 0.72 : 48));
                ctx.DrawImage(icon, new Rect(box.Center.X - s / 2, box.Center.Y - s / 2, s, s));
            }

            if (item.Label > 0)
                ctx.DrawEllipse(Skin.Label(item.Label), null, new Point(x + 14, 14), 4, 4);

            var name = Skin.Text1(item.Name, 12, item.IsSelected ? Skin.Text : Skin.TileText, tw - 12, align: TextAlignment.Center);
            name.MaxLineCount = 2;
            name.MaxTextHeight = 34;
            ctx.DrawText(name, new Point(x + 6, size + 14));

            if (item.IsDrive && item.Size > 0)
            {
                var bar = new Rect(x + 12, th - 10, tw - 24, 3);
                ctx.FillRectangle(Skin.Track, bar, 1.5f);
                ctx.FillRectangle(Skin.DriveBar(item), bar.WithWidth(Math.Max(2, bar.Width * item.SizeRatio)), 1.5f);
            }
        }
    }

    private static Rect Fit(Size image, Rect box)
    {
        if (image.Width <= 0 || image.Height <= 0) return box;
        var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        return new Rect(box.X + (box.Width - w) / 2, box.Bottom - h, w, h);
    }
}
