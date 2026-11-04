using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PaxCommander.Views;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace PaxCommander;

/// <summary>
/// The app icon: a modern take on the file-manager folder — layered golden folder with a rim light, papers peeking
/// out, and a dark badge holding two panes in the accent colour. Drawn from vectors so it's crisp at every size,
/// and the accent parts follow the user's colour.
/// </summary>
public static class AppIcon
{
    private const string BackPanel =
        "M8,25 A7,7 0 0 1 15,18 L36,18 A7,7 0 0 1 41.4,20.6 L46,26 L85,26 A7,7 0 0 1 92,33 L92,80 A7,7 0 0 1 85,87 L15,87 A7,7 0 0 1 8,80 Z";

    // Slightly wider at the top than the back panel, as if the front flap leans towards you.
    private const string FrontPanel =
        "M5.5,44 A7,7 0 0 1 12.5,37 L87.5,37 A7,7 0 0 1 94.5,44 L91.6,81.2 A7,7 0 0 1 84.6,87.6 L15.4,87.6 A7,7 0 0 1 8.4,81.2 Z";

    public static Control Mark(double size) => Build(size);

    private static Grid Build(double size)
    {
        // "Arctic": an ice-white folder with a blue rim and a blue sheet, "PAX_" in a terminal face split like a
        // horizon (bright blue above, deep blue below). Fixed colours: the icon is the app's identity, not the accent.
        var blue = Color.Parse("#2D9CFF");
        var canvas = new Canvas { Width = 100, Height = 100 };

        canvas.Children.Add(new ShapePath
        {
            Data = Geometry.Parse(BackPanel),
            Fill = Vertical("#E9F4FF", "#CFE5FA"),
            Stroke = new SolidColorBrush(blue),
            StrokeThickness = 1.6,
        });

        var sheet = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(blue, 0), new GradientStop(Color.Parse("#5BB6FF"), 1) },
        };
        canvas.Children.Add(Sheet(16, 29, 68, 26, sheet, 0.92));

        canvas.Children.Add(new ShapePath
        {
            Data = Geometry.Parse(FrontPanel),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.3, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#F5FAFF"), 0), new GradientStop(Color.Parse("#D6E9FB"), 1) },
            },
        });
        canvas.Children.Add(new ShapePath
        {
            Data = Geometry.Parse("M12.5,38.4 L87.5,38.4"),
            Stroke = new SolidColorBrush(blue),
            StrokeThickness = 2.2,
            StrokeLineCap = PenLineCap.Round,
        });

        var text = new FormattedText("PAX_", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyle.Normal, FontWeight.Bold), 40, Brushes.White);
        var label = new Viewbox
        {
            Width = 66,
            Height = 32,
            Stretch = Stretch.Uniform,
            Child = new ShapePath
            {
                Data = text.BuildGeometry(new Point()),
                Fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#1E90FF"), 0),
                        new GradientStop(Color.Parse("#1E90FF"), 0.52),
                        new GradientStop(Color.Parse("#0B5CC4"), 0.53),
                        new GradientStop(Color.Parse("#0B4A9E"), 1),
                    },
                },
            },
        };
        Canvas.SetLeft(label, 17);
        Canvas.SetTop(label, 47);
        canvas.Children.Add(label);

        var tile = new Grid { Width = size, Height = size };
        tile.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });
        return tile;
    }

    /// <summary>The second pane is outlined with list rows, like the app's own two-pane layout.</summary>
    private static Control Pane(int column)
    {
        var rows = new StackPanel { Spacing = 3, Margin = new Thickness(2.5, 3.5) };
        for (var i = 0; i < 3; i++)
            rows.Children.Add(new Border { Height = 2.2, CornerRadius = new CornerRadius(1), Background = AccentTheme.TileBrush, Width = i == 2 ? 6 : double.NaN, HorizontalAlignment = i == 2 ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch });
        var pane = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            BorderThickness = new Thickness(1.4),
            BorderBrush = AccentTheme.TileBrush,
            Child = rows,
        };
        Grid.SetColumn(pane, column);
        return pane;
    }

    private static Border Sheet(double x, double y, double w, double h, IBrush fill, double opacity)
    {
        var sheet = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(3.5), Background = fill, Opacity = opacity };
        Canvas.SetLeft(sheet, x);
        Canvas.SetTop(sheet, y);
        return sheet;
    }

    private static LinearGradientBrush Vertical(string top, string bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Writes a multi-size .ico (PNG entries) of the mark, for the exe's embedded icon.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [256, 64, 48, 32, 24, 16];
        var pngs = sizes.Select(s =>
        {
            var mark = Build(s);
            mark.Measure(new Size(s, s));
            mark.Arrange(new Rect(0, 0, s, s));
            using var bmp = new RenderTargetBitmap(new PixelSize(s, s), new Vector(96, 96));
            bmp.Render(mark);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return ms.ToArray();
        }).ToList();
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            bw.Write(b); bw.Write(b); bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
            bw.Write(pngs[i].Length); bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
    }

    public static WindowIcon CreateWindowIcon(int size = 128)
    {
        var tile = Build(size);
        tile.Measure(new Size(size, size));
        tile.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(tile);
        return new WindowIcon(bitmap);
    }
}
