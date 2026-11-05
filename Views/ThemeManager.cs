using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>
/// Light and dark themes. One palette per theme feeds the app-wide style resources (App.axaml reads them as
/// DynamicResource), the brushes drawn in code (<see cref="Skin"/>), the code-preview colours and the accent mixes.
/// Everything recolours in place, so switching is instant and nothing has to be rebuilt.
/// "System" follows Windows' own light/dark setting, live.
/// </summary>
public static class ThemeManager
{
    public static readonly (string Name, string Value)[] Choices = [("Dark", "Dark"), ("Light", "Light"), ("Match Windows", "System")];

    private static bool _listening;

    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    /// <summary>Dark (the original look), Light, or System.</summary>
    public static void Apply(string? mode)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = mode switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
        if (!_listening)
        {
            _listening = true;
            app.ActualThemeVariantChanged += (_, _) => Paint(app.ActualThemeVariant == ThemeVariant.Light);
        }
        Paint(app.ActualThemeVariant == ThemeVariant.Light);
    }

    private static void Paint(bool light)
    {
        IsLight = light;
        var p = light ? LightPalette : DarkPalette;
        var res = Application.Current!.Resources;
        foreach (var (key, hex) in p)
            if (!key.StartsWith('_')) res[key] = new SolidColorBrush(Color.Parse(hex));
        res["WindowBgBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(p["_WindowTop"]), 0),
                new GradientStop(Color.Parse(p["_WindowMid"]), 0.55),
                new GradientStop(Color.Parse(p["_WindowEnd"]), 1),
            },
        };

        Color C(string key) => Color.Parse(p[key]);
        Skin.Text.Color = C("TextBrush");
        Skin.Muted.Color = C("MutedBrush");
        Skin.Faint.Color = C("FaintBrush");
        Skin.Input.Color = C("InputBrush");
        Skin.InputBorder.Color = C("BorderBrush2");
        Skin.CodeBg.Color = C("_Code");
        Skin.CodeBorder.Color = C("_CodeBorder");
        Skin.CodeText.Color = C("_CodeText");
        Skin.Panel.Color = C("PaneBrush");
        Skin.PanelBorder.Color = C("PaneBorderBrush");
        Skin.Line.Color = C("_Line");
        Skin.Separator.Color = C("_Separator");
        Skin.Popup.Color = C("MenuFlyoutPresenterBackground");
        Skin.PopupBorder.Color = C("MenuFlyoutPresenterBorderBrush");
        Skin.Visual.Color = C("_Visual");
        Skin.TileBg.Color = C("_TileBg");
        Skin.TileText.Color = C("_TileText");
        Skin.RowHover.Color = C("_RowHover");
        Skin.RowSelectedInactive.Color = C("_RowSelectedInactive");
        Skin.HeaderBg.Color = C("_HeaderBg");
        Skin.Track.Color = C("_Line");
        Skin.SetThemeLines(light, C("PaneBorderBrush"), C("FaintBrush"));
        CodeView.ApplyTheme(light);

        // Accent mixes (selection, hovers, glows) are laid over this theme's background.
        AccentTheme.SetBackground(C("PaneBrush"), light);
        AccentTheme.Apply(AppSettings.Current.AccentColor);
        Changed?.Invoke();
    }

    // Keys without "_" are App.axaml resources; "_" keys are only used in code.
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["_WindowTop"] = "#0D1117", ["_WindowMid"] = "#0B0F14", ["_WindowEnd"] = "#091315",
        ["BgBrush"] = "#0E1015", ["SurfaceBrush"] = "#161922", ["SurfaceRaisedBrush"] = "#1D212C", ["BorderBrush2"] = "#272C39",
        ["TextBrush"] = "#E8EAF0", ["MutedBrush"] = "#8A92A6", ["FaintBrush"] = "#5B6274",
        ["PaneBrush"] = "#0F1218", ["PaneBorderBrush"] = "#1E2430", ["CardBrush"] = "#151922", ["CardBorderBrush"] = "#2A3142",
        ["InputBrush"] = "#0B0E13", ["ButtonHoverBrush"] = "#232A38", ["ButtonHoverBorderBrush"] = "#2B7C9A",
        ["IconHoverBrush"] = "#1A222C", ["SegHoverBrush"] = "#1C2430", ["SideHoverBrush"] = "#18202A", ["TileHoverBorderBrush"] = "#3A4454",
        ["CaptionHoverBrush"] = "#1C232E", ["CaptionPressedBrush"] = "#262E3A", ["CrumbTextBrush"] = "#C9CEDA", ["CrumbHoverBrush"] = "#1A2430",
        ["HoverTextBrush"] = "#FFFFFF", ["TabHoverBrush"] = "#161D26", ["TabSelectedBrush"] = "#1A222D", ["TrackBrush"] = "#1C222C",
        ["SegCheckedBrush"] = "#18212B", ["ScrollThumbBrush"] = "#323A49", ["ScrollThumbHoverBrush"] = "#55617A",
        ["DangerBrush"] = "#3A1A1E", ["DangerBorderBrush"] = "#6E2A31", ["DangerTextBrush"] = "#FFB3B6", ["DangerHoverBrush"] = "#55232A",
        ["MenuFlyoutPresenterBackground"] = "#171B24", ["MenuFlyoutPresenterBorderBrush"] = "#2A3040",
        ["MenuFlyoutItemBackgroundPointerOver"] = "#1F2A33", ["MenuFlyoutItemForegroundPointerOver"] = "#FFFFFF",
        ["MenuFlyoutSubItemBackgroundPointerOver"] = "#1F2A33", ["MenuFlyoutSubItemBackgroundSubMenuOpened"] = "#1F2A33",
        ["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = "#6F778A", ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = "#9AA3B6",
        ["ToolTipBackground"] = "#1B202A", ["ToolTipBorderBrush"] = "#2E3647", ["ToolTipForeground"] = "#E8EAF0",
        ["_Code"] = "#0A0D11", ["_CodeBorder"] = "#1C222D", ["_CodeText"] = "#C9D1DE", ["_Line"] = "#1A2029", ["_Separator"] = "#232A36",
        ["_Visual"] = "#0C0F14", ["_TileBg"] = "#10141A", ["_TileText"] = "#D3D7E0", ["_RowHover"] = "#141A22",
        ["_RowSelectedInactive"] = "#1C2430", ["_HeaderBg"] = "#11151C",
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["_WindowTop"] = "#F3F6FA", ["_WindowMid"] = "#EEF2F7", ["_WindowEnd"] = "#E9F0F5",
        ["BgBrush"] = "#FFFFFF", ["SurfaceBrush"] = "#F7F9FC", ["SurfaceRaisedBrush"] = "#FFFFFF", ["BorderBrush2"] = "#D3DAE4",
        ["TextBrush"] = "#1A1F2B", ["MutedBrush"] = "#556074", ["FaintBrush"] = "#8891A3",
        ["PaneBrush"] = "#FFFFFF", ["PaneBorderBrush"] = "#DCE2EB", ["CardBrush"] = "#FFFFFF", ["CardBorderBrush"] = "#D3DAE4",
        ["InputBrush"] = "#F3F5F9", ["ButtonHoverBrush"] = "#EDF2F8", ["ButtonHoverBorderBrush"] = "#7DB9D8",
        ["IconHoverBrush"] = "#E8EEF5", ["SegHoverBrush"] = "#E8EEF5", ["SideHoverBrush"] = "#E8EEF5", ["TileHoverBorderBrush"] = "#B5C0D0",
        ["CaptionHoverBrush"] = "#E4E9F0", ["CaptionPressedBrush"] = "#D8DFE9", ["CrumbTextBrush"] = "#3A4357", ["CrumbHoverBrush"] = "#E8EEF5",
        ["HoverTextBrush"] = "#000000", ["TabHoverBrush"] = "#EDF2F8", ["TabSelectedBrush"] = "#E4EBF4", ["TrackBrush"] = "#E1E7EF",
        ["SegCheckedBrush"] = "#E1EEF6", ["ScrollThumbBrush"] = "#C5CDD9", ["ScrollThumbHoverBrush"] = "#97A3B6",
        ["DangerBrush"] = "#FDECEC", ["DangerBorderBrush"] = "#EFA3A7", ["DangerTextBrush"] = "#B3262D", ["DangerHoverBrush"] = "#F9D8DA",
        ["MenuFlyoutPresenterBackground"] = "#FFFFFF", ["MenuFlyoutPresenterBorderBrush"] = "#D3DAE4",
        ["MenuFlyoutItemBackgroundPointerOver"] = "#E7F1F8", ["MenuFlyoutItemForegroundPointerOver"] = "#000000",
        ["MenuFlyoutSubItemBackgroundPointerOver"] = "#E7F1F8", ["MenuFlyoutSubItemBackgroundSubMenuOpened"] = "#E7F1F8",
        ["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = "#7A8396", ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = "#4F586A",
        ["ToolTipBackground"] = "#FFFFFF", ["ToolTipBorderBrush"] = "#D3DAE4", ["ToolTipForeground"] = "#1A1F2B",
        ["_Code"] = "#F7F9FC", ["_CodeBorder"] = "#DCE2EB", ["_CodeText"] = "#1F2430", ["_Line"] = "#E3E8EF", ["_Separator"] = "#D3DAE4",
        ["_Visual"] = "#EEF2F7", ["_TileBg"] = "#F6F8FB", ["_TileText"] = "#2A3142", ["_RowHover"] = "#EEF3F9",
        ["_RowSelectedInactive"] = "#E5EAF1", ["_HeaderBg"] = "#F7F9FC",
    };
}
