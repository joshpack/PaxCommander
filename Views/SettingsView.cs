using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>The settings card. Every change applies immediately and is saved.</summary>
public static class SettingsView
{
    public static Task ShowAsync(Action applied) => Dialogs.ShowAsync<bool>(tcs =>
    {
        var s = AppSettings.Current;
        void Changed()
        {
            AppSettings.SaveSoon();
            applied();
        }

        Control Toggle(string title, string? detail, bool value, Action<bool> set)
        {
            var sw = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            sw.IsCheckedChanged += (_, _) =>
            {
                set(sw.IsChecked == true);
                Changed();
            };
            return Row(title, detail, sw);
        }

        Control Choice<T>(string title, string? detail, T value, (string Label, T Value)[] options, Action<T> set) where T : notnull
        {
            var box = new ComboBox { ItemsSource = options.Select(o => o.Label).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value.Equals(value))), MinWidth = 170 };
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                set(options[box.SelectedIndex].Value);
                Changed();
            };
            return Row(title, detail, box);
        }

        static Control Row(string title, string? detail, Control control)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title });
            if (detail is not null) text.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Skin.Faint, TextWrapping = TextWrapping.Wrap });
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
            g.Children.Add(text);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(16, 0, 0, 0);
            g.Children.Add(control);
            return g;
        }

        static TextBlock Section(string text) => new() { Text = text, Classes = { "label" }, Margin = new Thickness(0, 14, 0, 2) };

        var terminal = new TextBox { Text = s.TerminalCommand, PlaceholderText = "Windows Terminal, else PowerShell", Width = 240 };
        terminal.LostFocus += (_, _) =>
        {
            s.TerminalCommand = terminal.Text?.Trim() ?? "";
            Changed();
        };

        var panel = new StackPanel
        {
            Children =
            {
                Dialogs.Title("Settings"),
                Section("FILES"),
                Toggle("Show hidden files", "Dot-files and items marked hidden (Ctrl+H)", s.ShowHidden, v => s.ShowHidden = v),
                Toggle("Show protected system files", "Hidden + system items such as desktop.ini", s.ShowSystemFiles, v => s.ShowSystemFiles = v),
                Choice("Folder sizes", "Adds up folders in the background and shows size bars", s.FolderSizes,
                    [("Off", FolderSizeMode.Off), ("Local drives", FolderSizeMode.LocalDrives), ("Everywhere (incl. network)", FolderSizeMode.Everywhere)], v => s.FolderSizes = v),
                Toggle("Ask before moving to the Recycle Bin", "Permanent deletes always ask", s.ConfirmRecycle, v => s.ConfirmRecycle = v),
                Section("LOOK"),
                Choice("Theme", "Dark, light, or follow Windows' own setting", s.Theme, [("Dark", "Dark"), ("Light", "Light"), ("Match Windows", "System")], v =>
                {
                    s.Theme = v;
                    ThemeManager.Apply(v);
                }),
                AccentPicker(Changed),
                Choice("New tabs open in", null, s.DefaultView,
                    [("Details", ViewMode.Details), ("Columns", ViewMode.Columns), ("Thumbnails", ViewMode.Tiles)], v => s.DefaultView = v),
                Toggle("Compact rows", null, s.CompactRows, v => s.CompactRows = v),
                Toggle("Friendly dates", "“Today 14:32”, “Yesterday”, “Mon 09:10”", s.RelativeDates, v => s.RelativeDates = v),
                Toggle("Size bars", "Proportional bars in the Size column", s.ShowSizeBars, v => s.ShowSizeBars = v),
                Toggle("Favorite hearts", "A heart beside each folder: click it to add or remove the folder from favorites", s.FavoriteHearts, v => s.FavoriteHearts = v),
                Toggle("Folders before files", null, s.FoldersFirst, v => s.FoldersFirst = v),
                Section("BEHAVIOUR"),
                Toggle("Open items with a single click", null, s.SingleClickOpen, v => s.SingleClickOpen = v),
                Toggle("Reopen my tabs at start", null, s.RestoreSession, v => s.RestoreSession = v),
                Row("Terminal command", "{path} is replaced by the folder", terminal),
                new TextBlock
                {
                    Text = $"PAX Commander {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)} · settings in {AppPaths.Data}",
                    Foreground = Skin.Faint,
                    FontSize = 11,
                    Margin = new Thickness(0, 16, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                },
                Dialogs.Buttons(Dialogs.Btn("Done", "primary", () => tcs.TrySetResult(true))),
            },
        };
        return new ScrollViewer { Content = panel, MaxHeight = 640 };
    }, false, width: 560, focusInput: false);

    /// <summary>
    /// Accent colour: preset swatches plus any #RRGGBB. Applies live, so the whole window (this card included)
    /// previews the colour as soon as it's picked.
    /// </summary>
    private static Control AccentPicker(Action changed)
    {
        var s = AppSettings.Current;
        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var custom = new TextBox { Text = s.AccentColor, Width = 110, MinHeight = 30, Height = 30, PlaceholderText = "#RRGGBB", FontFamily = Skin.Mono, FontSize = 12 };
        var rings = new List<(Border Ring, string Hex)>();

        void Pick(string hex)
        {
            if (!AccentTheme.TryParse(hex, out var color)) return;
            hex = AccentTheme.ToHex(color);
            s.AccentColor = hex;
            AccentTheme.Apply(hex);
            if (custom.Text?.Trim().Equals(hex, StringComparison.OrdinalIgnoreCase) != true) custom.Text = hex;
            foreach (var (ring, h) in rings)
                ring.BorderBrush = h.Equals(hex, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent;
            changed();
        }

        foreach (var (name, hex) in AccentTheme.Presets)
        {
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.Parse(hex)) };
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = hex.Equals(s.AccentColor, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent,
                Child = dot,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Background = Brushes.Transparent,
            };
            ToolTip.SetTip(ring, name);
            var value = hex;
            ring.PointerPressed += (_, e) =>
            {
                Pick(value);
                e.Handled = true;
            };
            rings.Add((ring, hex));
            swatches.Children.Add(ring);
        }
        custom.TextChanged += (_, _) =>
        {
            // Only complete colours apply, so typing doesn't flash through half-typed ones.
            var text = custom.Text?.Trim() ?? "";
            if (text.TrimStart('#').Length == 6 && AccentTheme.TryParse(text, out _)) Pick(text);
        };
        swatches.Children.Add(custom);

        var title = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Accent colour" },
                new TextBlock { Text = "Buttons, selection, highlights and the app icon", FontSize = 11.5, Foreground = Skin.Faint },
            },
        };
        return new StackPanel { Margin = new Thickness(0, 6), Children = { title, swatches } };
    }
}
