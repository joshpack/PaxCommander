using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using pxdCommander.Core;

namespace pxdCommander.Views;

/// <summary>
/// Remembered commands for the terminal: a suggestion list while you type (your snippets, the commands you've run
/// before, and common admin commands for whatever you're connected to), a Commands menu to browse them, and a
/// full-size toggle that gives the terminal the whole tab.
/// </summary>
public sealed partial class TerminalPane
{
    private Popup _suggestPopup = null!;
    private ListBox _suggestList = null!;
    private bool _suppressSuggest;
    /// <summary>Library contexts switched on in this terminal (connected Microsoft 365 services).</summary>
    private readonly HashSet<string> _contexts = [];
    private Button _maximize = null!;

    /// <summary>The ⤢ button: the tab gives the terminal all its room, or back.</summary>
    public event Action? MaximizeRequested;

    private string Kind => _spec.Local ? "powershell" : "ssh";

    /// <summary>What the suggestion list shows right now (empty when it's closed).</summary>
    public IReadOnlyList<CommandSuggestion> Suggestions =>
        _suggestPopup.IsOpen && _suggestList.ItemsSource is IEnumerable<CommandSuggestion> items ? items.ToList() : [];

    public string InputText => _input.Text ?? "";
    public string InputSelection => _input.SelectedText ?? "";

    private const string MaximizeGlyph = "M1,4 L1,1 L4,1 M8,1 L11,1 L11,4 M11,8 L11,11 L8,11 M4,11 L1,11 L1,8";
    private const string RestoreGlyph = "M4,1 L4,4 L1,4 M8,1 L8,4 L11,4 M11,8 L8,8 L8,11 M4,11 L4,8 L1,8";
    // A box with an arrow leaving it (pop out) / entering it (dock back).
    private const string PopOutGlyph = "M5,2 L1,2 L1,11 L10,11 L10,7 M7,1 L11,1 L11,5 M11,1 L5.5,6.5";
    private const string DockGlyph = "M5,2 L1,2 L1,11 L10,11 L10,7 M11,1 L5.5,6.5 M5.5,2.5 L5.5,6.5 L9.5,6.5";
    private Button _popOut = null!;

    /// <summary>The pop-out button: move the terminal into its own window, or back into the tab.</summary>
    public event Action? PopOutRequested;

    /// <summary>Builds the Commands / full-size header buttons and the suggestion popup (called from the constructor).</summary>
    private void InitCommands(StackPanel headerButtons, Control inputRow)
    {
        _history.AddRange(CommandLibrary.History(Kind));
        _historyAt = _history.Count;

        var commands = new Button { Content = "Commands", FontSize = 11.5, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(commands, "Saved snippets and common commands for what you're connected to");
        commands.Click += (_, _) => ShowCommandsMenu(commands);
        _maximize = new Button { Content = CaptionPath(MaximizeGlyph), Width = 26, Height = 24, Padding = new Thickness(0) };
        _maximize.Classes.Add("icon");
        ToolTip.SetTip(_maximize, "Full size: give the terminal the whole tab (Ctrl+Shift+`)");
        _maximize.Click += (_, _) => MaximizeRequested?.Invoke();
        _popOut = new Button { Content = CaptionPath(PopOutGlyph), Width = 26, Height = 24, Padding = new Thickness(0) };
        _popOut.Classes.Add("icon");
        ToolTip.SetTip(_popOut, "Pop out into its own window (the same session keeps running)");
        _popOut.Click += (_, _) => PopOutRequested?.Invoke();
        headerButtons.Children.Insert(0, commands);
        headerButtons.Children.Insert(headerButtons.Children.Count - 1, _maximize);
        headerButtons.Children.Insert(headerButtons.Children.Count - 1, _popOut);

        _suggestList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 260,
            ItemTemplate = new FuncDataTemplate<CommandSuggestion>((s, _) => s is null ? new Panel() : SuggestionRow(s)),
        };
        _suggestList.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_suggestList.SelectedItem is CommandSuggestion s) Accept(s);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _suggestPopup = new Popup
        {
            PlacementTarget = inputRow,
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = Skin.Popup,
                BorderBrush = Skin.PopupBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Child = _suggestList,
            },
        };
        ((ISetLogicalParent)_suggestPopup).SetParent(this);
        _input.TextChanged += (_, _) => UpdateSuggestions();
        _input.LostFocus += (_, _) =>
        {
            if (!_suggestList.IsKeyboardFocusWithin) _suggestPopup.IsOpen = false;
        };
    }

    private static Control CaptionPath(string data) =>
        new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Width = 12, Height = 12, Stroke = Skin.Muted, StrokeThickness = 1.4 };

    /// <summary>Popped out into its own window: the button docks it back, and "full size" has no meaning there.</summary>
    public void SetPoppedOut(bool poppedOut)
    {
        _popOut.Content = CaptionPath(poppedOut ? DockGlyph : PopOutGlyph);
        ToolTip.SetTip(_popOut, poppedOut ? "Dock back into the tab" : "Pop out into its own window (the same session keeps running)");
        _maximize.IsVisible = !poppedOut;
    }

    public void SetMaximized(bool maximized)
    {
        _maximize.Content = CaptionPath(maximized ? RestoreGlyph : MaximizeGlyph);
        ToolTip.SetTip(_maximize, maximized ? "Back to normal size (Ctrl+Shift+`)" : "Full size: give the terminal the whole tab (Ctrl+Shift+`)");
    }

    private static Control SuggestionRow(CommandSuggestion s)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 1) };
        grid.Children.Add(new TextBlock { Text = s.Command, FontFamily = Skin.Mono, FontSize = 12, Foreground = Skin.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var tag = new TextBlock
        {
            Text = s.Source == "history" ? s.Description : s.Source == "★" ? "★ snippet" : $"{s.Description} · {s.Source}",
            FontSize = 11,
            Foreground = s.Source == "★" ? Skin.Accent : Skin.Faint,
            Margin = new Thickness(14, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 280,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(tag, 1);
        grid.Children.Add(tag);
        return grid;
    }

    private void UpdateSuggestions()
    {
        if (_suppressSuggest || _input.PasswordChar != '\0')
        {
            _suggestPopup.IsOpen = false;
            return;
        }
        var items = CommandLibrary.Suggest(_input.Text ?? "", Kind, _contexts);
        if (items.Count == 0)
        {
            _suggestPopup.IsOpen = false;
            return;
        }
        _suggestList.ItemsSource = items;
        _suggestList.SelectedIndex = -1;
        _suggestList.Width = Math.Max(360, _input.Bounds.Width - 8);
        _suggestPopup.IsOpen = true;
    }

    /// <summary>Arrow keys pick, Tab or Enter takes the picked one, Escape closes. True when the key was used.</summary>
    private bool SuggestionKey(KeyEventArgs e)
    {
        if (!_suggestPopup.IsOpen || _suggestList.ItemCount == 0) return false;
        switch (e.Key)
        {
            case Key.Down:
                _suggestList.SelectedIndex = Math.Min(_suggestList.ItemCount - 1, _suggestList.SelectedIndex + 1);
                _suggestList.ScrollIntoView(_suggestList.SelectedIndex);
                return true;
            case Key.Up when _suggestList.SelectedIndex >= 0:
                _suggestList.SelectedIndex--;
                if (_suggestList.SelectedIndex >= 0) _suggestList.ScrollIntoView(_suggestList.SelectedIndex);
                return true;
            case Key.Tab:
                Accept((_suggestList.SelectedItem ?? _suggestList.Items[0]) as CommandSuggestion);
                return true;
            case Key.Enter when _suggestList.SelectedItem is CommandSuggestion picked:
                Accept(picked);
                return true;
            case Key.Escape:
                _suggestPopup.IsOpen = false;
                return true;
        }
        return false;
    }

    /// <summary>Puts a command in the input (it isn't run) and selects its first &lt;placeholder&gt; to type over.</summary>
    private void Accept(CommandSuggestion? s)
    {
        _suggestPopup.IsOpen = false;
        if (s is null) return;
        Insert(s.Command);
    }

    private void Insert(string command)
    {
        _suppressSuggest = true;
        _input.Text = command;
        _suppressSuggest = false;
        var start = command.IndexOf('<');
        var end = start >= 0 ? command.IndexOf('>', start) : -1;
        _input.Focus();
        if (start >= 0 && end > start)
        {
            _input.SelectionStart = start;
            _input.SelectionEnd = end + 1;
        }
        else _input.CaretIndex = command.Length;
    }

    /// <summary>Remembers a run command and switches on library sections for services it connects to.</summary>
    private void NoteCommand(string command, bool remember)
    {
        if (CommandLibrary.ContextForConnect(command) is { } ctx) _contexts.Add(ctx);
        if (remember) CommandLibrary.Remember(Kind, command);
    }

    // ---- Commands menu ---------------------------------------------------------------------------------------------------

    private void ShowCommandsMenu(Control anchor)
    {
        var menu = new MenuFlyout();
        var line = (_input.Text ?? "").Trim();
        var save = new MenuItem { Header = "Save the typed line as a snippet", IsEnabled = line.Length > 0 && !CommandLibrary.LooksSecret(line) };
        save.Click += (_, _) =>
        {
            CommandLibrary.AddSnippet(line);
            Toasts.Show("Snippet saved. It's suggested first from now on.", ToastKind.Success);
        };
        menu.Items.Add(save);

        var snippets = CommandLibrary.Snippets;
        if (snippets.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var s in snippets.Take(15)) menu.Items.Add(CommandItem(s, "★ snippet"));
            var remove = new MenuItem { Header = "Remove a snippet" };
            foreach (var s in snippets)
            {
                var mi = new MenuItem { Header = s };
                mi.Click += (_, _) => CommandLibrary.RemoveSnippet(s);
                remove.Items.Add(mi);
            }
            menu.Items.Add(remove);
        }

        // Connected services first, at the top level; everything else under "More commands".
        menu.Items.Add(new Separator());
        var first = _contexts.Append(Kind).Distinct().Where(CommandLibrary.Library.ContainsKey).ToList();
        foreach (var ctx in first) menu.Items.Add(LibraryMenu(ctx));
        var more = new MenuItem { Header = "More commands" };
        foreach (var ctx in CommandLibrary.Library.Keys.Where(k => !first.Contains(k) && (k == "ssh") == !_spec.Local || k is not ("ssh" or "powershell") && _spec.Local))
            more.Items.Add(LibraryMenu(ctx));
        if (more.Items.Count > 0) menu.Items.Add(more);

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear remembered commands" };
        clear.Click += (_, _) =>
        {
            CommandLibrary.ClearHistory(Kind);
            _history.Clear();
            _historyAt = 0;
            Toasts.Show("Command history cleared.", ToastKind.Success);
        };
        menu.Items.Add(clear);
        menu.ShowAt(anchor);
    }

    private MenuItem LibraryMenu(string ctx)
    {
        var (title, commands) = CommandLibrary.Library[ctx];
        var sub = new MenuItem { Header = title + (_contexts.Contains(ctx) ? "  · connected" : "") };
        foreach (var (cmd, desc) in commands) sub.Items.Add(CommandItem(cmd, desc));
        return sub;
    }

    private MenuItem CommandItem(string command, string description)
    {
        var mi = new MenuItem
        {
            Header = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = description, FontSize = 12.5 },
                    new TextBlock { Text = command, FontFamily = Skin.Mono, FontSize = 11, Foreground = Skin.Faint, MaxWidth = 560, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        ToolTip.SetTip(mi, command);
        mi.Click += (_, _) => Insert(command);
        return mi;
    }
}
