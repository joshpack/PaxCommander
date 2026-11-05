using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>
/// A popped-out terminal: the main window's own title bar (the PAX Commander mark, then what this terminal is and
/// where, then minimize / maximize / close) above the live terminal. It opens where the last one was left.
/// </summary>
public sealed class TerminalWindow : Window
{
    private readonly TextBlock _where = new()
    {
        FontSize = 12.5,
        Foreground = Skin.Muted,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(4, 0, 12, 0),
    };

    public TerminalWindow(Control terminal, string what, string folder, Window? owner)
    {
        Icon = AppIcon.CreateWindowIcon();
        MinWidth = 420;
        MinHeight = 220;
        Width = Math.Max(640, (owner?.Bounds.Width ?? 1200) * 0.6);
        Height = 460;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowChrome.RememberPlacement(this, () => AppSettings.Current.TerminalWindow, p => AppSettings.Current.TerminalWindow = p);

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0),
            Children = { AppIcon.Mark(22), new TextBlock { Text = "PAX Commander", Classes = { "neon" }, FontWeight = FontWeight.Bold, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center } },
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 48, Background = Brushes.Transparent };
        top.Children.Add(brand);
        var divider = new Border { Width = 1, Height = 18, Background = Skin.Separator, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(divider, 1);
        top.Children.Add(divider);
        Grid.SetColumn(_where, 2);
        top.Children.Add(_where);
        var caption = WindowChrome.CaptionButtons(this);
        Grid.SetColumn(caption, 3);
        top.Children.Add(caption);
        WindowChrome.MakeDraggable(this, top);

        var body = new Border { Margin = new Thickness(8, 0, 8, 8), CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = terminal };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);
        Content = root;
        SetWhere(what, folder);
    }

    /// <summary>The terminal moved: "PowerShell · this PC — C:\Users\me".</summary>
    public void SetWhere(string what, string folder)
    {
        var text = $"{what.Replace("  ", " · ")} — {folder}";
        _where.Text = text;
        Title = $"{text} — PAX Commander";
    }

    /// <summary>Hands the terminal back (before docking it into a tab again).</summary>
    public void Release()
    {
        if (Content is DockPanel root && root.Children.OfType<Border>().LastOrDefault() is { } body) body.Child = null;
    }
}
