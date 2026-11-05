using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>
/// PAX Commander's own title bar, shared by every window: the system one is removed, empty space in the bar drags the
/// window (with Windows snap), double-click maximizes, and minimize / maximize / close sit at the right.
/// </summary>
public static class WindowChrome
{
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    /// <summary>Removes the system title bar and returns the caption buttons to place at the right of the window's own bar.</summary>
    public static StackPanel CaptionButtons(Window w)
    {
        w.ExtendClientAreaToDecorationsHint = true;
        w.WindowDecorations = WindowDecorations.BorderOnly;
        var max = CaptionButton(MaximizeGlyph, "Maximize", WindowDecorationsElementRole.MaximizeButton, () => ToggleMaximized(w));
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            var maximized = w.WindowState == WindowState.Maximized;
            ((Avalonia.Controls.Shapes.Path)max.Content!).Data = Geometry.Parse(maximized ? RestoreGlyph : MaximizeGlyph);
            ToolTip.SetTip(max, maximized ? "Restore" : "Maximize");
            // A maximized borderless window hangs a few pixels off each screen edge; keep the content on screen.
            w.Padding = maximized ? w.OffScreenMargin : default;
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                CaptionButton("M0,5.5 L10,5.5", "Minimize", WindowDecorationsElementRole.MinimizeButton, () => w.WindowState = WindowState.Minimized),
                max,
                CaptionButton("M0,0 L10,10 M10,0 L0,10", "Close", WindowDecorationsElementRole.CloseButton, w.Close, close: true),
            },
        };
    }

    /// <summary>Empty space in <paramref name="bar"/> drags the window; double-click maximizes or restores.</summary>
    public static void MakeDraggable(Window w, Control bar)
    {
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (e.ClickCount == 2) ToggleMaximized(w);
            else w.BeginMoveDrag(e);
            e.Handled = true;
        };
    }

    public static void ToggleMaximized(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Minimize / maximize / close. The role lets Windows treat them as the real caption buttons (snap layouts on
    /// hovering maximize); the click handler does the job wherever that isn't available.
    /// </summary>
    private static Button CaptionButton(string glyph, string tip, WindowDecorationsElementRole role, Action click, bool close = false)
    {
        var b = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(glyph), Width = 10, Height = 10, Stroke = Skin.Text, StrokeThickness = 1 },
            Width = 46,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("caption");
        if (close) b.Classes.Add("close");
        ToolTip.SetTip(b, tip);
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(b, role);
        b.Click += (_, _) => click();
        return b;
    }

    // ---- Remembered placement ----------------------------------------------------------------------------------------

    /// <summary>
    /// Puts the window where the user last left it (size, position, maximized) and keeps that up to date as it's moved
    /// or resized, so it survives however the app ends. A position that's no longer on any screen (a monitor was
    /// unplugged) is ignored and the window centres instead.
    /// </summary>
    public static void RememberPlacement(Window w, Func<WindowPlacement?> load, Action<WindowPlacement> store)
    {
        var p = load();
        if (p is { Width: > 0, Height: > 0 })
        {
            w.Width = Math.Max(w.MinWidth, p.Width);
            w.Height = Math.Max(w.MinHeight, p.Height);
            if ((p.X != 0 || p.Y != 0) && OnSomeScreen(w, p))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Position = new PixelPoint(p.X, p.Y);
            }
            if (p.Maximized) w.WindowState = WindowState.Maximized;
        }

        var current = p is null ? new WindowPlacement { Width = w.Width, Height = w.Height } : Copy(p);
        var ready = false;
        void Save()
        {
            if (!ready) return;
            store(Copy(current));
            AppSettings.SaveSoon();
        }
        w.Opened += (_, _) =>
        {
            ready = true;
            if (w.WindowState == WindowState.Normal)
            {
                current.X = w.Position.X;
                current.Y = w.Position.Y;
                current.Width = w.ClientSize.Width;
                current.Height = w.ClientSize.Height;
            }
        };
        w.PositionChanged += (_, e) =>
        {
            if (w.WindowState != WindowState.Normal) return;
            current.X = e.Point.X;
            current.Y = e.Point.Y;
            Save();
        };
        w.Resized += (_, e) =>
        {
            if (w.WindowState != WindowState.Normal || e.ClientSize.Width < 50) return;
            current.Width = e.ClientSize.Width;
            current.Height = e.ClientSize.Height;
            Save();
        };
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty || w.WindowState == WindowState.Minimized) return;
            current.Maximized = w.WindowState == WindowState.Maximized;
            Save();
        };
    }

    private static WindowPlacement Copy(WindowPlacement p) => new() { X = p.X, Y = p.Y, Width = p.Width, Height = p.Height, Maximized = p.Maximized };

    private static bool OnSomeScreen(Window w, WindowPlacement p)
    {
        var screens = w.Screens?.All;
        if (screens is null || screens.Count == 0) return true;
        // The title bar (top-left corner, a little in) must land on a screen, or the window can't be grabbed.
        var probe = new PixelPoint(p.X + 60, p.Y + 20);
        return screens.Any(s => s.WorkingArea.Contains(probe));
    }
}
