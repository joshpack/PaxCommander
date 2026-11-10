using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdCommander.Core;
using pxdCommander.Ops;

namespace pxdCommander.Views;

public enum ToastKind { Info, Success, Error }

/// <summary>Small notices in the bottom-right corner that fade away (optionally with an action such as Undo).</summary>
public static class Toasts
{
    private static StackPanel? _host;

    public static void Attach(Panel layer)
    {
        _host = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 18, 18),
            MaxWidth = 460,
        };
        layer.Children.Add(_host);
    }

    public static void Show(string text, ToastKind kind = ToastKind.Info, string? action = null, Action? onAction = null, double seconds = 4)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(text, kind, action, onAction, seconds));
            return;
        }
        if (_host is null) return;
        var color = kind switch { ToastKind.Error => Skin.Bad, ToastKind.Success => Skin.Accent, _ => Skin.Info };
        var row = new DockPanel { LastChildFill = true };
        var icon = Glyphs.Icon(kind == ToastKind.Error ? "alert" : kind == ToastKind.Success ? "check" : "info", 16, color);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        Border card = null!;
        if (action is not null && onAction is not null)
        {
            var btn = new Button { Content = action, Padding = new Thickness(10, 3), MinHeight = 0, Margin = new Thickness(12, -3, 0, -3), VerticalAlignment = VerticalAlignment.Center };
            btn.Click += (_, _) =>
            {
                onAction();
                Remove(card);
            };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
        }
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14, 11),
            CornerRadius = new CornerRadius(10),
            Child = row,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
        };
        card.PointerPressed += (_, _) => Remove(card);
        _host.Children.Add(card);
        while (_host.Children.Count > 4) _host.Children.RemoveAt(0);
        Dispatcher.UIThread.Post(() => card.Opacity = 1, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => Remove(card), TimeSpan.FromSeconds(seconds + (action is null ? 0 : 3)));
    }

    private static void Remove(Border card)
    {
        if (_host is null || !_host.Children.Contains(card)) return;
        card.Opacity = 0;
        DispatcherTimer.RunOnce(() => _host.Children.Remove(card), TimeSpan.FromMilliseconds(200));
    }
}

/// <summary>
/// In-window modal dialogs (a dimmed backdrop and a card) — faster and better looking than separate OS windows, and
/// awaitable. Only one shows at a time; later requests (e.g. conflicts from two jobs) wait their turn.
/// </summary>
public static class Dialogs
{
    private static Panel? _layer;
    private static readonly SemaphoreSlim One = new(1, 1);

    public static void Attach(Panel layer) => _layer = layer;

    private static int _open;

    public static bool IsOpen => _open > 0;

    /// <summary>Shows a card built by <paramref name="build"/>; the card completes the source to close.</summary>
    public static async Task<T> ShowAsync<T>(Func<TaskCompletionSource<T>, Control> build, T cancelValue, double width = 460, bool focusInput = true,
        CancellationToken dismiss = default, bool queue = true)
    {
        if (dismiss.IsCancellationRequested) return cancelValue;
        // Prompts raised by a dialog that is itself open (e.g. "trust this server?" during Test) skip the queue.
        if (queue) await One.WaitAsync();
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_layer is null) return cancelValue;
                var tcs = new TaskCompletionSource<T>();
                // A prompt whose job ended meanwhile (cancelled from the queue) closes itself.
                await using var _ = dismiss.Register(() => Dispatcher.UIThread.Post(() => tcs.TrySetResult(cancelValue)));
                var top = TopLevel.GetTopLevel(_layer);
                var previousFocus = top?.FocusManager?.GetFocusedElement();
                var card = new Border { Classes = { "card" }, Width = width, Child = build(tcs), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var backdrop = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#A6050709")),
                    Child = card,
                };
                backdrop.PointerPressed += (_, e) =>
                {
                    if (e.Source == backdrop) tcs.TrySetResult(cancelValue);
                };
                backdrop.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        tcs.TrySetResult(cancelValue);
                        e.Handled = true;
                    }
                };
                _layer.Children.Add(backdrop);
                _open++;
                Dispatcher.UIThread.Post(() =>
                {
                    // Focus the first text box, else the primary button, so the keyboard works at once.
                    var first = (focusInput ? card.GetLogicalDescendantsOfType<TextBox>().FirstOrDefault() : null) as Control
                                ?? card.GetLogicalDescendantsOfType<Button>().FirstOrDefault(b => b.Classes.Contains("primary") || b.Classes.Contains("danger"))
                                ?? card.GetLogicalDescendantsOfType<Button>().FirstOrDefault();
                    first?.Focus();
                }, DispatcherPriority.Input);
                try
                {
                    return await tcs.Task;
                }
                finally
                {
                    _layer.Children.Remove(backdrop);
                    _open--;
                    previousFocus?.Focus();
                }
            });
        }
        finally
        {
            if (queue) One.Release();
        }
    }

    private static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control root) where T : Control
    {
        foreach (var child in root.GetLogicalChildren().OfType<Control>())
        {
            if (child is T t) yield return t;
            foreach (var d in child.GetLogicalDescendantsOfType<T>()) yield return d;
        }
    }

    private static IEnumerable<Avalonia.LogicalTree.ILogical> GetLogicalChildren(this Control c) =>
        ((Avalonia.LogicalTree.ILogical)c).LogicalChildren;

    public static TextBlock Title(string text) => new() { Text = text, Classes = { "h1" }, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Skin.Muted, Margin = new Thickness(0, 0, 0, 14) };

    public static Button Btn(string text, string? cls = null, Action? click = null)
    {
        var b = new Button { Content = text, MinWidth = 88 };
        if (cls is not null) b.Classes.Add(cls);
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static Task<bool> ConfirmAsync(string title, string message, string confirm = "OK", bool danger = false, bool queue = true) =>
        ShowAsync<bool>(tcs =>
        {
            var ok = Btn(confirm, danger ? "danger" : "primary", () => tcs.TrySetResult(true));
            var cancel = Btn("Cancel", null, () => tcs.TrySetResult(false));
            var panel = new StackPanel { Children = { Title(title), Body(message), Buttons(cancel, ok) } };
            panel.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    tcs.TrySetResult(true);
                    e.Handled = true;
                }
            };
            return panel;
        }, false, queue: queue);

    public static Task<int> ChoiceAsync(string title, string message, params string[] buttons) => ChoiceAsync(title, message, default, buttons);

    public static Task<int> ChoiceAsync(string title, string message, CancellationToken dismiss, params string[] buttons) =>
        ShowAsync<int>(tcs =>
        {
            var row = Buttons(buttons.Select((b, i) => Btn(b, i == buttons.Length - 1 ? "primary" : null, () => tcs.TrySetResult(i))).ToArray());
            return new StackPanel { Children = { Title(title), Body(message), row } };
        }, -1, dismiss: dismiss);

    /// <summary>Asks for a line of text. <paramref name="validate"/> returns an error message or null.</summary>
    public static Task<string?> PromptAsync(string title, string message, string initial, Func<string, string?>? validate = null, int selectLength = -1) =>
        ShowAsync<string?>(tcs =>
        {
            var box = new TextBox { Text = initial };
            var error = new TextBlock { Foreground = Skin.Bad, FontSize = 12, Margin = new Thickness(2, 6, 0, 0), IsVisible = false, TextWrapping = TextWrapping.Wrap };
            void Submit()
            {
                var text = box.Text ?? "";
                if (validate?.Invoke(text) is { } err)
                {
                    error.Text = err;
                    error.IsVisible = true;
                    return;
                }
                tcs.TrySetResult(text);
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Submit();
                    e.Handled = true;
                }
            };
            box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectionStart = 0;
                box.SelectionEnd = selectLength >= 0 ? selectLength : initial.Length;
            }, DispatcherPriority.Input);
            var panel = new StackPanel { Children = { Title(title) } };
            if (message.Length > 0) panel.Children.Add(Body(message));
            panel.Children.Add(box);
            panel.Children.Add(error);
            panel.Children.Add(Buttons(Btn("Cancel", null, () => tcs.TrySetResult(null)), Btn("OK", "primary", Submit)));
            return panel;
        }, null);

    /// <summary>Name clash during a copy/move: shows both files side by side.</summary>
    public static Task<ConflictAnswer> ConflictAsync(ConflictInfo c, CancellationToken dismiss = default) =>
        ShowAsync<ConflictAnswer>(tcs =>
        {
            var all = new CheckBox { Content = "Do this for all remaining conflicts", Margin = new Thickness(0, 10, 0, 4) };
            Control Side(string heading, string path, long size, DateTime modifiedUtc, bool newer)
            {
                var p = new StackPanel { Spacing = 2 };
                p.Children.Add(new TextBlock { Text = heading, Classes = { "label" } });
                p.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                p.Children.Add(new TextBlock { Text = Path.GetDirectoryName(path) ?? "", Foreground = Skin.Faint, FontSize = 11.5, TextTrimming = TextTrimming.PathSegmentEllipsis });
                if (!c.IsDirectory) p.Children.Add(new TextBlock { Text = Formatting.Size(size) + (size >= 1024 ? " · " + Formatting.Bytes(size) : ""), Foreground = Skin.Muted, FontSize = 12 });
                p.Children.Add(new TextBlock
                {
                    Text = "Modified " + Formatting.Date(modifiedUtc.ToLocalTime(), false) + (newer ? "  · newer" : ""),
                    Foreground = newer ? Skin.Accent : Skin.Muted,
                    FontSize = 12,
                });
                return new Border { Background = Skin.Popup, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Child = p };
            }
            var srcNewer = c.SourceModifiedUtc > c.DestinationModifiedUtc;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
            grid.Children.Add(Side("COPYING", c.SourcePath, c.SourceSize, c.SourceModifiedUtc, srcNewer));
            var right = Side("ALREADY THERE", c.DestinationPath, c.DestinationSize, c.DestinationModifiedUtc, !srcNewer && c.SourceModifiedUtc != c.DestinationModifiedUtc);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            void Answer(ConflictChoice choice) => tcs.TrySetResult(new ConflictAnswer(choice, all.IsChecked == true));
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (text, choice, cls) in new[]
                     {
                         ("Cancel", ConflictChoice.Cancel, (string?)null),
                         ("Skip", ConflictChoice.Skip, null),
                         ("Keep both", ConflictChoice.KeepBoth, null),
                         ("Replace if newer", ConflictChoice.ReplaceIfNewer, null),
                         ("Replace", ConflictChoice.Replace, "primary"),
                     })
            {
                var b = Btn(text, cls, () => Answer(choice));
                b.Margin = new Thickness(6, 4, 0, 0);
                buttons.Children.Add(b);
            }
            return new StackPanel
            {
                Children =
                {
                    Title(c.DestinationIsDirectory ? $"A folder named “{Path.GetFileName(c.DestinationPath)}” is already there" : $"A file named “{Path.GetFileName(c.DestinationPath)}” is already there"),
                    grid,
                    all,
                    buttons,
                },
            };
        }, new ConflictAnswer(ConflictChoice.Cancel, false), width: 620, dismiss: dismiss);

    public static async Task<ErrorChoice> ErrorAsync(string path, Exception error, CancellationToken dismiss = default)
    {
        var choice = await ChoiceAsync("Couldn't finish this item", $"{Path.GetFileName(path)}\n\n{error.Message}", dismiss, "Cancel job", "Skip all", "Skip", "Try again");
        return choice switch
        {
            0 or -1 => ErrorChoice.Cancel,
            1 => ErrorChoice.SkipAll,
            2 => ErrorChoice.Skip,
            _ => ErrorChoice.Retry,
        };
    }
}
