using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PaxCommander.Views;

/// <summary>Line icons on a 24×24 grid, drawn with round strokes in the inherited foreground colour.</summary>
public static class Glyphs
{
    private static readonly Dictionary<string, Geometry> Cache = new();

    private static readonly Dictionary<string, string> Data = new()
    {
        ["back"] = "M19,12 L5,12 M12,19 L5,12 L12,5",
        ["forward"] = "M5,12 L19,12 M12,5 L19,12 L12,19",
        ["up"] = "M12,19 L12,5 M5,12 L12,5 L19,12",
        ["refresh"] = "M21,12 A9,9 0 1 1 18.36,5.64 M21,3 L21,9 L15,9",
        ["search"] = "M4,11 A7,7 0 1 0 18,11 A7,7 0 1 0 4,11 Z M21,21 L16.65,16.65",
        ["chevron"] = "M9,6 L15,12 L9,18",
        ["chevrondown"] = "M6,9 L12,15 L18,9",
        ["close"] = "M18,6 L6,18 M6,6 L18,18",
        ["plus"] = "M12,5 L12,19 M5,12 L19,12",
        ["folder"] = "M3,7 A2,2 0 0 1 5,5 L9,5 L11,7 L19,7 A2,2 0 0 1 21,9 L21,17 A2,2 0 0 1 19,19 L5,19 A2,2 0 0 1 3,17 Z",
        ["newfolder"] = "M3,7 A2,2 0 0 1 5,5 L9,5 L11,7 L19,7 A2,2 0 0 1 21,9 L21,17 A2,2 0 0 1 19,19 L5,19 A2,2 0 0 1 3,17 Z M12,10 L12,16 M9,13 L15,13",
        ["details"] = "M8,6 L21,6 M8,12 L21,12 M8,18 L21,18 M3,6 L3.01,6 M3,12 L3.01,12 M3,18 L3.01,18",
        ["columns"] = "M3,4 L21,4 L21,20 L3,20 Z M9,4 L9,20 M15,4 L15,20",
        ["tiles"] = "M3,3 L10,3 L10,10 L3,10 Z M14,3 L21,3 L21,10 L14,10 Z M3,14 L10,14 L10,21 L3,21 Z M14,14 L21,14 L21,21 L14,21 Z",
        ["preview"] = "M3,4 L21,4 L21,20 L3,20 Z M14,4 L14,20 M16.5,8 L18.5,8 M16.5,11 L18.5,11",
        ["sidebar"] = "M3,4 L21,4 L21,20 L3,20 Z M9,4 L9,20 M5,8 L7,8 M5,11 L7,11",
        ["dual"] = "M3,4 L21,4 L21,20 L3,20 Z M12,4 L12,20",
        ["settings"] = "M4,21 L4,14 M4,10 L4,3 M12,21 L12,12 M12,8 L12,3 M20,21 L20,16 M20,12 L20,3 M1,14 L7,14 M9,8 L15,8 M17,16 L23,16",
        ["star"] = "M12,2 L15.09,8.26 L22,9.27 L17,14.14 L18.18,21.02 L12,17.77 L5.82,21.02 L7,14.14 L2,9.27 L8.91,8.26 Z",
        ["drive"] = "M22,12 L2,12 M5.45,5.11 L2,12 L2,18 A2,2 0 0 0 4,20 L20,20 A2,2 0 0 0 22,18 L22,12 L18.55,5.11 A2,2 0 0 0 16.76,4 L7.24,4 A2,2 0 0 0 5.45,5.11 Z M6,16 L6.01,16 M10,16 L10.01,16",
        ["network"] = "M2,4 L22,4 L22,10 L2,10 Z M2,14 L22,14 L22,20 L2,20 Z M6,7 L6.01,7 M6,17 L6.01,17",
        ["clock"] = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M12,7 L12,12 L15,14",
        ["home"] = "M3,10 L12,3 L21,10 L21,20 A2,2 0 0 1 19,22 L5,22 A2,2 0 0 1 3,20 Z M9,22 L9,12 L15,12 L15,22",
        ["download"] = "M21,15 L21,19 A2,2 0 0 1 19,21 L5,21 A2,2 0 0 1 3,19 L3,15 M7,10 L12,15 L17,10 M12,15 L12,3",
        ["doc"] = "M14,2 L6,2 A2,2 0 0 0 4,4 L4,20 A2,2 0 0 0 6,22 L18,22 A2,2 0 0 0 20,20 L20,8 Z M14,2 L14,8 L20,8 M8,13 L16,13 M8,17 L13,17",
        ["file"] = "M14,2 L6,2 A2,2 0 0 0 4,4 L4,20 A2,2 0 0 0 6,22 L18,22 A2,2 0 0 0 20,20 L20,8 Z M14,2 L14,8 L20,8",
        ["image"] = "M3,5 A2,2 0 0 1 5,3 L19,3 A2,2 0 0 1 21,5 L21,19 A2,2 0 0 1 19,21 L5,21 A2,2 0 0 1 3,19 Z M7,9 A1.5,1.5 0 1 0 10,9 A1.5,1.5 0 1 0 7,9 Z M21,15 L16,10 L5,21",
        ["audio"] = "M9,18 L9,5 L21,3 L21,16 M3,18 A3,3 0 1 0 9,18 A3,3 0 1 0 3,18 Z M15,16 A3,3 0 1 0 21,16 A3,3 0 1 0 15,16 Z",
        ["video"] = "M3,5 L21,5 L21,19 L3,19 Z M7,5 L7,19 M17,5 L17,19 M3,9 L7,9 M3,15 L7,15 M17,9 L21,9 M17,15 L21,15",
        ["desktop"] = "M3,4 L21,4 L21,16 L3,16 Z M8,20 L16,20 M12,16 L12,20",
        ["terminal"] = "M4,17 L10,11 L4,5 M12,19 L20,19",
        ["copy"] = "M9,9 L20,9 L20,20 L9,20 Z M5,15 L4,15 A1,1 0 0 1 3,14 L3,4 A1,1 0 0 1 4,3 L14,3 A1,1 0 0 1 15,4 L15,5",
        ["cut"] = "M3,6 A3,3 0 1 0 9,6 A3,3 0 1 0 3,6 Z M3,18 A3,3 0 1 0 9,18 A3,3 0 1 0 3,18 Z M20,4 L8.12,15.88 M14.47,14.48 L20,20 M8.12,8.12 L12,12",
        ["paste"] = "M16,4 L18,4 A2,2 0 0 1 20,6 L20,20 A2,2 0 0 1 18,22 L6,22 A2,2 0 0 1 4,20 L4,6 A2,2 0 0 1 6,4 L8,4 M9,2 L15,2 L15,6 L9,6 Z",
        ["trash"] = "M3,6 L21,6 M19,6 L18,20 A2,2 0 0 1 16,22 L8,22 A2,2 0 0 1 6,20 L5,6 M10,11 L10,17 M14,11 L14,17 M9,6 L9,4 A1,1 0 0 1 10,3 L14,3 A1,1 0 0 1 15,4 L15,6",
        ["rename"] = "M17,3 L21,7 L7,21 L3,21 L3,17 Z",
        ["pause"] = "M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z",
        ["play"] = "M6,4 L20,12 L6,20 Z",
        ["filter"] = "M22,3 L2,3 L10,12.46 L10,19 L14,21 L14,12.46 Z",
        ["sort"] = "M7,4 L7,20 M3,8 L7,4 L11,8 M17,20 L17,4 M13,16 L17,20 L21,16",
        ["more"] = "M5,12 L5.01,12 M12,12 L12.01,12 M19,12 L19.01,12",
        ["command"] = "M18,3 A3,3 0 0 0 15,6 L15,18 A3,3 0 0 0 18,21 A3,3 0 0 0 21,18 A3,3 0 0 0 18,15 L6,15 A3,3 0 0 0 3,18 A3,3 0 0 0 6,21 A3,3 0 0 0 9,18 L9,6 A3,3 0 0 0 6,3 A3,3 0 0 0 3,6 A3,3 0 0 0 6,9 L18,9 A3,3 0 0 0 21,6 A3,3 0 0 0 18,3 Z",
        ["tag"] = "M20.59,13.41 L13.42,20.58 A2,2 0 0 1 10.59,20.58 L2,12 L2,2 L12,2 L20.59,10.59 A2,2 0 0 1 20.59,13.41 Z M7,7 L7.01,7",
        ["branch"] = "M6,3 L6,15 M15,6 A3,3 0 1 0 21,6 A3,3 0 1 0 15,6 Z M3,18 A3,3 0 1 0 9,18 A3,3 0 1 0 3,18 Z M18,9 A9,9 0 0 1 9,18",
        ["archive"] = "M21,8 L21,21 L3,21 L3,8 M1,3 L23,3 L23,8 L1,8 Z M10,12 L14,12",
        ["code"] = "M16,18 L22,12 L16,6 M8,6 L2,12 L8,18",
        ["check"] = "M20,6 L9,17 L4,12",
        ["alert"] = "M10.29,3.86 L1.82,18 A2,2 0 0 0 3.53,21 L20.47,21 A2,2 0 0 0 22.18,18 L13.71,3.86 A2,2 0 0 0 10.29,3.86 Z M12,9 L12,13 M12,17 L12.01,17",
        ["info"] = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M12,16 L12,12 M12,8 L12.01,8",
        ["undo"] = "M3,7 L3,13 L9,13 M21,17 A9,9 0 0 0 6,10.3 L3,13",
        ["redo"] = "M21,7 L21,13 L15,13 M3,17 A9,9 0 0 1 18,10.3 L21,13",
        ["open"] = "M18,13 L18,19 A2,2 0 0 1 16,21 L5,21 A2,2 0 0 1 3,19 L3,8 A2,2 0 0 1 5,6 L11,6 M15,3 L21,3 L21,9 M10,14 L21,3",
        ["zap"] = "M13,2 L3,14 L12,14 L11,22 L21,10 L12,10 Z",
        ["pin"] = "M12,17 L12,22 M5,17 L19,17 L17,13 L17,6 L18,6 L18,2 L6,2 L6,6 L7,6 L7,13 Z",
        ["swap"] = "M16,3 L20,7 L16,11 M20,7 L4,7 M8,21 L4,17 L8,13 M4,17 L20,17",
        ["sigma"] = "M18,7 L18,4 L6,4 L12,12 L6,20 L18,20 L18,17",
        ["hash"] = "M4,9 L20,9 M4,15 L20,15 M10,3 L8,21 M16,3 L14,21",
        ["eye"] = "M2,12 C2,12 5.5,5 12,5 C18.5,5 22,12 22,12 C22,12 18.5,19 12,19 C5.5,19 2,12 2,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z",
        ["eyeoff"] = "M3,3 L21,21 M10.6,5.1 C11.06,5.03 11.53,5 12,5 C18.5,5 22,12 22,12 C21.4,13.2 20.6,14.3 19.7,15.3 M6.6,6.6 C3.7,8.5 2,12 2,12 C2,12 5.5,19 12,19 C13.8,19 15.4,18.5 16.8,17.7",
        ["window"] = "M3,4 L21,4 L21,20 L3,20 Z M3,9 L21,9",
        ["label"] = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z",
        ["link"] = "M10,13 A5,5 0 0 0 17.54,13.54 L20.54,10.54 A5,5 0 0 0 13.46,3.46 L11.74,5.17 M14,11 A5,5 0 0 0 6.46,10.46 L3.46,13.46 A5,5 0 0 0 10.54,20.54 L12.25,18.83",
        ["shield"] = "M12,22 C12,22 20,18 20,12 L20,5 L12,2 L4,5 L4,12 C4,18 12,22 12,22 Z",
        ["select"] = "M3,3 L10,3 M14,3 L21,3 L21,10 M21,14 L21,21 L14,21 M10,21 L3,21 L3,14 M3,10 L3,3",
        ["disc"] = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M10,12 A2,2 0 1 0 14,12 A2,2 0 1 0 10,12 Z M12,6 A6,6 0 0 1 17.2,9",
        ["eject"] = "M12,5 L19,13 L5,13 Z M5,18 L19,18",
        ["palette"] = "M12,3 A9,9 0 1 0 12,21 C13.1,21 13.6,20.2 13.6,19.4 C13.6,18.2 12.7,17.6 12.7,16.6 C12.7,15.6 13.5,14.9 14.5,14.9 L16.4,14.9 A4.6,4.6 0 0 0 21,10.3 C21,6.3 17,3 12,3 Z M7.5,11 L7.51,11 M10,7.5 L10.01,7.5 M14.5,7.5 L14.51,7.5 M17,11 L17.01,11",
    };

    public static Geometry Get(string name)
    {
        if (Cache.TryGetValue(name, out var g)) return g;
        g = Geometry.Parse(Data.TryGetValue(name, out var d) ? d : Data["file"]);
        Cache[name] = g;
        return g;
    }

    /// <summary>A sized icon control for buttons and menus.</summary>
    public static GlyphIcon Icon(string name, double size = 16, IBrush? brush = null) =>
        new() { Glyph = name, Width = size, Height = size, Brush = brush };
}

/// <summary>
/// Draws a <see cref="Glyphs"/> line icon. Uses the inherited text colour unless <see cref="Brush"/> is set, so icon
/// buttons light up together with their label on hover.
/// </summary>
public sealed class GlyphIcon : Control
{
    public static readonly StyledProperty<string> GlyphProperty = AvaloniaProperty.Register<GlyphIcon, string>(nameof(Glyph), "file");
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<GlyphIcon, IBrush?>(nameof(Brush));
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<GlyphIcon, double>(nameof(StrokeWidth), 2);

    static GlyphIcon()
    {
        AffectsRender<GlyphIcon>(GlyphProperty, BrushProperty, StrokeWidthProperty, TextElement.ForegroundProperty);
    }

    public string Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var brush = Brush ?? GetValue(TextElement.ForegroundProperty) ?? Brushes.White;
        var scale = size / 24.0;
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, Glyphs.Get(Glyph));
    }
}
