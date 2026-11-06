using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PaxCommander.Core;
using PaxCommander.Core.Remote;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Views;

/// <summary>Right-click menus: items, folder background, tabs, panes, sidebar — plus the native Windows menu.</summary>
public sealed partial class MainWindow
{
    private static MenuItem Item(string header, string? glyph, Action click, string? gesture = null, bool enabled = true, bool bold = false)
    {
        var mi = new MenuItem { Header = WithGesture(header, gesture), IsEnabled = enabled };
        if (glyph is not null) mi.Icon = Glyphs.Icon(glyph, 15);
        if (bold) mi.FontWeight = FontWeight.SemiBold;
        mi.Click += (_, _) => click();
        return mi;
    }

    /// <summary>
    /// The shortcut is drawn by us (not MenuItem.InputGesture) so keys read the way people say them: "Enter", "Ctrl+`".
    /// </summary>
    private static object WithGesture(string header, string? gesture)
    {
        if (gesture is null) return header;
        var keys = new TextBlock { Text = Pretty(gesture), Foreground = Skin.Faint, FontSize = 12, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        DockPanel.SetDock(keys, Dock.Right);
        return new DockPanel { Children = { keys, new TextBlock { Text = header, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } };
    }

    private static MenuItem Sub(string header, string? glyph, params Control[] children)
    {
        var mi = new MenuItem { Header = header };
        if (glyph is not null) mi.Icon = Glyphs.Icon(glyph, 15);
        foreach (var c in children) mi.Items.Add(c);
        return mi;
    }

    private static Separator Sep() => new();

    public void ShowContextMenu(FolderView view, Control anchor, FileItem? item, Point position, bool shellMenu)
    {
        var items = view.SelectedItems;
        if (shellMenu && OperatingSystem.IsWindows() && !PathUtil.IsRemote(view.ActiveFolder))
        {
            ShowShellMenu(view, anchor, position, item is null ? [] : items);
            return;
        }
        var menu = new ContextMenu { Placement = Avalonia.Controls.PlacementMode.Pointer };
        foreach (var c in item is null ? BackgroundMenu(view, anchor, position) : ItemMenu(view, items, anchor, position))
            menu.Items.Add(c);
        menu.Open(anchor);
    }

    private IEnumerable<Control> ItemMenu(FolderView view, IReadOnlyList<FileItem> items, Control anchor, Point position)
    {
        if (items.Count == 0) yield break;
        if (items.Any(i => i.IsRemote))
        {
            // Server items get their own menu: no shell, Recycle Bin or archive actions apply there.
            foreach (var c in RemoteItemMenu(view, items)) yield return c;
            yield break;
        }
        var first = items[0];
        var single = items.Count == 1;
        var folders = items.All(i => i.IsDirectory);
        var anyDrive = items.Any(i => i.IsDrive);

        yield return Item("Open", "open", () => view.ItemsActivated(view.ActiveView!, items, false), "Enter", bold: true);
        if (folders)
        {
            yield return Item("Open in new tab", "plus", () => { foreach (var f in items) OpenInNewTab(f.Path, false); }, "Ctrl+Enter");
            yield return Item("Open in other pane", "dual", () =>
            {
                if (!AppSettings.Current.DualPane) SetDual(true);
                _ = OtherPane.Current?.NavigateAsync(first.Path);
            });
        }
        if (single)
            foreach (var mi in NetworkMenuItems(first.Path)) yield return mi;
        if (single && !first.IsDirectory && OperatingSystem.IsWindows())
        {
            yield return Item("Open with…", "open", () => Shell.OpenWith(Hwnd, first.Path));
            if (Shell.CanRunAsAdmin(first))
                yield return Item("Run as administrator", "shield", () => Try(() => Shell.RunAsAdmin(first.Path)));
        }
        // Disc images: mount without Explorer popping up, or unmount the drive they're mounted as.
        if (single && first.IsDrive && first.Image is { } image)
        {
            yield return Item($"Unmount {image.Kind}", "eject", () => _ = UnmountAsync(first.Path), bold: true);
            yield return Item($"Show {Path.GetFileName(image.DisplayPath)}", "disc", () => ShowImageFile(image));
        }
        if (single && IsMountableImage(first))
        {
            var mounted = VirtualDrives.FindMount(first.Path);
            yield return mounted is null
                ? Item("Mount", "disc", () => _ = MountAsync(first.Path), bold: false)
                : Item($"Unmount ({mounted.DriveRoot.TrimEnd('\\')})", "eject", () => _ = UnmountAsync(mounted.DriveRoot));
            if (mounted is not null)
                yield return Item($"Open {mounted.DriveRoot.TrimEnd('\\')}", "drive", () => OpenInNewTab(mounted.DriveRoot, false));
        }
        yield return Item("Quick Look", "eye", () => QuickLook(view), "Space");
        yield return Sep();

        if (!anyDrive)
        {
            yield return Item("Cut", "cut", () => _ = CopyToClipboardAsync(cut: true), "Ctrl+X");
            yield return Item("Copy", "copy", () => _ = CopyToClipboardAsync(cut: false), "Ctrl+C");
            if (single && first.IsDirectory)
                yield return Item("Paste into folder", "paste", () => _ = PasteAsync(first.Path), enabled: FileClipboard.MaybeHasFiles());
        }
        var copyPaths = Sub("Copy path", "link",
            Item(single ? "Full path" : "Full paths", null, () => _ = CopyPathsAsync(), "Ctrl+Shift+C"),
            Item(single ? "Name" : "Names", null, async () =>
            {
                if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, string.Join(Environment.NewLine, items.Select(i => i.Name)));
            }));
        if (OperatingSystem.IsWindows() && ShellInfo.GetUniversalPath(first.Path) is not null)
            copyPaths.Items.Add(Item("Network path (\\\\server\\share…)", null, () => _ = CopyPathsAsync(network: true)));
        yield return copyPaths;
        if (AppSettings.Current.DualPane && OtherView is { } other && !anyDrive)
        {
            yield return Item($"Copy to “{PathUtil.DisplayName(other.ActiveFolder)}”", "copy", () => CopyOrMoveToOtherPane(false), "Shift+F5");
            yield return Item($"Move to “{PathUtil.DisplayName(other.ActiveFolder)}”", "swap", () => CopyOrMoveToOtherPane(true), "Shift+F6");
        }
        yield return Sep();

        if (!anyDrive)
        {
            yield return Item(single ? "Rename" : "Rename all…", "rename", () => _ = RenameSelectionAsync(), "F2");
            yield return Item("Delete", "trash", () => _ = DeleteSelectionAsync(false), "Delete");
            yield return Item("Delete permanently", "trash", () => _ = DeleteSelectionAsync(true), "Shift+Delete");
            yield return Sep();
            yield return Item(single ? $"Compress to “{(first.IsDirectory ? first.Name : Path.GetFileNameWithoutExtension(first.Name))}.zip”" : "Compress to ZIP", "archive", CompressSelection);
            if (single && first.Extension == ".zip")
            {
                yield return Item("Extract here", "archive", () => Extract(first, false));
                yield return Item($"Extract to “{Path.GetFileNameWithoutExtension(first.Name)}\\”", "archive", () => Extract(first, true));
            }
        }
        if (items.Any(i => i.Kind == ItemKind.Folder))
            yield return Item("Calculate size", "sigma", () =>
            {
                foreach (var f in items.Where(i => i.Kind == ItemKind.Folder))
                    _ = FolderSizes.MeasureOne(f, CancellationToken.None, () => Avalonia.Threading.Dispatcher.UIThread.Post(() => view.ActiveListing?.SizesChanged()));
            });
        if (single && first.Kind == ItemKind.File)
            yield return Item("Checksum (SHA-256, MD5)", "hash", () => _ = ShowChecksumAsync(first));

        var label = Sub("Colour label", "tag");
        for (var i = 0; i < Labels.All.Length; i++)
        {
            var index = i;
            var mi = Item(Labels.All[i].Name, null, () =>
            {
                Labels.Set(items.Select(x => x.Path), index);
                foreach (var x in items) x.Label = index;
            });
            mi.Icon = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = index == 0 ? Brushes.Transparent : Skin.Label(index), BorderBrush = Skin.Faint, BorderThickness = new Thickness(index == 0 ? 1 : 0) };
            label.Items.Add(mi);
        }
        yield return label;
        if (folders && single)
            yield return Sidebar.IsFavorite(first.Path)
                ? Item("Remove from favorites", "star", () => _sidebar.RemoveFavorite(first.Path))
                : Item("Add to favorites", "star", () => _sidebar.AddFavorite(first.Path));
        yield return Sep();

        var termFolder = first.IsDirectory ? first.Path : Path.GetDirectoryName(first.Path) ?? view.ActiveFolder;
        yield return Item("Open in Windows Terminal", "terminal", () => Try(() => Shell.OpenTerminal(termFolder)));
        if (OperatingSystem.IsWindows())
        {
            yield return Item("Show in Explorer", "window", () => Try(() => Shell.Reveal(first.Path)));
            yield return Item("More options (Windows menu)", "more", () => ShowShellMenu(view, anchor, position, items), "Shift+F10");
            yield return Item("Properties", "info", () => Shell.Properties(Hwnd, items.Select(i => i.Path).ToList()), "Alt+Enter");
        }
    }

    private IEnumerable<Control> BackgroundMenu(FolderView view, Control anchor, Point position)
    {
        var folder = view.ActiveFolder;
        var real = !PathUtil.IsVirtual(folder) && !view.IsSearch;

        yield return Sub("View", "details",
            Item("Details", "details", () => SetMode(ViewMode.Details), "Ctrl+Shift+D1"),
            Item("Columns", "columns", () => SetMode(ViewMode.Columns), "Ctrl+Shift+D2"),
            Item("Thumbnails", "tiles", () => SetMode(ViewMode.Tiles), "Ctrl+Shift+D3"));
        yield return SortMenu(view);
        yield return Item("Refresh", "refresh", () => _ = view.RefreshAsync(), "F5");
        yield return Sep();
        if (real)
        {
            yield return Item("Paste", "paste", () => _ = PasteAsync(), "Ctrl+V");
            yield return Sub("New", "plus",
                Item("Folder", "folder", () => _ = NewFolderAsync(), "Ctrl+Shift+N"),
                Item("Text document", "doc", () => _ = NewFileAsync("New text document", ".txt"), "Ctrl+Alt+N"),
                Item("Markdown note", "doc", () => _ = NewFileAsync("New note", ".md")));
            yield return Sep();
        }
        yield return Item("Select all", "select", () => view.ActiveSelection?.SelectAll(), "Ctrl+A");
        yield return Item("Invert selection", "select", () => view.ActiveSelection?.Invert(), "Ctrl+I");
        if (real)
        {
            yield return Sep();
            yield return Item("Copy folder path", "link", () => _ = CopyPathsAsync());
            yield return Sidebar.IsFavorite(folder)
                ? Item("Remove from favorites", "star", () => _sidebar.RemoveFavorite(folder))
                : Item("Add to favorites", "star", () => _sidebar.AddFavorite(folder));
            if (PathUtil.IsRemote(folder))
            {
                if (RemotePath.TryParse(folder, out var conn, out _) && conn is { Protocol: RemoteProtocol.Sftp })
                {
                    yield return Item(view.HasTerminal ? "Hide SSH terminal" : "Show SSH terminal", "terminal", () => view.ToggleTerminal(), "Ctrl+OemTilde");
                    yield return Item("Open in Windows Terminal", "window", () => OpenSshTerminal(conn));
                }
                yield break;
            }
            yield return Item(view.HasTerminal ? "Hide terminal" : "Show terminal", "terminal", () => view.ToggleTerminal(), "Ctrl+OemTilde");
            yield return Item("Open in Windows Terminal", "window", () => Try(() => Shell.OpenTerminal(folder)));
            if (OperatingSystem.IsWindows())
            {
                yield return Item("Open in Explorer", "window", () => Try(() => Shell.Open(folder)));
                yield return Item("More options (Windows menu)", "more", () => ShowShellMenu(view, anchor, position, []));
                yield return Item("Properties", "info", () => Shell.Properties(Hwnd, [folder]));
            }
        }
    }

    private MenuItem SortMenu(FolderView view)
    {
        var sort = Sub("Sort by", "sort");
        foreach (var (title, key) in new[] { ("Name", SortKey.Name), ("Date modified", SortKey.Modified), ("Type", SortKey.Type), ("Size", SortKey.Size), ("Date created", SortKey.Created) })
        {
            var mi = Item(title, null, () => view.SetSort(key, key == view.Sort.Key ? view.Sort.Descending : null));
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = view.Sort.Key == key;
            sort.Items.Add(mi);
        }
        sort.Items.Add(Sep());
        var asc = Item("Ascending", null, () => view.SetSort(view.Sort.Key, false));
        asc.ToggleType = MenuItemToggleType.Radio;
        asc.IsChecked = !view.Sort.Descending;
        var desc = Item("Descending", null, () => view.SetSort(view.Sort.Key, true));
        desc.ToggleType = MenuItemToggleType.Radio;
        desc.IsChecked = view.Sort.Descending;
        sort.Items.Add(asc);
        sort.Items.Add(desc);
        return sort;
    }

    private void SetMode(ViewMode mode)
    {
        CurrentView?.SetMode(mode);
        _activePane.SyncMode();
    }

    /// <summary>The full Explorer menu (Shift+right-click), for shell extensions like 7-Zip, Git, antivirus…</summary>
    private void ShowShellMenu(FolderView view, Control anchor, Point position, IReadOnlyList<FileItem> items)
    {
        if (!OperatingSystem.IsWindows()) return;
        var screen = anchor.PointToScreen(position);
        var paths = items.Where(i => !i.IsDrive || items.Count == 1).Select(i => i.Path).ToList();
        // The shell menu needs items from one folder; search results can span many.
        if (paths.Count > 1)
        {
            var parent = Path.GetDirectoryName(paths[0]);
            paths = paths.Where(p => PathUtil.Same(Path.GetDirectoryName(p), parent)).ToList();
        }
        var extended = (KeyModifiersNow() & KeyModifiers.Shift) != 0;
        ContextMenuResult result;
        try
        {
            result = paths.Count > 0
                ? ShellContextMenu.ShowForItems(Hwnd, paths, screen.X, screen.Y, extended)
                : ShellContextMenu.ShowForBackground(Hwnd, view.ActiveFolder, screen.X, screen.Y, extended);
        }
        catch (Exception ex)
        {
            CrashLog.Write("shell menu", ex);
            return;
        }
        Activate();
        if (result.Invoked || result.Verb is null) return;
        switch (result.Verb.ToLowerInvariant())
        {
            case "rename": _ = RenameSelectionAsync(); break;
            case "delete": _ = DeleteSelectionAsync(false); break;
            case "cut": _ = CopyToClipboardAsync(true); break;
            case "copy": _ = CopyToClipboardAsync(false); break;
            case "paste": _ = PasteAsync(); break;
        }
    }

    private KeyModifiers _lastModifiers;

    private KeyModifiers KeyModifiersNow() => _lastModifiers;

    private void ShowTabMenu(PaneView pane, FolderView tab, Control anchor)
    {
        var menu = new ContextMenu { Placement = Avalonia.Controls.PlacementMode.Pointer };
        menu.Items.Add(Item("Duplicate tab", "plus", () => pane.AddTab(tab.CurrentPath, tab.Mode, tab.Sort)));
        menu.Items.Add(Item("Open in other pane", "dual", () =>
        {
            if (!AppSettings.Current.DualPane) SetDual(true);
            (pane == _panes[0] ? _panes[1] : _panes[0]).AddTab(tab.CurrentPath, tab.Mode, tab.Sort);
        }));
        menu.Items.Add(Item("Move left", "back", () => pane.MoveTab(tab, -1)));
        menu.Items.Add(Item("Move right", "forward", () => pane.MoveTab(tab, 1)));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("Close", "close", () => pane.CloseTab(tab), "Ctrl+W"));
        menu.Items.Add(Item("Close other tabs", "close", () => pane.CloseOthers(tab), enabled: pane.Tabs.Count > 1));
        menu.Items.Add(Item("Reopen closed tab", "undo", ReopenClosedTab, "Ctrl+Shift+T", enabled: _closedTabs.Count > 0));
        menu.Open(anchor);
    }

    private void ShowPaneMenu(PaneView pane, Control anchor)
    {
        SetActive(pane);
        if (pane.Current is not { } view) return;
        var s = AppSettings.Current;
        var menu = new MenuFlyout();
        menu.Items.Add(SortMenu(view));
        menu.Items.Add(Sep());
        menu.Items.Add(Check("Hidden files", s.ShowHidden, "Ctrl+H", v => { s.ShowHidden = v; ApplySettings(); }));
        menu.Items.Add(Check("Protected system files", s.ShowSystemFiles, null, v => { s.ShowSystemFiles = v; ApplySettings(); }));
        menu.Items.Add(Check("Size bars", s.ShowSizeBars, null, v => { s.ShowSizeBars = v; ApplySettings(); }));
        menu.Items.Add(Check("Compact rows", s.CompactRows, null, v => { s.CompactRows = v; ApplySettings(); }));
        var preview = Sub("Preview pane", "preview");
        foreach (var (title, on, auto) in new[] { ("Off", false, false), ("Always", true, false), ("Auto: images, videos and code", true, true) })
        {
            var mi = Item(title, null, () =>
            {
                if (!on) SetPreview(false);
                else SetAutoPreview(auto);
            });
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = s.ShowPreview == on && (!on || s.AutoPreview == auto);
            preview.Items.Add(mi);
        }
        menu.Items.Add(preview);
        var sizes = Sub("Folder sizes", "sigma");
        foreach (var (title, mode) in new[] { ("Off", FolderSizeMode.Off), ("Local drives", FolderSizeMode.LocalDrives), ("Everywhere", FolderSizeMode.Everywhere) })
        {
            var mi = Item(title, null, () =>
            {
                s.FolderSizes = mode;
                AppSettings.SaveSoon();
                if (FolderSizes.ShouldAutoMeasure(view.CurrentPath)) view.MeasureSizes();
            });
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = s.FolderSizes == mode;
            sizes.Items.Add(mi);
        }
        sizes.Items.Add(Sep());
        sizes.Items.Add(Item("Calculate now for this folder", null, () => view.MeasureSizes(force: true), "Ctrl+Shift+S"));
        menu.Items.Add(sizes);
        menu.Items.Add(Sep());
        menu.Items.Add(Check("Stack panes top/bottom", s.StackedPanes, null, v =>
        {
            s.StackedPanes = v;
            SetDual(s.DualPane);
        }));
        menu.Items.Add(Item("Swap panes", "swap", SwapPanes, "Ctrl+U"));
        menu.Items.Add(Item("Show this folder in the other pane", "dual", MirrorToOtherPane, "Ctrl+Shift+M"));
        menu.Items.Add(Sep());
        var terminal = Check("Terminal", view.HasTerminal, "Ctrl+OemTilde", _ => view.ToggleTerminal());
        terminal.IsEnabled = view.HasTerminal || view.CanShowTerminal;
        menu.Items.Add(terminal);
        menu.Items.Add(Item("Open in Windows Terminal", "window", () =>
        {
            if (RemotePath.TryParse(view.ActiveFolder, out var conn, out _) && conn is { Protocol: RemoteProtocol.Sftp }) OpenSshTerminal(conn);
            else Try(() => Shell.OpenTerminal(view.ActiveFolder));
        }, enabled: !PathUtil.IsVirtual(view.ActiveFolder) && (!PathUtil.IsRemote(view.ActiveFolder) || view.CanShowTerminal)));
        menu.Items.Add(Item("Settings…", "settings", () => _ = SettingsView.ShowAsync(ApplySettings), "Ctrl+OemComma"));
        menu.Items.Add(Item("Check for updates…", "download", () => _ = UpdatePrompt.CheckNowAsync(this)));
        menu.ShowAt(anchor);
    }

    private static MenuItem Check(string header, bool value, string? gesture, Action<bool> set)
    {
        var mi = new MenuItem { Header = WithGesture(header, gesture), ToggleType = MenuItemToggleType.CheckBox, IsChecked = value };
        mi.Click += (_, _) =>
        {
            set(!value);
            AppSettings.SaveSoon();
        };
        return mi;
    }

    private void ShowSidebarMenu(string path, Control anchor)
    {
        var menu = new ContextMenu { Placement = Avalonia.Controls.PlacementMode.Pointer };
        menu.Items.Add(Item("Open", "open", () => OpenFromSidebar(path, OpenTarget.Here), bold: true));
        menu.Items.Add(Item("Open in new tab", "plus", () => OpenFromSidebar(path, OpenTarget.NewTab)));
        menu.Items.Add(Item("Open in other pane", "dual", () => OpenFromSidebar(path, OpenTarget.OtherPane)));
        if (OperatingSystem.IsWindows() && PathUtil.IsRoot(path) && VirtualDrives.GetMountedImage(path) is { } image)
        {
            menu.Items.Add(Sep());
            menu.Items.Add(Item($"Unmount {image.Kind}", "eject", () => _ = UnmountAsync(path)));
            menu.Items.Add(Item($"Show {Path.GetFileName(image.DisplayPath)}", "disc", () => ShowImageFile(image)));
        }
        foreach (var mi in NetworkMenuItems(path)) menu.Items.Add(mi);
        menu.Items.Add(Sep());
        if (Sidebar.IsFavorite(path))
        {
            menu.Items.Add(Item("Move up", "up", () => _sidebar.MoveFavorite(path, -1)));
            menu.Items.Add(Item("Move down", "back", () => _sidebar.MoveFavorite(path, 1)));
            menu.Items.Add(Item("Remove from favorites", "star", () => _sidebar.RemoveFavorite(path)));
        }
        else
        {
            menu.Items.Add(Item("Add to favorites", "star", () => _sidebar.AddFavorite(path)));
            menu.Items.Add(Item("Remove from Frequent", "close", () =>
            {
                Frecency.Forget(path);
                _sidebar.Rebuild();
            }));
        }
        menu.Items.Add(Item("Open terminal here", "terminal", () => Try(() => Shell.OpenTerminal(path))));
        if (OperatingSystem.IsWindows())
            menu.Items.Add(Item("Properties", "info", () => Shell.Properties(Hwnd, [path])));
        menu.Open(anchor);
    }

    private async Task ShowChecksumAsync(FileItem item)
    {
        Toasts.Show($"Computing checksums of “{item.Name}”…", seconds: 2);
        try
        {
            var (sha, md5) = await Checksums.ComputeAsync(item.Path, null, CancellationToken.None);
            await Dialogs.ShowAsync<bool>(tcs =>
            {
                var compare = new TextBox { PlaceholderText = "Paste a checksum to compare" };
                var verdict = new TextBlock { FontSize = 12, Margin = new Thickness(2, 6, 0, 0) };
                compare.TextChanged += (_, _) =>
                {
                    var t = (compare.Text ?? "").Trim().ToLowerInvariant();
                    verdict.Text = t.Length == 0 ? "" : t == sha || t == md5 ? "✓ Matches" : "✗ Doesn't match";
                    verdict.Foreground = t == sha || t == md5 ? Skin.Accent : Skin.Bad;
                };
                Control Line(string name, string value)
                {
                    var copy = Dialogs.Btn("Copy", null, async () =>
                    {
                        if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, value);
                    });
                    copy.MinWidth = 60;
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,Auto"), Margin = new Thickness(0, 4) };
                    g.Children.Add(new TextBlock { Text = name, Foreground = Skin.Faint, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    var v = new SelectableTextBlock { Text = value, FontFamily = Skin.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    Grid.SetColumn(v, 1);
                    g.Children.Add(v);
                    Grid.SetColumn(copy, 2);
                    g.Children.Add(copy);
                    return g;
                }
                return new StackPanel
                {
                    Children =
                    {
                        Dialogs.Title($"Checksums of “{item.Name}”"),
                        Line("SHA-256", sha),
                        Line("MD5", md5),
                        new Border { Height = 10 },
                        compare,
                        verdict,
                        Dialogs.Buttons(Dialogs.Btn("Close", "primary", () => tcs.TrySetResult(true))),
                    },
                };
            }, false, width: 640);
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't read the file: " + ex.Message, ToastKind.Error);
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }
}
