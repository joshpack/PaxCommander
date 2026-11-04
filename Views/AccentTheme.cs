using Avalonia;
using Avalonia.Media;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>
/// The user-chosen accent colour. Every accent-tinted resource (buttons, toggles, selection, glows, gradients, text on
/// the accent) is derived from one colour, so any pick looks as deliberate as the default.
/// </summary>
public static class AccentTheme
{
    public const string Default = "#89CFF0";

    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Baby blue", "#89CFF0"),
        ("Neon green", "#19F58C"),
        ("Cyan", "#00C8FF"),
        ("Violet", "#B48CFF"),
        ("Pink", "#FF7EB6"),
        ("Coral", "#FF8A6B"),
        ("Amber", "#F5B14C"),
        ("Lime", "#B5F23D"),
    ];

    private static Color Background = Color.Parse("#0F1218");
    private static bool _light;

    /// <summary>The theme's pane colour: accent mixes (selection, hovers) are laid over it.</summary>
    public static void SetBackground(Color background, bool light)
    {
        Background = background;
        _light = light;
    }

    public static Color Accent { get; private set; } = Color.Parse(Default);

    /// <summary>The partner colour for gradients: the accent's hue turned 40°, as cyan is to the original green.</summary>
    public static Color Secondary { get; private set; }

    /// <summary>Dark text/glyph colour that reads on an accent-filled button.</summary>
    public static Color OnAccent { get; private set; }

    /// <summary>The gradient of the app mark; mutated in place so marks already on screen recolour themselves.</summary>
    public static readonly LinearGradientBrush TileBrush = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 1) },
    };

    /// <summary>The dark ink of the app mark (and anything drawn on the accent), also mutated in place.</summary>
    public static readonly SolidColorBrush MarkInk = new(Color.Parse("#021620"));

    public static event Action? Changed;

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        hex = hex?.Trim();
        if (string.IsNullOrEmpty(hex)) return false;
        if (!hex.StartsWith('#')) hex = "#" + hex;
        return hex.Length == 7 && Color.TryParse(hex, out color);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static void Apply(string? hex)
    {
        if (!TryParse(hex, out var c)) c = Color.Parse(Default);
        Accent = c;
        var hsl = c.ToHsl();
        Secondary = new HslColor(1, (hsl.H + 40) % 360, hsl.S, hsl.L).ToRgb();
        OnAccent = new HslColor(1, hsl.H, Math.Min(1, 0.55 + hsl.S * 0.4), 0.07).ToRgb();
        var hover = Shade(c, 0.14);
        var pressed = Shade(c, -0.12);

        var res = Application.Current!.Resources;
        res["SystemAccentColor"] = c;
        res["SystemAccentColorLight1"] = Shade(c, 0.12);
        res["SystemAccentColorLight2"] = Shade(c, 0.24);
        res["SystemAccentColorLight3"] = Shade(c, 0.36);
        res["SystemAccentColorDark1"] = Shade(c, -0.12);
        res["SystemAccentColorDark2"] = Shade(c, -0.24);
        res["SystemAccentColorDark3"] = Shade(c, -0.36);

        var onAccent = new SolidColorBrush(OnAccent);
        foreach (var key in new[]
                 {
                     "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
                     "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed", "OnAccentBrush",
                 })
            res[key] = onAccent;

        res["Accent"] = c;
        res["AccentHover"] = hover;
        res["AccentPressed"] = pressed;
        res["AccentBrush"] = new SolidColorBrush(c);
        res["AccentHoverBrush"] = new SolidColorBrush(hover);
        res["AccentPressedBrush"] = new SolidColorBrush(pressed);
        res["AccentSoftBrush"] = new SolidColorBrush(Mix(0.17));
        res["AccentFaintBrush"] = new SolidColorBrush(Mix(0.10));
        res["PaneActiveBorderBrush"] = new SolidColorBrush(Mix(0.42));
        res["FocusBorderBrush"] = new SolidColorBrush(c);
        res["FocusGlow"] = BoxShadows.Parse($"0 0 14 0 {Alpha(c, 0x55)}");
        res["AccentGlowSoft"] = BoxShadows.Parse($"0 0 16 0 {Alpha(c, 0x44)}");
        res["AccentGlowStrong"] = BoxShadows.Parse($"0 0 24 2 {Alpha(c, 0x80)}");
        res["AccentGlowPressed"] = BoxShadows.Parse($"0 0 12 0 {Alpha(c, 0x66)}");
        res["CardShadow"] = BoxShadows.Parse(_light ? $"0 12 40 0 #30000000, 0 0 30 0 {Alpha(c, 0x18)}" : $"0 18 60 0 #A0000000, 0 0 40 0 {Alpha(c, 0x14)}");
        res["NeonTextBrush"] = Gradient(Secondary, c, horizontal: true);
        res["NeonBarBrush"] = Gradient(Secondary, c, horizontal: true);
        res["NeonGlowEffect"] = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 12, Color = Color.FromArgb(0x55, c.R, c.G, c.B) };

        TileBrush.GradientStops[0].Color = Secondary;
        TileBrush.GradientStops[1].Color = c;
        MarkInk.Color = OnAccent;
        Skin.SetAccent(c, Secondary, Mix);
        Changed?.Invoke();
    }

    /// <summary>The accent laid over the dark background at <paramref name="amount"/> strength.</summary>
    public static Color Mix(double amount) => Color.FromRgb(
        (byte)(Background.R + (Accent.R - Background.R) * amount),
        (byte)(Background.G + (Accent.G - Background.G) * amount),
        (byte)(Background.B + (Accent.B - Background.B) * amount));

    /// <summary>Lighter (positive) or darker (negative) in HSL lightness.</summary>
    public static Color Shade(Color c, double amount)
    {
        var hsl = c.ToHsl();
        return new HslColor(1, hsl.H, hsl.S, Math.Clamp(hsl.L + amount, 0, 1)).ToRgb();
    }

    private static string Alpha(Color c, byte a) => $"#{a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static LinearGradientBrush Gradient(Color from, Color to, bool horizontal) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, horizontal ? 0 : 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };
}
