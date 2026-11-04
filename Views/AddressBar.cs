using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;
using PaxCommander.Search;

namespace PaxCommander.Views;

/// <summary>Lays crumbs out from the right; whatever doesn't fit collapses into the leading overflow button.</summary>
public sealed class CrumbPanel : Panel
{
    public CrumbPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 0, h = 0;
        foreach (var c in Children)
        {
            c.Measure(Size.Infinity);
            w += c.DesiredSize.Width;
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(Math.Min(w, double.IsInfinity(availableSize.Width) ? w : availableSize.Width), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        var overflow = Children[0];
        var room = finalSize.Width - overflow.DesiredSize.Width;
        var visibleFrom = Children.Count;
        double used = 0;
        for (var i = Children.Count - 1; i >= 1; i--)
        {
            var w = Children[i].DesiredSize.Width;
            if (used + w > room && i < Children.Count - 2) break;
            used += w;
            visibleFrom = i;
        }
        // Children are [overflow, spacer, crumb, chevron, crumb, …]; anything before the spacer's successor is folded.
        var showOverflow = visibleFrom > 2;
        double x = 0;
        overflow.IsVisible = showOverflow;
        if (showOverflow)
        {
            overflow.Arrange(new Rect(0, 0, overflow.DesiredSize.Width, finalSize.Height));
            x = overflow.DesiredSize.Width;
        }
        for (var i = 1; i < Children.Count; i++)
        {
            var c = Children[i];
            if (i < visibleFrom)
            {
                // Parked out of sight: a zero-size arrange would still let the button paint its glyph.
                c.Arrange(new Rect(-10_000, 0, c.DesiredSize.Width, finalSize.Height));
                continue;
            }
            c.Arrange(new Rect(x, 0, c.DesiredSize.Width, finalSize.Height));
            x += c.DesiredSize.Width;
        }
        HiddenCount = (visibleFrom - 1) / 2;
        return finalSize;
    }

    /// <summary>How many crumbs are folded into the overflow button.</summary>
    public int HiddenCount { get; private set; }
}

/// <summary>
/// The path bar. Shows breadcrumbs (click to go there, click a chevron for that folder's subfolders, drop files on a
/// crumb to move/copy them there); click the empty part or press Ctrl+L to type a path, with folder autocomplete.
/// </summary>
public sealed class AddressBar : UserControl
{
    private readonly Border _frame;
    private readonly CrumbPanel _crumbs = new();
    private readonly Border _crumbHost;
    private readonly TextBox _edit;
    private readonly Popup _suggestPopup;
    private readonly ListBox _suggestions;
    private List<Crumb> _trail = [];
    private string _path = "";
    private CancellationTokenSource? _suggestCts;

    public event Action<string>? NavigateRequested;
    public event Action<string, IReadOnlyList<string>, DragDropEffects>? DropOnCrumb;
    public event Action? EditFinished;

    public AddressBar()
    {
        _crumbHost = new Border
        {
            Background = Brushes.Transparent,
            Child = _crumbs,
            Padding = new Thickness(4, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        _crumbHost.PointerPressed += (_, e) =>
        {
            if (e.Source == _crumbHost || e.Source == _crumbs) BeginEdit();
        };
        _edit = new TextBox { IsVisible = false, VerticalContentAlignment = VerticalAlignment.Center };
        _edit.Classes.Add("bare");
        _edit.KeyDown += OnEditKeyDown;
        _edit.LostFocus += (_, _) =>
        {
            if (!_suggestPopup!.IsPointerOverPopup) EndEdit();
        };
        _edit.TextChanged += (_, _) => QueueSuggestions();

        _suggestions = new ListBox { MaxHeight = 320, Classes = { "palette" } };
        _suggestions.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => s is null ? new Panel() : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                Glyphs.Icon("folder", 14, Skin.Muted),
                new TextBlock { Text = Path.GetFileName(s.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : s, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        _suggestions.PointerReleased += (_, _) =>
        {
            if (_suggestions.SelectedItem is string s) Accept(s, navigate: true);
        };
        _suggestPopup = new Popup
        {
            Child = new Border { Classes = { "card" }, Padding = new Thickness(4), Child = _suggestions, MinWidth = 320 },
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
        };

        var grid = new Grid();
        grid.Children.Add(_crumbHost);
        grid.Children.Add(_edit);
        grid.Children.Add(_suggestPopup);
        _frame = new Border
        {
            Background = Skin.Input,
            BorderBrush = Skin.InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Height = 32,
            Child = grid,
            ClipToBounds = true,
        };
        Content = _frame;
    }

    public bool IsEditing => _edit.IsVisible;

    /// <summary>Shows a location; <paramref name="label"/> replaces the crumbs (search results).</summary>
    public void SetPath(string path, string? label = null)
    {
        _path = path;
        _crumbs.Children.Clear();
        _crumbs.Children.Add(MakeOverflowButton());
        if (label is not null)
        {
            _crumbs.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0),
                Foreground = Skin.Info,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _trail = [];
            return;
        }
        _trail = PathUtil.Crumbs(path);
        for (var i = 0; i < _trail.Count; i++)
        {
            var crumb = _trail[i];
            _crumbs.Children.Add(MakeChevron(i == 0 ? null : _trail[i - 1]));
            _crumbs.Children.Add(MakeCrumb(crumb, i == _trail.Count - 1));
        }
        // Children: [overflow, chev, crumb, chev, crumb…]; the first chevron belongs before the first crumb, drop it.
        if (_crumbs.Children.Count > 1) _crumbs.Children.RemoveAt(1);
        _crumbs.Children.Insert(1, new Border { Width = 0 });
    }

    private Button MakeCrumb(Crumb crumb, bool last)
    {
        var b = new Button
        {
            Content = PathUtil.IsComputer(crumb.Path)
                ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Glyphs.Icon("desktop", 14), new TextBlock { Text = crumb.Name } } }
                : crumb.Name,
        };
        b.Classes.Add("crumb");
        if (last) b.Classes.Add("last");
        b.Click += (_, _) => NavigateRequested?.Invoke(crumb.Path);
        ToolTip.SetTip(b, PathUtil.IsComputer(crumb.Path) ? "This PC" : crumb.Path);

        DragDrop.SetAllowDrop(b, true);
        b.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var paths = ItemViewBase.PathsFrom(e.DataTransfer);
            e.DragEffects = PathUtil.IsComputer(crumb.Path) ? DragDropEffects.None : ItemViewBase.ChooseEffect(e.KeyModifiers, paths, crumb.Path, e.DragEffects);
            b.Classes.Set("droptarget", e.DragEffects != DragDropEffects.None);
            e.Handled = true;
        });
        b.AddHandler(DragDrop.DragLeaveEvent, (_, _) => b.Classes.Set("droptarget", false));
        b.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            b.Classes.Set("droptarget", false);
            var paths = ItemViewBase.PathsFrom(e.DataTransfer);
            var effect = ItemViewBase.ChooseEffect(e.KeyModifiers, paths, crumb.Path, e.DragEffects);
            if (effect != DragDropEffects.None && !PathUtil.IsComputer(crumb.Path))
                DropOnCrumb?.Invoke(crumb.Path, paths, effect);
            e.DragEffects = effect;
            e.Handled = true;
        });
        return b;
    }

    private Button MakeChevron(Crumb? parent)
    {
        var b = new Button { Content = Glyphs.Icon("chevron", 12) };
        b.Classes.Add("chev");
        if (parent is not null)
            b.Click += async (_, _) => await ShowSubfolderMenu(b, parent.Path);
        return b;
    }

    private Button MakeOverflowButton()
    {
        var b = new Button { Content = Glyphs.Icon("more", 14), IsVisible = false };
        b.Classes.Add("chev");
        b.Width = 24;
        b.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            for (var i = 0; i < Math.Min(_crumbs.HiddenCount, _trail.Count); i++)
            {
                var c = _trail[i];
                var mi = new MenuItem { Header = c.Name, Icon = Glyphs.Icon(PathUtil.IsComputer(c.Path) ? "desktop" : "folder", 14) };
                mi.Click += (_, _) => NavigateRequested?.Invoke(c.Path);
                menu.Items.Add(mi);
            }
            menu.ShowAt(b);
        };
        return b;
    }

    /// <summary>The chevron after a crumb lists that folder's subfolders, like Explorer.</summary>
    private async Task ShowSubfolderMenu(Control anchor, string folder)
    {
        List<(string Name, string Path)> subs;
        try
        {
            subs = await Task.Run(() =>
            {
                if (PathUtil.IsComputer(folder))
                    return DriveInfo.GetDrives().Select(d => (d.Name.TrimEnd('\\'), d.RootDirectory.FullName)).ToList();
                return new DirectoryInfo(folder)
                    .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = AppSettings.Current.ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System })
                    .Select(d => (d.Name, d.FullName))
                    .OrderBy(d => d.Name, NaturalComparer.Instance)
                    .Take(400)
                    .ToList();
            });
        }
        catch
        {
            return;
        }
        var menu = new MenuFlyout();
        if (subs.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No subfolders", IsEnabled = false });
        var current = _trail.Select(t => t.Path).ToHashSet(PathUtil.Comparer);
        foreach (var (name, path) in subs)
        {
            var mi = new MenuItem { Header = name, Icon = Glyphs.Icon("folder", 14, current.Contains(path) ? Skin.Accent : null) };
            if (current.Contains(path)) mi.FontWeight = FontWeight.SemiBold;
            mi.Click += (_, _) => NavigateRequested?.Invoke(path);
            menu.Items.Add(mi);
        }
        menu.ShowAt(anchor);
    }

    // ---- Edit mode -----------------------------------------------------------------------------------------------------

    public void BeginEdit()
    {
        _crumbHost.IsVisible = false;
        _edit.IsVisible = true;
        _edit.Text = PathUtil.IsComputer(_path) ? "" : _path;
        Dispatcher.UIThread.Post(() =>
        {
            _edit.Focus();
            _edit.SelectAll();
        });
    }

    private void EndEdit()
    {
        if (!_edit.IsVisible) return;
        _suggestCts?.Cancel();
        _suggestPopup.IsOpen = false;
        _edit.IsVisible = false;
        _crumbHost.IsVisible = true;
        EditFinished?.Invoke();
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                EndEdit();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_suggestPopup.IsOpen && _suggestions.SelectedItem is string s)
                    Accept(s, navigate: true);
                else
                {
                    var text = _edit.Text ?? "";
                    EndEdit();
                    NavigateRequested?.Invoke(text);
                }
                e.Handled = true;
                break;
            case Key.Down when _suggestPopup.IsOpen:
                _suggestions.SelectedIndex = Math.Min(_suggestions.ItemCount - 1, _suggestions.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up when _suggestPopup.IsOpen:
                _suggestions.SelectedIndex = Math.Max(0, _suggestions.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Tab when _suggestPopup.IsOpen:
                if (_suggestions.SelectedItem is string t || (_suggestions.ItemCount > 0 && _suggestions.Items[0] is string t0 && (t = t0) is not null))
                    Accept(t, navigate: false);
                e.Handled = true;
                break;
        }
    }

    private void Accept(string folder, bool navigate)
    {
        if (navigate)
        {
            EndEdit();
            NavigateRequested?.Invoke(folder);
        }
        else
        {
            var text = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
            _edit.Text = text;
            _edit.CaretIndex = text.Length;
        }
    }

    private void QueueSuggestions()
    {
        if (!_edit.IsVisible) return;
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        var text = _edit.Text ?? "";
        _ = Task.Run(async () =>
        {
            await Task.Delay(90, cts.Token);
            var list = Suggest(text);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || !_edit.IsVisible) return;
                _suggestions.ItemsSource = list;
                _suggestions.SelectedIndex = -1;
                _suggestPopup.IsOpen = list.Count > 0;
            });
        }, cts.Token);
    }

    /// <summary>Folders that complete what has been typed so far.</summary>
    private static List<string> Suggest(string text)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
            if (expanded.Length < 2) return [];
            var sep = expanded.LastIndexOfAny(['\\', '/']);
            if (sep < 0) return [];
            var parent = expanded[..(sep + 1)];
            var prefix = expanded[(sep + 1)..];
            if (!Directory.Exists(parent)) return [];
            return new DirectoryInfo(parent)
                .EnumerateDirectories(prefix + "*", new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
                .Where(d => AppSettings.Current.ShowHidden || (d.Attributes & FileAttributes.Hidden) == 0)
                .Select(d => d.FullName)
                .OrderBy(n => n, NaturalComparer.Instance)
                .Take(14)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
