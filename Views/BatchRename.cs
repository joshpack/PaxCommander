using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using pxdCommander.Core;

namespace pxdCommander.Views;

/// <summary>
/// Renames many items at once with a live preview: find/replace (optionally regex), a name template with counters
/// and dates, and case changes. Clashes are flagged before anything is touched.
/// </summary>
public static class BatchRename
{
    private sealed record Row(FileItem Item, string NewName, string? Problem);

    public static Task<List<(FileItem Item, string NewName)>?> ShowAsync(IReadOnlyList<FileItem> items) =>
        Dialogs.ShowAsync<List<(FileItem, string)>?>(tcs =>
        {
            var find = new TextBox { PlaceholderText = "Find (in the name, not the extension)" };
            var replace = new TextBox { PlaceholderText = "Replace with" };
            var regex = new CheckBox { Content = "Regular expression" };
            var matchCase = new CheckBox { Content = "Match case" };
            var template = new TextBox { Text = "{name}", PlaceholderText = "{name}" };
            var start = new NumericUpDown { Value = 1, Minimum = 0, Maximum = 1_000_000, Increment = 1, FormatString = "0", Width = 120 };
            var caseBox = new ComboBox { ItemsSource = new[] { "Keep case", "lower case", "UPPER CASE", "Title Case" }, SelectedIndex = 0, Width = 150 };
            var summary = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 4) };
            var list = new ListBox
            {
                Height = 260,
                Background = Skin.Input,
                ItemTemplate = new FuncDataTemplate<Row>((r, _) =>
                {
                    // Recycled containers are re-templated with null.
                    if (r is null) return new Panel();
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
                    g.Children.Add(new TextBlock { Text = r.Item.Name, Foreground = Skin.Muted, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
                    var arrow = new TextBlock { Text = "→", Foreground = Skin.Faint, Margin = new Thickness(8, 0), FontSize = 12 };
                    Grid.SetColumn(arrow, 1);
                    g.Children.Add(arrow);
                    var to = new TextBlock
                    {
                        Text = r.Problem is null ? r.NewName : $"{r.NewName}  ({r.Problem})",
                        Foreground = r.Problem is not null ? Skin.Bad : r.NewName == r.Item.Name ? Skin.Faint : Skin.Accent,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontSize = 12,
                    };
                    Grid.SetColumn(to, 2);
                    g.Children.Add(to);
                    return g;
                }),
            };
            var ok = Dialogs.Btn("Rename", "primary");
            List<Row> rows = [];

            void Update()
            {
                rows = Compute(items, find.Text ?? "", replace.Text ?? "", regex.IsChecked == true, matchCase.IsChecked == true,
                    template.Text ?? "{name}", (int)(start.Value ?? 1), caseBox.SelectedIndex, out var error);
                list.ItemsSource = rows;
                var changed = rows.Count(r => r.NewName != r.Item.Name);
                var problems = rows.Count(r => r.Problem is not null);
                summary.Text = error ?? (problems > 0 ? $"{problems} problem{(problems == 1 ? "" : "s")} — fix them to continue" : $"{changed} of {rows.Count} will change");
                summary.Foreground = error is not null || problems > 0 ? Skin.Bad : Skin.Muted;
                ok.IsEnabled = error is null && problems == 0 && changed > 0;
            }

            foreach (var tb in new[] { find, replace, template }) tb.TextChanged += (_, _) => Update();
            regex.IsCheckedChanged += (_, _) => Update();
            matchCase.IsCheckedChanged += (_, _) => Update();
            start.ValueChanged += (_, _) => Update();
            caseBox.SelectionChanged += (_, _) => Update();
            ok.Click += (_, _) => tcs.TrySetResult(rows.Where(r => r.NewName != r.Item.Name).Select(r => (r.Item, r.NewName)).ToList());

            static Control Labeled(string label, Control c) => new StackPanel
            {
                Spacing = 4,
                Children = { new TextBlock { Text = label, Classes = { "label" } }, c },
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto") };
            grid.Children.Add(Labeled("FIND", find));
            var r1 = Labeled("REPLACE", replace);
            Grid.SetColumn(r1, 2);
            grid.Children.Add(r1);
            var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { regex, matchCase } };
            Grid.SetRow(opts, 2);
            Grid.SetColumnSpan(opts, 3);
            grid.Children.Add(opts);
            var tpl = Labeled("NAME TEMPLATE  ·  {name} {ext} {n} {n:000} {date} {date:yyyyMMdd} {parent}", template);
            Grid.SetRow(tpl, 4);
            grid.Children.Add(tpl);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom, Children = { Labeled("START AT", start), Labeled("CASE", caseBox) } };
            Grid.SetRow(right, 4);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            Update();
            return new StackPanel
            {
                Children =
                {
                    Dialogs.Title($"Rename {Formatting.Count(items.Count, "item")}"),
                    grid,
                    summary,
                    list,
                    Dialogs.Buttons(Dialogs.Btn("Cancel", null, () => tcs.TrySetResult(null)), ok),
                },
            };
        }, null, width: 720);

    private static List<Row> Compute(IReadOnlyList<FileItem> items, string find, string replace, bool useRegex, bool matchCase,
        string template, int start, int caseMode, out string? error)
    {
        error = null;
        Regex? rx = null;
        if (find.Length > 0 && useRegex)
        {
            try
            {
                rx = new Regex(find, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                error = "Invalid regular expression: " + ex.Message;
            }
        }
        var width = Math.Max(1, (start + items.Count - 1).ToString(CultureInfo.InvariantCulture).Length);
        var result = new List<Row>(items.Count);
        var names = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var ext = item.IsDirectory ? "" : Path.GetExtension(item.Name);
            var stem = item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
            if (find.Length > 0 && error is null)
            {
                try
                {
                    stem = rx is not null ? rx.Replace(stem, replace)
                        : stem.Replace(find, replace, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException)
                {
                    error = "The regular expression is too slow.";
                }
            }
            var name = ApplyTemplate(template, stem, ext, item, start + i, width);
            name = caseMode switch
            {
                1 => name.ToLower(CultureInfo.CurrentCulture),
                2 => name.ToUpper(CultureInfo.CurrentCulture),
                3 => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower(CultureInfo.CurrentCulture)),
                _ => name,
            };
            // The extension stays unless the template placed it itself.
            if (!template.Contains("{ext}", StringComparison.OrdinalIgnoreCase)) name += ext;
            names.Add(name.Trim());
        }

        var counts = names.GroupBy(n => n, PathUtil.Comparer).ToDictionary(g => g.Key, g => g.Count(), PathUtil.Comparer);
        var renamed = new HashSet<string>(items.Select(i => i.Path), PathUtil.Comparer);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = names[i];
            string? problem = PathUtil.ValidateName(name);
            if (problem is null && counts[name] > 1) problem = "duplicate";
            if (problem is null && !string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase))
            {
                var target = Path.Combine(Path.GetDirectoryName(item.Path) ?? "", name);
                // Taken by something that isn't itself being renamed away.
                if (PathUtil.Exists(target) && !renamed.Contains(target)) problem = "already exists";
            }
            result.Add(new Row(item, name, problem));
        }
        return result;
    }

    private static string ApplyTemplate(string template, string stem, string ext, FileItem item, int n, int width)
    {
        return Regex.Replace(template, @"\{(name|ext|n|date|parent)(?::([^}]*))?\}", m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
            return key switch
            {
                "name" => stem,
                "ext" => ext.TrimStart('.'),
                "n" => fmt is not null && fmt.All(c => c == '0') ? n.ToString(new string('0', fmt.Length), CultureInfo.InvariantCulture)
                    : fmt is null ? n.ToString(new string('0', width), CultureInfo.InvariantCulture)
                    : n.ToString(fmt, CultureInfo.InvariantCulture),
                "date" => item.Modified.ToString(fmt ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                "parent" => Path.GetFileName(Path.GetDirectoryName(item.Path) ?? ""),
                _ => m.Value,
            };
        }, RegexOptions.IgnoreCase);
    }
}
