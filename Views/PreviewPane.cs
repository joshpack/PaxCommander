using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using pxdCommander.Core;
using pxdCommander.Platform.Win32;

namespace pxdCommander.Views;

/// <summary>
/// Details pane for the focused item: a visual preview (image, text/code, archive listing, Explorer thumbnail), the
/// facts (sizes, dates, media properties from the shell), folder totals, and a checksum on demand.
/// </summary>
public sealed class PreviewPane : UserControl
{
    private readonly ContentControl _visual = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _name = new() { Classes = { "h1" }, TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private readonly TextBlock _kind = new() { Foreground = Skin.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Grid _facts = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _body;
    private readonly StackPanel _empty;
    private FileItem? _item;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _debounce;
    private FileItem? _queued;

    public event Action<FileItem>? OpenRequested;
    /// <summary>Unmount the image mounted at this drive root.</summary>
    public event Action<string>? UnmountRequested;
    /// <summary>Mount this image file.</summary>
    public event Action<string>? MountRequested;

    private void AddAction(string text, Action click)
    {
        var b = Dialogs.Btn(text, null, click);
        b.Margin = new Thickness(0, 0, 6, 6);
        _actions.Children.Add(b);
    }

    /// <summary>For an ISO/VHD file: is it mounted, and where? Offers Mount or Unmount accordingly.</summary>
    private async Task MountStateAsync(FileItem item)
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = _cts?.Token ?? CancellationToken.None;
        var mounted = await Task.Run(() => Platform.Win32.VirtualDrives.FindMount(item.Path), token);
        if (token.IsCancellationRequested || _item != item) return;
        if (mounted is null)
            AddAction("Mount", () => MountRequested?.Invoke(item.Path));
        else
        {
            Fact("Mounted as", mounted.DriveRoot.TrimEnd('\\'));
            AddAction($"Unmount {mounted.DriveRoot.TrimEnd('\\')}", () => UnmountRequested?.Invoke(mounted.DriveRoot));
        }
    }

    public PreviewPane()
    {
        _body = new StackPanel
        {
            Margin = new Thickness(16, 14, 16, 16),
            Children = { _visual, _name, _kind, _facts, _actions },
        };
        _name.Margin = new Thickness(0, 12, 0, 0);
        _facts.Margin = new Thickness(0, 12, 0, 0);
        _empty = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Children =
            {
                new GlyphIcon { Glyph = "eye", Width = 34, Height = 34, Brush = Skin.Faint, StrokeWidth = 1.5 },
                new TextBlock { Text = "Select something to preview it", Foreground = Skin.Faint, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Space opens Quick Look", Foreground = Skin.Faint, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 },
            },
        };
        var grid = new Grid();
        grid.Children.Add(new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        grid.Children.Add(_empty);
        Content = grid;
        _body.IsVisible = false;

        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(110), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Load(_queued);
        });
    }

    /// <summary>Shows an item (debounced so holding an arrow key doesn't decode every photo on the way).</summary>
    public void Show(FileItem? item)
    {
        if (item == _item && item is not null) return;
        _queued = item;
        _debounce.Stop();
        _debounce.Start();
    }

    public void Reload()
    {
        var item = _item;
        _item = null;
        Load(item);
    }

    private void Load(FileItem? item)
    {
        _cts?.Cancel();
        _item = item;
        if (item is null || !IsVisible)
        {
            _body.IsVisible = false;
            _empty.IsVisible = true;
            return;
        }
        _empty.IsVisible = false;
        _body.IsVisible = true;
        var cts = _cts = new CancellationTokenSource();
        _name.Text = item.Name;
        _kind.Text = item.TypeName ?? FileKinds.GenericTypeName(item);
        _facts.Children.Clear();
        _facts.RowDefinitions.Clear();
        _actions.Children.Clear();
        _visual.Content = new Border { Height = 160, Background = Skin.Visual, CornerRadius = new CornerRadius(10) };

        BasicFacts(item);
        BuildActions(item);
        _ = FillAsync(item, cts.Token);
    }

    private async Task FillAsync(FileItem item, CancellationToken ct)
    {
        try
        {
            var visual = await BuildVisualAsync(item, 640, large: false, ct);
            if (ct.IsCancellationRequested) return;
            _visual.Content = visual;

            if (item.Kind == ItemKind.Folder && !item.IsRemote)
                await FolderFactsAsync(item, ct);
            if (OperatingSystem.IsWindows() && item.Kind == ItemKind.File && !item.IsRemote)
            {
                var details = await ShellThread.RunAsync(() => ShellInfo.GetDetails(item.Path), ct);
                if (ct.IsCancellationRequested) return;
                foreach (var (k, v) in details) Fact(k, v);
                if (item.TypeName is null && ShellInfo.GetTypeName(item.Path, false) is { } tn)
                    _kind.Text = item.TypeName = tn;
            }
            if (visual is Image { Source: Bitmap bmp } && item.Kind == ItemKind.File && !_facts.Children.OfType<TextBlock>().Any(t => t.Text is "Dimensions"))
                Fact("Pixels", $"{bmp.PixelSize.Width} × {bmp.PixelSize.Height}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Fact("Preview", ex.Message);
        }
    }

    private void BasicFacts(FileItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Drive:
                if (item.Size > 0 && item.IsDisc)
                    Fact("Size", $"{Formatting.Size(item.Size)} (read-only)");
                else if (item.Size > 0)
                {
                    Fact("Free", $"{Formatting.Size(item.DriveFree)} of {Formatting.Size(item.Size)}");
                    Fact("Used", $"{(item.Size - item.DriveFree) * 100.0 / item.Size:0.#}%");
                }
                if (item.DriveDescription is { } d) Fact("Type", d);
                if (item.Image is { } image) Fact("Mounted from", image.DisplayPath);
                break;
            case ItemKind.File:
                Fact("Size", $"{Formatting.Size(item.Size)}  ({Formatting.Bytes(item.Size)})");
                break;
        }
        if (item.Kind != ItemKind.Drive)
        {
            Fact("Modified", Formatting.Date(item.Modified, false));
            Fact("Created", Formatting.Date(item.Created, false));
            var attrs = new List<string>();
            if ((item.Attributes & FileAttributes.ReadOnly) != 0) attrs.Add("Read-only");
            if ((item.Attributes & FileAttributes.Hidden) != 0) attrs.Add("Hidden");
            if ((item.Attributes & FileAttributes.System) != 0) attrs.Add("System");
            if ((item.Attributes & FileAttributes.Compressed) != 0) attrs.Add("Compressed");
            if ((item.Attributes & FileAttributes.Encrypted) != 0) attrs.Add("Encrypted");
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) attrs.Add("Link");
            if ((item.Attributes & FileAttributes.Offline) != 0 || (item.Attributes & (FileAttributes)0x400000) != 0) attrs.Add("Online-only");
            if (attrs.Count > 0) Fact("Attributes", string.Join(", ", attrs));
            Fact("Location", item.IsRemote ? Core.Remote.RemotePath.ToUrl(PathUtil.Parent(item.Path) ?? item.Path) : Path.GetDirectoryName(item.Path) ?? item.Path);
        }
    }

    private async Task FolderFactsAsync(FileItem item, CancellationToken ct)
    {
        var counts = await Task.Run(() =>
        {
            int files = 0, folders = 0;
            try
            {
                foreach (var e in new DirectoryInfo(item.Path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    if (ct.IsCancellationRequested) break;
                    if ((e.Attributes & FileAttributes.Directory) != 0) folders++;
                    else files++;
                    if (files + folders > 200_000) break;
                }
            }
            catch
            {
                // Access denied: no counts.
            }
            return (files, folders);
        }, ct);
        if (ct.IsCancellationRequested) return;
        Fact("Contains", $"{Formatting.Count(counts.folders, "folder")}, {Formatting.Count(counts.files, "file")}");
        var size = Fact("Total size", item.FolderBytes >= 0 && !item.FolderSizePartial ? Formatting.Size(item.FolderBytes) : "Adding up…");
        if (item.FolderBytes >= 0 && !item.FolderSizePartial) return;
        void Update(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(FileItem.FolderBytes) or nameof(FileItem.FolderSizePartial))
                size.Text = Formatting.Size(Math.Max(0, item.FolderBytes)) + (item.FolderSizePartial ? "…" : "");
        }
        item.PropertyChanged += Update;
        Search.FolderSizeResult? result = null;
        try
        {
            if (!item.FolderSizePartial)
                result = await FolderSizes.MeasureOne(item, ct);
        }
        finally
        {
            item.PropertyChanged -= Update;
        }
        if (!ct.IsCancellationRequested)
        {
            // Use the result itself: the item's own copy is updated through the dispatcher a moment later.
            var bytes = result?.Bytes ?? item.FolderBytes;
            size.Text = bytes < 0 ? "—" : Formatting.Size(bytes) + (bytes >= 1024 ? $"  ({Formatting.Bytes(bytes)})" : "") + (result is { Inaccessible: > 0 } r ? $" · {r.Inaccessible} folders unreadable" : "");
        }
    }

    private TextBlock Fact(string key, string value)
    {
        var row = _facts.RowDefinitions.Count;
        _facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var k = new TextBlock { Text = key, Foreground = Skin.Faint, FontSize = 12, Margin = new Thickness(0, 3, 14, 3) };
        var v = new SelectableTextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3) };
        Grid.SetRow(k, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        _facts.Children.Add(k);
        _facts.Children.Add(v);
        return v;
    }

    private void BuildActions(FileItem item)
    {
        var open = Dialogs.Btn("Open", "primary", () => OpenRequested?.Invoke(item));
        open.Margin = new Thickness(0, 0, 6, 6);
        _actions.Children.Add(open);
        if (item.IsDrive && item.Image is not null)
            AddAction("Unmount", () => UnmountRequested?.Invoke(item.Path));
        else if (MainWindow.IsMountableImage(item))
            _ = MountStateAsync(item);
        var copy = Dialogs.Btn("Copy path", null, async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, item.IsRemote ? Core.Remote.RemotePath.ToUrl(item.Path) : item.Path);
            Toasts.Show("Path copied", ToastKind.Success, seconds: 1.5);
        });
        copy.Margin = new Thickness(0, 0, 6, 6);
        _actions.Children.Add(copy);
        if (item.Kind == ItemKind.File && !item.IsRemote)
        {
            Button? hash = null;
            hash = Dialogs.Btn("Checksum", null, () => _ = HashAsync(item, hash!));
            hash.Margin = new Thickness(0, 0, 6, 6);
            ToolTip.SetTip(hash, "SHA-256 and MD5 of the file");
            _actions.Children.Add(hash);
        }
    }

    private async Task HashAsync(FileItem item, Button button)
    {
        button.IsEnabled = false;
        var ct = _cts?.Token ?? CancellationToken.None;
        var result = Fact("SHA-256", "Reading…");
        try
        {
            var (sha, md5) = await Checksums.ComputeAsync(item.Path, p => Dispatcher.UIThread.Post(() => result.Text = $"Reading… {p:P0}"), ct);
            result.Text = sha;
            Fact("MD5", md5);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Text = ex.Message;
        }
    }

    // ---- Visual previews (shared with Quick Look) ----------------------------------------------------------------------

    public static async Task<Control> BuildVisualAsync(FileItem item, int pixels, bool large, CancellationToken ct)
    {
        var kind = FileKinds.Of(item);
        // Server files: small images and text are fetched to a temp copy and previewed from there.
        if (item.IsRemote && item.Kind == ItemKind.File && item.Size < (large ? 30_000_000 : 5_000_000) && (FileKinds.IsTextual(item.Extension) || FileKinds.IsDecodableImage(item.Extension)))
        {
            try
            {
                var local = await Core.Remote.RemoteSessions.DownloadToTempAsync(item.Path, ct);
                return await BuildVisualAsync(FileItem.TryStat(local) ?? item, pixels, large, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Fall back to the type icon.
            }
        }
        if (item.IsRemote) return await IconBox(item, large);
        if (item.Kind == ItemKind.File)
        {
            if (FileKinds.IsTextual(item.Extension) && item.Size < 8_000_000 && kind != FileKind.Image)
            {
                var text = await Task.Run(() => ReadText(item.Path, large ? 400_000 : 60_000), ct);
                var ext = item.Extension.Length > 0 ? item.Extension : (item.Name.StartsWith('.') ? "" : ".") + item.Name.ToLowerInvariant();
                if (text is not null) return CodeView.Build(text, ext, large) ?? TextView(text, large);
            }
            if (item.Extension is ".zip" or ".jar" or ".nupkg" or ".apk" or ".docx" or ".xlsx" or ".pptx" or ".epub" or ".vsix")
            {
                var list = await Task.Run(() => ZipListing(item.Path), ct);
                if (list is not null) return list;
            }
            var image = await IconService.LoadPreviewAsync(item.Path, pixels, ct);
            if (image is not null) return ImageView(image, large);
            if (!FileKinds.IsTextual(item.Extension) && item.Size > 0 && kind is FileKind.Other)
            {
                var hex = await Task.Run(() => HexDump(item.Path), ct);
                if (hex is not null) return TextView(hex, large);
            }
        }
        return await IconBox(item, large);
    }

    private static async Task<Control> IconBox(FileItem item, bool large)
    {
        var icon = await IconService.LoadLargeIconAsync(item);
        return new Border
        {
            Height = large ? 360 : 170,
            Background = Skin.Visual,
            CornerRadius = new CornerRadius(10),
            Child = new Image { Source = icon, Width = large ? 192 : 112, Height = large ? 192 : 112, Stretch = Stretch.Uniform },
        };
    }

    private static Control ImageView(IImage image, bool large)
    {
        var img = new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = large ? double.PositiveInfinity : 340 };
        RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
        return new Border
        {
            Background = Skin.Visual,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Padding = new Thickness(large ? 0 : 6),
            Child = img,
        };
    }

    private static Control TextView(string text, bool large)
    {
        var block = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Skin.Mono,
            FontSize = large ? 13 : 11.5,
            Foreground = Skin.CodeText,
            TextWrapping = large ? TextWrapping.NoWrap : TextWrapping.Wrap,
        };
        return new Border
        {
            Background = Skin.CodeBg,
            BorderBrush = Skin.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            MaxHeight = large ? double.PositiveInfinity : 380,
            Child = new ScrollViewer { Content = block, HorizontalScrollBarVisibility = large ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
    }

    /// <summary>The start of a text file, or null when it turns out to be binary.</summary>
    private static string? ReadText(string path, int maxChars)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[Math.Min(4096, (int)Math.Max(0, Math.Min(fs.Length, 4096)))];
        var n = fs.Read(head, 0, head.Length);
        var utf16 = n >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));
        if (!utf16 && head.AsSpan(0, n).Contains((byte)0)) return null;
        fs.Position = 0;
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars];
        var read = reader.ReadBlock(buffer, 0, maxChars);
        var text = new string(buffer, 0, read);
        if (!reader.EndOfStream) text += "\n\n… (preview truncated)";
        return text.Length == 0 ? "(empty file)" : text;
    }

    private static string? HexDump(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(fs.Length, 1024)];
        var n = fs.Read(bytes, 0, bytes.Length);
        var sb = new StringBuilder();
        for (var off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("X6")).Append("  ");
            for (var i = 0; i < 16; i++)
                sb.Append(off + i < n ? bytes[off + i].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (var i = 0; i < 16 && off + i < n; i++)
            {
                var b = bytes[off + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '·');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static Control? ZipListing(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries;
            long total = 0, packed = 0;
            foreach (var e in entries)
            {
                total += e.Length;
                packed += e.CompressedLength;
            }
            var sb = new StringBuilder();
            foreach (var e in entries.Take(400))
                sb.Append(e.FullName.EndsWith('/') ? "📁 " : "    ").Append(e.FullName).Append(e.Length > 0 ? $"   {Formatting.Size(e.Length)}" : "").Append('\n');
            if (entries.Count > 400) sb.Append($"… and {entries.Count - 400:N0} more\n");
            var header = new TextBlock
            {
                Text = $"{Formatting.Count(entries.Count, "entry", "entries")} · {Formatting.Size(total)} unpacked" + (total > 0 ? $" · {100 - packed * 100.0 / total:0}% smaller" : ""),
                Foreground = Skin.Info,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var list = TextView(sb.ToString(), false);
            return new StackPanel { Children = { header, list } };
        }
        catch
        {
            return null;
        }
    }
}

public static class Checksums
{
    /// <summary>SHA-256 and MD5 in one pass over the file.</summary>
    public static Task<(string Sha256, string Md5)> ComputeAsync(string path, Action<double>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[1 << 20];
        long done = 0;
        var last = Environment.TickCount64;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            md5.AppendData(buffer, 0, n);
            done += n;
            if (progress is not null && Environment.TickCount64 - last > 150 && fs.Length > 0)
            {
                last = Environment.TickCount64;
                progress((double)done / fs.Length);
            }
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant());
    }, ct);
}

/// <summary>Space bar preview over the whole window; arrows step through the folder, Enter opens, Esc/Space closes.</summary>
public sealed class QuickLook
{
    private readonly Panel _layer;
    private readonly Border _backdrop;
    private readonly ContentControl _visual = new() { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sub = new() { FontSize = 12, Foreground = Skin.Muted };
    private readonly ItemList _items;
    private readonly Action<FileItem> _select;
    private readonly Action<FileItem> _open;
    private int _index;
    private CancellationTokenSource? _cts;

    public QuickLook(Panel layer, ItemList items, FileItem start, Action<FileItem> select, Action<FileItem> open)
    {
        _layer = layer;
        _items = items;
        _select = select;
        _open = open;
        _index = Math.Max(0, items.IndexOf(start));
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = new Button { Content = Glyphs.Icon("close", 16), Classes = { "icon" } };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new StackPanel { Children = { _title, _sub } });
        var card = new Border
        {
            Classes = { "card" },
            Margin = new Thickness(60, 40),
            Child = new DockPanel { Children = { header, _visual } },
        };
        DockPanel.SetDock(header, Dock.Top);
        _backdrop = new Border { Background = new SolidColorBrush(Color.Parse("#C0050709")), Child = card, Focusable = true };
        _backdrop.KeyDown += OnKey;
        _backdrop.PointerPressed += (_, e) =>
        {
            if (e.Source == _backdrop) Close();
        };
    }

    public static QuickLook? Current { get; private set; }

    public void Show()
    {
        Current?.Close();
        Current = this;
        _layer.Children.Add(_backdrop);
        Dispatcher.UIThread.Post(() => _backdrop.Focus(), DispatcherPriority.Input);
        Load();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Space:
                Close();
                break;
            case Key.Right or Key.Down:
                Step(1);
                break;
            case Key.Left or Key.Up:
                Step(-1);
                break;
            case Key.Enter:
                if (_index < _items.Count) _open(_items[_index]);
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Step(int delta)
    {
        if (_items.Count == 0) return;
        _index = Math.Clamp(_index + delta, 0, _items.Count - 1);
        _select(_items[_index]);
        Load();
    }

    private async void Load()
    {
        _cts?.Cancel();
        if (_index >= _items.Count) return;
        var cts = _cts = new CancellationTokenSource();
        var item = _items[_index];
        _title.Text = item.Name;
        _sub.Text = $"{_index + 1} of {_items.Count}  ·  " + (item.Kind == ItemKind.File ? Formatting.Size(item.Size) + "  ·  " : "") + Formatting.Date(item.Modified, false);
        try
        {
            var px = (int)Math.Max(800, (_layer.Bounds.Width - 160) * (TopLevel.GetTopLevel(_layer)?.RenderScaling ?? 1));
            var visual = await PreviewPane.BuildVisualAsync(item, Math.Min(px, 2600), large: true, cts.Token);
            if (!cts.IsCancellationRequested) _visual.Content = visual;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _visual.Content = new TextBlock { Text = ex.Message, Foreground = Skin.Bad };
        }
    }

    public void Close()
    {
        _cts?.Cancel();
        _layer.Children.Remove(_backdrop);
        if (Current == this) Current = null;
    }
}
