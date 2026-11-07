using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PaxCommander.Core;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Views;

/// <summary>Every command with its shortcut, the window-level key handling, and the "Go anywhere" palette.</summary>
public sealed partial class MainWindow
{
    private sealed record AppCommand(string Title, string Glyph, string? Gesture, Action Run, bool InTextBox = false)
    {
        public KeyGesture[] Keys { get; init; } = [];
    }

    private readonly List<AppCommand> _commands = [];

    private void BuildCommands()
    {
        void Add(string title, string glyph, string? gesture, Action run, bool inText = false, params string[] alsoKeys)
        {
            var keys = new List<KeyGesture>();
            if (gesture is not null) keys.Add(KeyGesture.Parse(gesture));
            keys.AddRange(alsoKeys.Select(KeyGesture.Parse));
            _commands.Add(new AppCommand(title, glyph, gesture, run, inText) { Keys = keys.ToArray() });
        }
        FolderView? V() => CurrentView;

        // Finding and moving around
        Add("Go anywhere…", "search", "Ctrl+K", () => ShowGoAnywhere(""), true, "Ctrl+P");
        Add("Command palette…", "command", "Ctrl+Shift+P", () => ShowGoAnywhere(">"), true);
        Add("Edit the path", "rename", "Ctrl+L", () => V()?.EditPath(), true, "Alt+D", "F4");
        Add("Filter this folder", "filter", "Ctrl+F", () => V()?.FocusFilter(), true, "F3");
        Add("Back", "back", "Alt+Left", () => V()?.GoBack(), true);
        Add("Forward", "forward", "Alt+Right", () => V()?.GoForward(), true);
        Add("Up one level", "up", "Alt+Up", () => V()?.GoUp(), true);
        Add("Refresh", "refresh", "F5", () => _ = V()?.RefreshAsync(), true, "Ctrl+R");
        Add("Go home", "home", "Alt+Home", () => _ = V()?.NavigateAsync(PathUtil.Home), true);

        // Tabs and panes
        Add("New tab", "plus", "Ctrl+T", () => _activePane.AddTab(V()?.ActiveFolder ?? PathUtil.Home), true);
        Add("Close tab", "close", "Ctrl+W", () => { if (V() is { } v) _activePane.CloseTab(v); }, true, "Ctrl+F4");
        Add("Reopen closed tab", "undo", "Ctrl+Shift+T", ReopenClosedTab, true);
        Add("Next tab", "forward", "Ctrl+Tab", () => _activePane.Cycle(1), true, "Ctrl+PageDown");
        Add("Previous tab", "back", "Ctrl+Shift+Tab", () => _activePane.Cycle(-1), true, "Ctrl+PageUp");
        for (var i = 1; i <= 9; i++)
        {
            var n = i;
            _commands.Add(new AppCommand($"Go to tab {n}", "folder", null, () =>
            {
                _activePane.Activate(n == 9 ? _activePane.Tabs.Count - 1 : n - 1);
                CurrentView?.FocusList();
            }, true) { Keys = [KeyGesture.Parse($"Ctrl+D{n}")] });
        }
        Add("Switch pane", "dual", null, () => FocusPane(OtherPane));
        Add("Two panes on/off", "dual", "Ctrl+Alt+D", () => SetDual(!AppSettings.Current.DualPane), true);
        Add("Swap panes", "swap", "Ctrl+U", SwapPanes, true);
        Add("Show this folder in the other pane", "dual", "Ctrl+Shift+M", MirrorToOtherPane, true);
        Add("Sidebar on/off", "sidebar", "Ctrl+B", () => SetSidebar(!AppSettings.Current.ShowSidebar), true);
        Add("Preview pane on/off", "preview", "Alt+P", () => SetPreview(!AppSettings.Current.ShowPreview), true);
        Add("Light / dark theme", "palette", "Ctrl+Alt+L", () =>
        {
            AppSettings.Current.Theme = ThemeManager.IsLight ? "Dark" : "Light";
            AppSettings.SaveSoon();
            ThemeManager.Apply(AppSettings.Current.Theme);
        }, true);
        Add("New window", "window", "Ctrl+N", () => App.NewWindow(V()?.ActiveFolder), true);

        // Views
        Add("Details view", "details", "Ctrl+Shift+D1", () => SetMode(ViewMode.Details), true);
        Add("Columns view", "columns", "Ctrl+Shift+D2", () => SetMode(ViewMode.Columns), true);
        Add("Thumbnails view", "tiles", "Ctrl+Shift+D3", () => SetMode(ViewMode.Tiles), true);
        Add("Show/hide hidden files", "eye", "Ctrl+H", () =>
        {
            AppSettings.Current.ShowHidden = !AppSettings.Current.ShowHidden;
            AppSettings.SaveSoon();
            ApplySettings();
            Toasts.Show(AppSettings.Current.ShowHidden ? "Showing hidden files" : "Hiding hidden files", seconds: 1.5);
        }, true);
        Add("Calculate folder sizes", "sigma", "Ctrl+Shift+S", () => V()?.MeasureSizes(force: true), true);
        Add("Sort by name", "sort", null, () => V()?.SetSort(SortKey.Name));
        Add("Sort by date modified", "sort", null, () => V()?.SetSort(SortKey.Modified));
        Add("Sort by size", "sort", null, () => V()?.SetSort(SortKey.Size));
        Add("Sort by type", "sort", null, () => V()?.SetSort(SortKey.Type));

        // Selection
        Add("Select all", "select", "Ctrl+A", () => V()?.ActiveSelection?.SelectAll());
        Add("Invert selection", "select", "Ctrl+I", () => V()?.ActiveSelection?.Invert());
        Add("Select none", "select", null, () => V()?.ActiveSelection?.Clear());
        Add("Quick Look", "eye", null, () => { if (V() is { } v) QuickLook(v); });
        Add("Slideshow of this folder", "play", null, () =>
        {
            if (V() is not { } v || PathUtil.IsVirtual(v.ActiveFolder)) return;
            var images = v.ActiveListing?.Items.Where(IsViewableImage).ToList() ?? [];
            if (images.Count == 0) Toasts.Show("There are no images here.");
            else ViewImages(v, [v.FocusedItem is { } f && IsViewableImage(f) ? f : images[0]], slideshow: true);
        });

        // Files
        Add("Copy", "copy", "Ctrl+C", () => _ = CopyToClipboardAsync(false));
        Add("Cut", "cut", "Ctrl+X", () => _ = CopyToClipboardAsync(true));
        Add("Paste", "paste", "Ctrl+V", () => _ = PasteAsync());
        Add("Copy path", "link", "Ctrl+Shift+C", () => _ = CopyPathsAsync());
        Add("Copy to other pane", "copy", "Shift+F5", () => CopyOrMoveToOtherPane(false));
        Add("Move to other pane", "swap", "Shift+F6", () => CopyOrMoveToOtherPane(true));
        Add("Rename", "rename", "F2", () => _ = RenameSelectionAsync());
        Add("Delete (Recycle Bin)", "trash", "Delete", () => _ = DeleteSelectionAsync(false));
        Add("Delete permanently", "trash", "Shift+Delete", () => _ = DeleteSelectionAsync(true));
        Add("New folder", "newfolder", "Ctrl+Shift+N", () => _ = NewFolderAsync(), true);
        Add("New text document", "doc", "Ctrl+Alt+N", () => _ = NewFileAsync("New text document", ".txt"), true);
        Add("Compress to ZIP", "archive", null, CompressSelection);
        Add("Undo", "undo", "Ctrl+Z", () => _ = UndoAsync());
        Add("Redo", "redo", "Ctrl+Y", () => _ = RedoAsync(), false, "Ctrl+Shift+Z");
        Add("Show or hide the terminal", "terminal", "Ctrl+OemTilde", () => V()?.ToggleTerminal(), true);
        Add("Open in Windows Terminal", "window", null, () =>
        {
            if (V()?.ActiveFolder is not { } f || PathUtil.IsVirtual(f)) return;
            if (Core.Remote.RemotePath.TryParse(f, out var conn, out _) && conn is not null)
            {
                if (conn.Protocol == Core.Remote.RemoteProtocol.Sftp) OpenSshTerminal(conn);
                return;
            }
            Try(() => Shell.OpenTerminal(f));
        }, true);
        Add("Show in Explorer", "window", "Ctrl+Shift+E", () =>
        {
            if (V() is not { } v) return;
            Try(() =>
            {
                if (v.FocusedItem is { } i) Shell.Reveal(i.Path);
                else if (!PathUtil.IsVirtual(v.ActiveFolder)) Shell.Open(v.ActiveFolder);
            });
        }, true);
        Add("Properties", "info", "Alt+Enter", () =>
        {
            if (V() is not { } v) return;
            var paths = v.SelectedItems.Select(i => i.Path).ToList();
            if (paths.Count == 0 && !PathUtil.IsVirtual(v.ActiveFolder)) paths = [v.ActiveFolder];
            Shell.Properties(Hwnd, paths);
        });
        Add("Add this folder to favorites", "star", null, () => { if (V()?.ActiveFolder is { } f && !PathUtil.IsVirtual(f)) _sidebar.AddFavorite(f); });
        if (OperatingSystem.IsWindows())
            Add("Open the Recycle Bin", "trash", null, () => Try(RecycleBin.Open));
        if (OperatingSystem.IsWindows())
            Add("Unmount a disc image (ISO)…", "eject", null, UnmountPicker);
        Add("Change the accent colour…", "palette", null, () => _ = SettingsView.ShowAsync(ApplySettings));
        Add("Settings", "settings", "Ctrl+OemComma", () => _ = SettingsView.ShowAsync(ApplySettings), true);
        Add("Check for updates", "download", null, () => _ = UpdatePrompt.CheckNowAsync(this));
        Add("Keyboard shortcuts", "command", "F1", () => ShowGoAnywhere(">"), true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _lastModifiers = e.KeyModifiers;
        // Dialogs, Quick Look and the palette own the keyboard while they're up.
        if (Dialogs.IsOpen || CommandPalette.Current is not null || PaxCommander.Views.QuickLook.Current is not null) return;

        var focused = FocusManager?.GetFocusedElement();
        var inText = focused is TextBox;

        // Tab flips between the panes, like every two-pane file manager.
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && !inText && AppSettings.Current.DualPane && focused is ItemViewBase)
        {
            FocusPane(OtherPane);
            e.Handled = true;
            return;
        }

        foreach (var cmd in _commands)
        {
            if (inText && !cmd.InTextBox) continue;
            if (!cmd.Keys.Any(k => k.Matches(e))) continue;
            // In the rename box and path editor, plain editing keys must reach the text box.
            if (inText && e.KeyModifiers == KeyModifiers.None && e.Key is not (Key.F1 or Key.F3 or Key.F4 or Key.F5)) continue;
            e.Handled = true;
            try
            {
                cmd.Run();
            }
            catch (Exception ex)
            {
                CrashLog.Write("command " + cmd.Title, ex);
                Toasts.Show(ex.Message, ToastKind.Error);
            }
            return;
        }
    }

    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        var t = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        _activePane.AddTab(t.Path, t.View, new SortSpec { Key = t.Sort, Descending = t.Descending, FoldersFirst = AppSettings.Current.FoldersFirst });
    }

    private void SwapPanes()
    {
        if (!AppSettings.Current.DualPane) return;
        var a = _panes[0].Current;
        var b = _panes[1].Current;
        if (a is null || b is null) return;
        var (pa, pb) = (a.CurrentPath, b.CurrentPath);
        _ = a.NavigateAsync(pb);
        _ = b.NavigateAsync(pa);
    }

    private void MirrorToOtherPane()
    {
        if (CurrentView is not { } v) return;
        if (!AppSettings.Current.DualPane) SetDual(true);
        _ = OtherPane.Current?.NavigateAsync(v.ActiveFolder);
    }

    /// <summary>Everything carrying a colour label, as search-style results.</summary>
    private void ShowLabelled(int label)
    {
        var paths = Labels.Snapshot().Where(x => x.Label == label).Select(x => x.Path).Where(PathUtil.Exists).ToList();
        if (paths.Count == 0)
        {
            Toasts.Show("Nothing has that label any more.");
            return;
        }
        ShowGoAnywhere("", paths.Select(p => new PaletteItem(Path.GetFileName(p), p, Directory.Exists(p) ? "folder" : "file", null,
            () => _ = CurrentView?.NavigateAsync(p), Skin.Label(label))).ToList(), $"{Labels.All[label].Name} label");
    }

    // ---- Go anywhere ---------------------------------------------------------------------------------------------------

    private void ShowGoAnywhere(string initial, IReadOnlyList<PaletteItem>? fixedItems = null, string? placeholder = null)
    {
        CommandPalette.Show(_dialogLayer, initial, placeholder ?? "Go to a folder, find a file, run a command…", query =>
        {
            if (fixedItems is not null)
                return fixedItems.Select(i => (i, Score: Fuzzy.Score(query, i.Title + " " + i.Detail))).Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score).Select(x => x.i).Take(80).ToList();
            return GoAnywhereResults(query);
        });
    }

    private List<PaletteItem> GoAnywhereResults(string query)
    {
        var results = new List<(PaletteItem Item, double Score)>();
        var q = query.Trim();
        var commandsOnly = q.StartsWith('>');
        if (commandsOnly) q = q[1..].Trim();

        foreach (var c in _commands.Where(c => !c.Title.StartsWith("Go to tab", StringComparison.Ordinal)))
        {
            var s = q.Length == 0 ? 1 : Fuzzy.Score(q, c.Title);
            if (s <= 0) continue;
            results.Add((new PaletteItem(c.Title, null, c.Glyph, Pretty(c.Gesture), c.Run, Skin.Info), commandsOnly ? s : s * 0.55));
        }
        if (commandsOnly)
            return results.OrderByDescending(r => r.Score).ThenBy(r => r.Item.Title).Select(r => r.Item).Take(120).ToList();

        var view = CurrentView;
        // A typed path goes straight there (with its subfolders offered as you type).
        if (LooksLikePath(q) && PathUtil.Resolve(q, view?.ActiveFolder) is { } resolved)
        {
            if (Directory.Exists(resolved) || File.Exists(resolved))
                results.Add((new PaletteItem($"Go to {resolved}", null, "forward", "Enter", () => _ = view?.NavigateAsync(resolved), Skin.Accent), 10_000));
            var parent = q.EndsWith('\\') || q.EndsWith('/') ? resolved : Path.GetDirectoryName(resolved);
            var prefix = q.EndsWith('\\') || q.EndsWith('/') ? "" : Path.GetFileName(resolved);
            if (parent is not null && Directory.Exists(parent))
            {
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(parent, prefix + "*", new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }).Take(30))
                    {
                        var dir = d;
                        results.Add((new PaletteItem(Path.GetFileName(dir), dir, "folder", null, () => _ = view?.NavigateAsync(dir), new SolidColorBrush(FallbackIcons.KindColor(FileKind.Folder))), 5_000));
                    }
                }
                catch
                {
                    // Unreadable folder: just the "go to" entry.
                }
            }
        }

        // Frequent folders, favorites and drives.
        var places = new Dictionary<string, (string Glyph, double Boost)>(PathUtil.Comparer);
        foreach (var (path, score) in Frecency.Ranked(300)) places[path] = ("clock", Math.Log(1 + score));
        foreach (var f in AppSettings.Current.Favorites) places[f] = ("star", 3);
        foreach (var d in DriveInfo.GetDrives()) places[d.RootDirectory.FullName] = ("drive", 1);
        foreach (var (path, (glyph, boost)) in places)
        {
            var name = PathUtil.DisplayName(path);
            var s = q.Length == 0 ? boost : Math.Max(Fuzzy.Score(q, name) * 1.6, Fuzzy.Score(q, path));
            if (s <= 0) continue;
            var target = path;
            results.Add((new PaletteItem(name, path, glyph, null, () =>
            {
                _ = view?.NavigateAsync(target);
                view?.FocusList();
            }, glyph == "star" ? Skin.Warn : null), s + boost));
        }

        // Items in the current folder: jump to them.
        if (q.Length > 0 && view?.ActiveListing is { } listing)
        {
            foreach (var item in listing.Items.Take(5000))
            {
                var s = Fuzzy.Score(q, item.Name);
                if (s <= 0) continue;
                var target = item;
                results.Add((new PaletteItem(item.Name, "in this folder", item.IsDirectory ? "folder" : "file", null, () =>
                {
                    view.ActiveSelection?.SelectOnly(target);
                    view.ActiveView?.BringIntoView(target);
                    view.FocusList();
                }), s * 0.9));
            }
        }

        if (q.Length > 1 && view is not null && !PathUtil.IsVirtual(view.ActiveFolder))
            results.Add((new PaletteItem($"Search subfolders for “{q}”", PathUtil.DisplayName(view.ActiveFolder), "search", null,
                () => _ = view.SearchAsync(q), Skin.Info), 0.5));

        return results.OrderByDescending(r => r.Score).Select(r => r.Item).Take(60).ToList();
    }

    private static bool LooksLikePath(string q) =>
        q.Length >= 2 && (q[1] == ':' || q.StartsWith(@"\\", StringComparison.Ordinal) || q.StartsWith('/') || q.StartsWith('~') || q.StartsWith('%') ||
                          q.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || q.StartsWith(".."));

    /// <summary>Key names as people write them ("Ctrl+Shift+1", "Ctrl+`").</summary>
    private static string? Pretty(string? gesture) => gesture?
        .Replace("OemTilde", "`").Replace("OemComma", ",").Replace("D1", "1").Replace("D2", "2").Replace("D3", "3")
        .Replace("PageDown", "PgDn").Replace("PageUp", "PgUp");
}
