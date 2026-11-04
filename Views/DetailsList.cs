using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>
/// Column geometry for the details list. The name column takes whatever room is left; on narrow panes the type and
/// date columns drop out instead of forcing a horizontal scrollbar.
/// </summary>
public sealed class ColumnLayout
{
    public const double Pad = 6;
    public double Date { get; set; } = AppSettings.Current.DateColumnWidth;
    public double Type { get; set; } = AppSettings.Current.TypeColumnWidth;
    public double Size { get; set; } = AppSettings.Current.SizeColumnWidth;
    public double Location { get; set; } = 220;
    public bool ShowLocation { get; set; }
    /// <summary>"This PC": no dates, and a wide column for "412 GB free of 1.8 TB".</summary>
    public bool Computer { get; set; }

    public bool DateVisible { get; private set; } = true;
    public bool TypeVisible { get; private set; } = true;
    public double NameWidth { get; private set; } = 300;
    public double Width { get; private set; }
    public double SizeWidth => Computer ? 190 : Size;

    public event Action? Changed;

    public void Arrange(double width)
    {
        Width = width;
        var avail = width - Pad * 2;
        _locationWidth = Math.Min(Location, Math.Max(110, avail * 0.38));
        var middle = ShowLocation ? _locationWidth : Type;
        var size = SizeWidth;
        DateVisible = !Computer;
        TypeVisible = true;
        if (ShowLocation)
        {
            // Search results: where a hit lives matters more than when it changed.
            if (avail - (Date + middle + size) < 200) DateVisible = false;
            if (avail - (DateVisible ? Date : 0) - middle - size < 140) TypeVisible = false;
        }
        else
        {
            // Names matter most, then dates, then types.
            if (avail - ((DateVisible ? Date : 0) + middle + size) < 230) TypeVisible = false;
            if (DateVisible && avail - (Date + size) - (TypeVisible ? middle : 0) < 150) DateVisible = false;
        }
        NameWidth = Math.Max(120, avail - size - (DateVisible ? Date : 0) - (TypeVisible ? middle : 0));
        Changed?.Invoke();
    }

    public void Raise() => Arrange(Width);

    public double NameX => Pad;
    public double DateX => Pad + NameWidth;
    public double MiddleX => DateX + (DateVisible ? Date : 0);
    public double MiddleWidth => TypeVisible ? (ShowLocation ? _locationWidth : Type) : 0;
    private double _locationWidth = 220;
    public double SizeX => MiddleX + MiddleWidth;
}

/// <summary>The classic details view: icon, name, date, type and size (with proportional size bars).</summary>
public sealed class DetailsList : ItemViewBase
{
    private readonly ScrollViewer _scroll;
    private readonly ItemsControl _items;
    private readonly DetailsHeader? _header;

    public ColumnLayout Layout { get; } = new();

    /// <summary>Miller-column mode: names only, a chevron on folders, no header.</summary>
    public bool ColumnMode { get; }

    public double RowHeight => ColumnMode ? 26 : AppSettings.Current.CompactRows ? 24 : 28;

    public event Action<SortKey>? SortRequested;

    public DetailsList(bool columnMode = false)
    {
        ColumnMode = columnMode;
        _items = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<FileItem>((_, _) => new DetailsRow(this), supportsRecycling: true),
            Padding = new Thickness(0, 2, 0, 8),
        };
        _scroll = new ScrollViewer
        {
            Content = _items,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            // A transparent background makes the empty area below the rows clickable (rubber band, context menu).
            Background = Brushes.Transparent,
        };
        _scroll.ScrollChanged += (_, _) => RepositionRenameBox();
        _scroll.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - Layout.Width) > 0.5) Layout.Arrange(e.NewSize.Width);
        };

        if (!columnMode)
        {
            _header = new DetailsHeader(this);
            Root.RowDefinitions = new RowDefinitions("Auto,*");
            Root.Children.Add(_header);
            Grid.SetRow(_scroll, 1);
            Grid.SetRow(Overlay, 1);
        }
        Root.Children.Add(_scroll);
        Root.Children.Add(Overlay);

        Layout.Changed += () =>
        {
            _header?.InvalidateVisual();
            InvalidateRows();
        };
    }

    public override void Attach(FolderListing listing, SelectionModel selection)
    {
        base.Attach(listing, selection);
        _items.ItemsSource = listing.Items;
        if (Layout.Computer != listing.IsComputer)
        {
            Layout.Computer = listing.IsComputer;
            Layout.Raise();
        }
        UpdateSortGlyph(listing.Sort);
    }

    public override void Detach()
    {
        _items.ItemsSource = null;
        base.Detach();
    }

    public SortSpec? CurrentSort { get; private set; }

    public void UpdateSortGlyph(SortSpec sort)
    {
        CurrentSort = sort;
        _header?.InvalidateVisual();
    }

    internal void RequestSort(SortKey key) => SortRequested?.Invoke(key);

    public void ShowLocationColumn(bool show)
    {
        Layout.ShowLocation = show;
        Layout.Raise();
    }

    protected override Vector ScrollOffset
    {
        get => _scroll.Offset;
        set => _scroll.Offset = value;
    }

    protected override Size Viewport => _scroll.Viewport;

    protected override Visual PositionRoot => _scroll;

    protected override int PageStep => Math.Max(1, (int)(_scroll.Viewport.Height / RowHeight) - 1);

    protected override FileItem? ItemAt(Point p)
    {
        return (_scroll.InputHitTest(p) as Visual)?.FindAncestorOfType<DetailsRow>(includeSelf: true)?.Item;
    }

    protected override Rect? NameRect(FileItem item)
    {
        if (_items.ContainerFromItem(item) is not { } container) return null;
        var row = container.GetVisualDescendants().OfType<DetailsRow>().FirstOrDefault();
        if (row?.TranslatePoint(new Point(0, 0), _scroll) is not { } origin) return null;
        if (origin.Y < -RowHeight / 2 || origin.Y > _scroll.Bounds.Height - RowHeight / 2) return null;
        var x = ColumnMode ? DetailsRow.TextLeft : Layout.NameX + DetailsRow.TextLeft;
        var width = ColumnMode ? _scroll.Bounds.Width - DetailsRow.TextLeft - 24 : Layout.NameWidth - DetailsRow.TextLeft - 4;
        return new Rect(x, origin.Y + 2, Math.Max(80, width), RowHeight - 4);
    }

    protected override void SelectInBand(Rect content, IReadOnlyCollection<FileItem>? keep)
    {
        if (Selection is null || Listing is null) return;
        var top = 2.0;
        var first = (int)Math.Floor((content.Top - top) / RowHeight);
        var last = (int)Math.Floor((content.Bottom - top) / RowHeight);
        if (last < 0 || first >= Listing.Items.Count)
        {
            Selection.SelectIndexRange(0, -1, keep);
            return;
        }
        Selection.SelectIndexRange(Math.Max(0, first), Math.Min(Listing.Items.Count - 1, last), keep);
    }

    public override void BringIntoView(FileItem item)
    {
        if (Listing is null) return;
        var index = Listing.Items.IndexOf(item);
        if (index >= 0) _items.ScrollIntoView(index);
    }

    public override void InvalidateRows()
    {
        foreach (var c in _items.GetRealizedContainers())
            foreach (var row in c.GetVisualDescendants().OfType<DetailsRow>())
                row.InvalidateVisual();
    }

    public void ScrollToTop() => _scroll.Offset = default;
}

/// <summary>One custom-drawn row. Rows are recycled as the list scrolls, so all state comes from the bound item.</summary>
public sealed class DetailsRow : Control
{
    public const double IconLeft = 20;
    public const double TextLeft = 44;

    private readonly DetailsList _owner;
    private FileItem? _item;
    private FormattedText? _name, _date, _type, _size;
    private bool _dirty = true;

    public DetailsRow(DetailsList owner)
    {
        _owner = owner;
        ClipToBounds = true;
    }

    public FileItem? Item => _item;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = DataContext as FileItem;
        if (_item is not null)
        {
            _item.PropertyChanged += OnItemChanged;
            RequestIcon();
        }
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner.Layout.Changed += OnLayoutChanged;
        RequestIcon();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _owner.Layout.Changed -= OnLayoutChanged;
    }

    private void RequestIcon()
    {
        if (_item is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        IconService.RequestIcon(_item, 16, scale);
    }

    private void OnLayoutChanged()
    {
        _dirty = true;
        InvalidateVisual();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FileItem.IsSelected) or nameof(FileItem.IsCaret) or nameof(FileItem.Icon) or nameof(FileItem.SizeRatio) or nameof(FileItem.IsCut):
                break;
            default:
                _dirty = true;
                break;
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, _owner.RowHeight);

    protected override void OnPointerEntered(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        InvalidateVisual();
    }

    private void Build(FileItem item)
    {
        var L = _owner.Layout;
        var faded = item.IsHidden;
        var nameBrush = faded ? Skin.Muted : Skin.Text;
        var gitSpace = item.Git != '\0' ? 16 : 0;

        if (_owner.ColumnMode)
        {
            _name = Skin.Text1(item.Name, 13, nameBrush, Math.Max(10, Bounds.Width - TextLeft - 22));
            _date = _type = _size = null;
            return;
        }

        _name = Skin.Text1(item.Name, 13, nameBrush, Math.Max(10, L.NameWidth - TextLeft - 6 - gitSpace), item.IsDrive ? Skin.SemiBold : null);
        _date = L.DateVisible && !item.IsDrive
            ? Skin.Text1(Formatting.Date(item.Modified, AppSettings.Current.RelativeDates), 12, Skin.Muted, L.Date - 12)
            : null;
        var middle = L.ShowLocation ? item.Location ?? "" : item.TypeName ?? FileKinds.GenericTypeName(item);
        _type = L.TypeVisible ? Skin.Text1(middle, 12, Skin.Muted, L.MiddleWidth - 12) : null;

        string sizeText = item.Kind switch
        {
            ItemKind.File => Formatting.Size(item.Size),
            ItemKind.Drive => Formatting.DriveSpace(item),
            _ => item.FolderBytes >= 0 ? Formatting.Size(item.FolderBytes) + (item.FolderSizePartial ? "…" : "") : item.FolderSizePartial ? "…" : "",
        };
        _size = Skin.Text1(sizeText, 12, item.Kind == ItemKind.Folder ? Skin.Muted : Skin.Text, L.SizeWidth - 14, align: TextAlignment.Right);
    }

    public override void Render(DrawingContext ctx)
    {
        var item = _item;
        var w = Bounds.Width;
        var h = Bounds.Height;
        // Always paint something across the row so the whole row is hit-testable.
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        if (item is null) return;
        if (_dirty)
        {
            Build(item);
            _dirty = false;
        }

        var rowRect = new Rect(4, 1, Math.Max(0, w - 8), h - 2);
        var drop = _owner.DropHighlight == item;
        IBrush? bg = drop ? Skin.RowDrop
            : item.IsSelected ? (_owner.IsActiveList ? Skin.RowSelected : Skin.RowSelectedInactive)
            : IsPointerOver ? Skin.RowHover : null;
        if (bg is not null) ctx.DrawRectangle(bg, drop ? Skin.DropPen : null, rowRect, 5, 5);
        if (item.IsSelected && _owner.IsActiveList)
            ctx.FillRectangle(Skin.Accent, new Rect(4, 6, 2.5, h - 12), 1.25f);
        if (item.IsCaret && _owner.IsActiveList && !item.IsSelected)
            ctx.DrawRectangle(null, Skin.CaretPen, rowRect.Deflate(0.5), 5, 5);

        using var cut = ctx.PushOpacity(item.IsCut ? 0.45 : 1);

        if (item.Label > 0)
            ctx.DrawEllipse(Skin.Label(item.Label), null, new Point(12, h / 2), 3.5, 3.5);

        if (item.Icon is { } icon)
        {
            using var faded = ctx.PushOpacity(item.IsHidden ? 0.55 : 1);
            ctx.DrawImage(icon, new Rect(IconLeft, Math.Round((h - 16) / 2), 16, 16));
        }

        var L = _owner.Layout;
        if (_name is not null)
            ctx.DrawText(_name, new Point(TextLeft, (h - _name.Height) / 2));

        if (_owner.ColumnMode)
        {
            if (item.IsDirectory)
            {
                var x = w - 16;
                var pen = item.IsSelected ? Skin.ChevronPenSelected : Skin.ChevronPen;
                ctx.DrawLine(pen, new Point(x, h / 2 - 4), new Point(x + 4, h / 2));
                ctx.DrawLine(pen, new Point(x + 4, h / 2), new Point(x, h / 2 + 4));
            }
            return;
        }

        if (item.Git != '\0')
        {
            var mark = Skin.Text1(item.Git.ToString(), 11, Skin.Git(item.Git), 14, Skin.SemiBold);
            ctx.DrawText(mark, new Point(L.NameX + L.NameWidth - 18, (h - mark.Height) / 2));
        }

        if (_date is not null)
            ctx.DrawText(_date, new Point(L.DateX + 6, (h - _date.Height) / 2));
        if (_type is not null)
            ctx.DrawText(_type, new Point(L.MiddleX + 6, (h - _type.Height) / 2));

        var sx = L.SizeX;
        if (AppSettings.Current.ShowSizeBars && item.SizeRatio > 0)
        {
            var barW = Math.Max(0, L.SizeWidth - 16);
            var y = h - 6;
            ctx.FillRectangle(Skin.Track, new Rect(sx + 6, y, barW, 2.5), 1.25f);
            var fill = item.IsDrive
                ? Skin.DriveBar(item)
                : item.FolderSizePartial ? Skin.SizeBarPartial : Skin.SizeBar;
            ctx.FillRectangle(fill, new Rect(sx + 6, y, Math.Max(2, barW * Math.Min(1, item.SizeRatio)), 2.5), 1.25f);
        }
        if (_size is not null)
            ctx.DrawText(_size, new Point(sx + 4, (h - _size.Height) / 2 - (AppSettings.Current.ShowSizeBars ? 1.5 : 0)));
    }
}

/// <summary>Column titles: click to sort (again to reverse), drag a column's left edge to resize it.</summary>
public sealed class DetailsHeader : Control
{
    private readonly DetailsList _owner;
    private int _dragging = -1;
    private double _dragStartX;
    private double _dragStartWidth;
    private int _hover = -1;

    public DetailsHeader(DetailsList owner)
    {
        _owner = owner;
        Height = 28;
        Cursor = Cursor.Default;
    }

    private IEnumerable<(int Id, string Title, double X, double W, SortKey? Key, bool Right)> Columns()
    {
        var L = _owner.Layout;
        yield return (0, "Name", L.NameX, L.NameWidth, SortKey.Name, false);
        if (L.DateVisible) yield return (1, "Date modified", L.DateX, L.Date, SortKey.Modified, false);
        if (L.TypeVisible) yield return (2, L.ShowLocation ? "Folder" : "Type", L.MiddleX, L.MiddleWidth, L.ShowLocation ? null : SortKey.Type, false);
        yield return (3, L.Computer ? "Free space" : "Size", L.SizeX, L.SizeWidth, SortKey.Size, true);
    }

    public override void Render(DrawingContext ctx)
    {
        var h = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        ctx.DrawLine(Skin.DividerPen, new Point(0, h - 0.5), new Point(Bounds.Width, h - 0.5));
        var sort = _owner.CurrentSort;
        foreach (var c in Columns())
        {
            var active = sort is not null && c.Key == sort.Key;
            var brush = active ? Skin.Text : c.Id == _hover ? Skin.Text : Skin.Muted;
            var ft = Skin.Text1(c.Title, 11.5, brush, c.W - 24, Skin.SemiBold, c.Right ? TextAlignment.Right : TextAlignment.Left);
            var tx = c.Right ? c.X + 4 : c.Id == 0 ? c.X + DetailsRow.TextLeft - 6 : c.X + 6;
            ctx.DrawText(ft, new Point(tx, (h - ft.Height) / 2));
            if (active)
            {
                var ax = c.Right ? c.X + c.W - ft.Width - 16 : tx + ft.Width + 6;
                var ay = h / 2;
                var down = sort!.Descending;
                var pen = Skin.ChevronPenSelected;
                ctx.DrawLine(pen, new Point(ax, ay + (down ? -2 : 2)), new Point(ax + 3.5, ay + (down ? 2 : -2)));
                ctx.DrawLine(pen, new Point(ax + 3.5, ay + (down ? 2 : -2)), new Point(ax + 7, ay + (down ? -2 : 2)));
            }
            if (c.Id > 0)
                ctx.DrawLine(Skin.DividerPen, new Point(c.X + 0.5, 7), new Point(c.X + 0.5, h - 7));
        }
    }

    private int EdgeAt(double x)
    {
        foreach (var c in Columns())
            if (c.Id > 0 && Math.Abs(x - c.X) <= 4) return c.Id;
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (_dragging > 0)
        {
            // Dragging a column's left edge to the left widens it (the name column gives up the room).
            var w = Math.Clamp(_dragStartWidth + (_dragStartX - x), 60, 600);
            var L = _owner.Layout;
            switch (_dragging)
            {
                case 1: L.Date = w; AppSettings.Current.DateColumnWidth = w; break;
                case 2 when L.ShowLocation: L.Location = w; break;
                case 2: L.Type = w; AppSettings.Current.TypeColumnWidth = w; break;
                case 3: L.Size = w; AppSettings.Current.SizeColumnWidth = w; break;
            }
            L.Raise();
            return;
        }
        Cursor = EdgeAt(x) > 0 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        var hover = Columns().FirstOrDefault(c => x >= c.X && x < c.X + c.W).Id;
        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var x = e.GetPosition(this).X;
        var edge = EdgeAt(x);
        if (edge > 0)
        {
            _dragging = edge;
            _dragStartX = x;
            var L = _owner.Layout;
            _dragStartWidth = edge switch { 1 => L.Date, 2 => L.MiddleWidth, _ => L.SizeWidth };
            e.Pointer.Capture(this);
        }
        // Header clicks must not reach the list underneath (it would clear the selection or start a band).
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled = true;
        if (_dragging > 0)
        {
            _dragging = -1;
            e.Pointer.Capture(null);
            AppSettings.SaveSoon();
            return;
        }
        var x = e.GetPosition(this).X;
        var col = Columns().FirstOrDefault(c => x >= c.X && x < c.X + c.W);
        if (col.Key is { } key && e.InitialPressMouseButton == MouseButton.Left)
            _owner.RequestSort(key);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 28);
}
