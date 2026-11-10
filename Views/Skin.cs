using System.Globalization;
using Avalonia;
using Avalonia.Media;
using pxdCommander.Core;

namespace pxdCommander.Views;

/// <summary>
/// Brushes, pens and fonts for the custom-drawn lists (kept static so thousands of rows share them). The accent-tinted
/// ones are mutated in place by <see cref="SetAccent"/>, so a new accent reaches every row without rebuilding anything.
/// </summary>
public static class Skin
{
    private static readonly SolidColorBrush AccentSolid = Solid(AccentTheme.Default);
    private static readonly SolidColorBrush SelectedSolid = Solid("#273843");
    private static readonly SolidColorBrush DropSolid = Solid("#22303A");
    private static readonly SolidColorBrush BandSolid = new(Color.Parse("#2289CFF0"));
    private static readonly SolidColorBrush BandBorderSolid = new(Color.Parse("#8089CFF0"));
    private static readonly SolidColorBrush CaretSolid = Solid("#4C7088");
    private static readonly SolidColorBrush PartialSolid = new(Color.Parse("#5089CFF0"));
    private static readonly LinearGradientBrush SizeBarGradient = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#9AA4F5"), 0), new GradientStop(Color.Parse(AccentTheme.Default), 1) },
    };

    // Theme colours: every brush below is recoloured in place by ApplyTheme, so a light/dark switch reaches every
    // control and row already on screen.
    public static readonly SolidColorBrush Text = Solid("#E8EAF0");
    public static readonly SolidColorBrush Muted = Solid("#8A92A6");
    public static readonly SolidColorBrush Faint = Solid("#5B6274");
    /// <summary>Text fields and search boxes drawn in code.</summary>
    public static readonly SolidColorBrush Input = Solid("#0B0E13");
    public static readonly SolidColorBrush InputBorder = Solid("#222937");
    /// <summary>Code, text previews and the terminal.</summary>
    public static readonly SolidColorBrush CodeBg = Solid("#0A0D11");
    public static readonly SolidColorBrush CodeBorder = Solid("#1C222D");
    public static readonly SolidColorBrush CodeText = Solid("#C9D1DE");
    /// <summary>Panes, panels and their borders.</summary>
    public static readonly SolidColorBrush Panel = Solid("#0F1218");
    public static readonly SolidColorBrush PanelBorder = Solid("#1E2430");
    public static readonly SolidColorBrush Line = Solid("#1A2029");
    public static readonly SolidColorBrush Separator = Solid("#232A36");
    /// <summary>Popups and suggestion lists.</summary>
    public static readonly SolidColorBrush Popup = Solid("#141922");
    public static readonly SolidColorBrush PopupBorder = Solid("#2A3140");
    /// <summary>The frame behind previews (images, icons).</summary>
    public static readonly SolidColorBrush Visual = Solid("#0C0F14");
    public static readonly SolidColorBrush TileBg = Solid("#10141A");
    public static readonly SolidColorBrush TileText = Solid("#D3D7E0");
    public static bool IsLight { get; private set; }
    public static readonly IBrush Accent = AccentSolid;
    public static readonly IBrush Info = Solid("#00C8FF");
    public static readonly IBrush Warn = Solid("#F5B14C");
    public static readonly IBrush Bad = Solid("#F2666A");
    public static readonly IBrush Good = Solid("#19F58C");

    public static readonly SolidColorBrush RowHover = Solid("#141A22");
    public static readonly IBrush RowSelected = SelectedSolid;
    public static readonly SolidColorBrush RowSelectedInactive = Solid("#1C2430");
    public static readonly IBrush RowDrop = DropSolid;
    public static readonly SolidColorBrush HeaderBg = Solid("#11151C");
    public static readonly SolidColorBrush Track = Solid("#1A2029");
    public static readonly IBrush Band = BandSolid;
    public static readonly IBrush BandBorder = BandBorderSolid;
    public static readonly IPen CaretPen = new Pen(CaretSolid, 1);
    public static readonly IPen DropPen = new Pen(AccentSolid, 1.5);
    public static readonly IPen SelectedTilePen = new Pen(AccentSolid, 1.2);
    private static readonly SolidColorBrush DividerSolid = Solid("#1E2430");
    private static readonly SolidColorBrush ChevronSolid = Solid("#5B6274");
    public static readonly IPen DividerPen = new Pen(DividerSolid, 1);
    public static readonly IPen ChevronPen = new Pen(ChevronSolid, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    public static readonly IPen ChevronPenSelected = new Pen(AccentSolid, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    public static readonly IBrush SizeBar = SizeBarGradient;
    public static readonly IBrush SizeBarPartial = PartialSolid;
    public static readonly IBrush SizeBarFull = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#F5934C"), 0), new GradientStop(Color.Parse("#F2666A"), 1) },
    };

    public static readonly Typeface Regular = new(FontFamily.Default);
    public static readonly Typeface SemiBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    public static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, DejaVu Sans Mono, Menlo, monospace");

    private static readonly IBrush[] LabelBrushes = Labels.All.Select(l => (IBrush)Solid(l.Color)).ToArray();

    /// <summary>Theme switch: the colours the pens own (called by <see cref="ThemeManager"/>).</summary>
    internal static void SetThemeLines(bool light, Color divider, Color chevron)
    {
        IsLight = light;
        DividerSolid.Color = divider;
        ChevronSolid.Color = chevron;
    }

    /// <summary>Recolours every accent-derived brush and pen (called by <see cref="AccentTheme.Apply"/>).</summary>
    internal static void SetAccent(Color accent, Color secondary, Func<double, Color> mix)
    {
        AccentSolid.Color = accent;
        SelectedSolid.Color = mix(0.20);
        DropSolid.Color = mix(0.16);
        BandSolid.Color = Color.FromArgb(0x22, accent.R, accent.G, accent.B);
        BandBorderSolid.Color = Color.FromArgb(0x80, accent.R, accent.G, accent.B);
        CaretSolid.Color = mix(0.50);
        PartialSolid.Color = Color.FromArgb(0x50, accent.R, accent.G, accent.B);
        SizeBarGradient.GradientStops[0].Color = secondary;
        SizeBarGradient.GradientStops[1].Color = accent;
    }

    public static IBrush Label(int index) => index > 0 && index < LabelBrushes.Length ? LabelBrushes[index] : Brushes.Transparent;

    /// <summary>Drive usage bar: soft for read-only discs (always "full"), red when a disk is nearly out of room.</summary>
    public static IBrush DriveBar(FileItem item) =>
        item.IsDisc ? SizeBarPartial : item.SizeRatio > 0.9 ? SizeBarFull : SizeBar;

    public static IBrush Git(char mark) => mark switch
    {
        'M' or 'R' => Warn,
        'A' or '?' => Accent,
        'D' => Bad,
        _ => Muted,
    };

    public static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));

    public static FormattedText Text1(string text, double size, IBrush brush, double maxWidth, Typeface? face = null, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? Regular, size, brush)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = align,
        };
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        return ft;
    }
}
