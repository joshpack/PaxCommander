using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PaxCommander.Views;

public sealed record PaletteItem(string Title, string? Detail, string Glyph, string? Gesture, Action Run, IBrush? Tint = null);

/// <summary>
/// The "Go anywhere" box: one fuzzy search over frequent folders, favorites, drives, items in the current folder,
/// typed paths and every command. Start with ">" for commands only.
/// </summary>
public sealed class CommandPalette
{
    private readonly Panel _layer;
    private readonly Border _backdrop;
    private readonly TextBox _input;
    private readonly ListBox _list;
    private readonly Func<string, IReadOnlyList<PaletteItem>> _provider;
    private DispatcherTimer? _timer;

    public static CommandPalette? Current { get; private set; }

    private CommandPalette(Panel layer, string initial, string placeholder, Func<string, IReadOnlyList<PaletteItem>> provider)
    {
        _layer = layer;
        _provider = provider;
        _input = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder,
            FontSize = 15,
            Height = 42,
            InnerLeftContent = new GlyphIcon { Glyph = initial.StartsWith('>') ? "command" : "search", Width = 16, Height = 16, Margin = new Thickness(12, 0, 2, 0), Brush = Skin.Accent },
            Background = Skin.Input,
        };
        _list = new ListBox
        {
            Classes = { "palette" },
            MaxHeight = 440,
            Margin = new Thickness(0, 8, 0, 0),
            ItemTemplate = new FuncDataTemplate<PaletteItem>((item, _) => item is null ? new Panel() : Row(item)),
        };
        _list.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) RunSelected();
        };
        var hint = new TextBlock
        {
            Text = "↑↓ to choose · Enter to go · > for commands · type a path to jump there",
            FontSize = 11,
            Foreground = Skin.Faint,
            Margin = new Thickness(4, 8, 0, 0),
        };
        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(10),
            Width = 660,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 64, 0, 0),
            Child = new StackPanel { Children = { _input, _list, hint } },
        };
        _backdrop = new Border { Background = new SolidColorBrush(Color.Parse("#70050709")), Child = card };
        _backdrop.PointerPressed += (_, e) =>
        {
            if (e.Source == _backdrop) Close();
        };
        _input.AddHandler(InputElement.KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.TextChanged += (_, _) => Queue();
    }

    public static void Show(Panel layer, string initial, string placeholder, Func<string, IReadOnlyList<PaletteItem>> provider)
    {
        Current?.Close();
        var p = new CommandPalette(layer, initial, placeholder, provider);
        Current = p;
        layer.Children.Add(p._backdrop);
        p.Refresh();
        Dispatcher.UIThread.Post(() =>
        {
            p._input.Focus();
            p._input.CaretIndex = p._input.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private static Control Row(PaletteItem item)
    {
        var icon = Glyphs.Icon(item.Glyph, 16, item.Tint ?? Skin.Muted);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(item.Detail))
            text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 11.5, Foreground = Skin.Faint, TextTrimming = TextTrimming.PathSegmentEllipsis });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (!string.IsNullOrEmpty(item.Gesture))
        {
            var key = new Border
            {
                Background = Skin.Line,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Child = new TextBlock { Text = item.Gesture, FontSize = 11, Foreground = Skin.Muted },
            };
            Grid.SetColumn(key, 2);
            grid.Children.Add(key);
        }
        return grid;
    }

    private void Queue()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Input, (_, _) =>
        {
            _timer!.Stop();
            Refresh();
        });
        _timer.Stop();
        _timer.Start();
    }

    private void Refresh()
    {
        var items = _provider(_input.Text ?? "");
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        if (_input.InnerLeftContent is GlyphIcon g) g.Glyph = (_input.Text ?? "").StartsWith('>') ? "command" : "search";
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Enter:
                RunSelected();
                break;
            case Key.Down:
                if (count > 0) _list.SelectedIndex = (_list.SelectedIndex + 1) % count;
                _list.ScrollIntoView(_list.SelectedIndex);
                break;
            case Key.Up:
                if (count > 0) _list.SelectedIndex = (_list.SelectedIndex - 1 + count) % count;
                _list.ScrollIntoView(_list.SelectedIndex);
                break;
            case Key.PageDown:
                if (count > 0) _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 8);
                _list.ScrollIntoView(_list.SelectedIndex);
                break;
            case Key.PageUp:
                if (count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 8);
                _list.ScrollIntoView(_list.SelectedIndex);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void RunSelected()
    {
        if (_list.SelectedItem is not PaletteItem item) return;
        Close();
        try
        {
            item.Run();
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    public void Close()
    {
        _layer.Children.Remove(_backdrop);
        if (Current == this) Current = null;
    }
}
