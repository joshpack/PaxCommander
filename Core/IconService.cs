using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PaxCommander.Platform.Win32;


namespace PaxCommander.Core;

/// <summary>
/// Supplies icons and thumbnails lazily: rows ask when they are realized, get a generic icon at once, and the real
/// one (from the Windows shell, or decoded locally elsewhere) arrives a moment later through the item's properties.
/// </summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<(int Index, ShellIconSize Size), IImage> IconCache = new();
    private static readonly ConcurrentDictionary<string, int> IndexByKey = new(StringComparer.OrdinalIgnoreCase);

    // Files whose icon lives inside the file (or the target, for shortcuts), so it can't be shared per extension.
    private static readonly HashSet<string> PerFileIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".cpl", ".scr", ".msc", ".appref-ms", ".cur", ".ani", ".msi", ".dll",
    };

    public static ShellIconSize PickSize(double pixels) => pixels switch
    {
        <= 16 => ShellIconSize.Small,
        <= 32 => ShellIconSize.Large,
        <= 48 => ShellIconSize.ExtraLarge,
        _ => ShellIconSize.Jumbo,
    };

    /// <summary>Makes sure <paramref name="item"/> gets an icon suitable for <paramref name="dip"/> device-independent pixels.</summary>
    public static void RequestIcon(FileItem item, double dip, double renderScale)
    {
        var pixels = dip * renderScale;
        if (item.IconRequested && item.IconScale >= pixels) return;
        item.IconRequested = true;
        item.IconScale = pixels;
        item.Icon ??= FallbackIcons.For(item);
        if (!OperatingSystem.IsWindows()) return;

        var size = PickSize(pixels);
        var key = SharedKey(item);
        if (key is not null && IndexByKey.TryGetValue(key, out var known) && IconCache.TryGetValue((known, size), out var cached))
        {
            item.Icon = cached;
            return;
        }
        _ = LoadIconAsync(item, size, key);
    }

    /// <summary>The cache key for icons shared by many items, or null when the item has its own icon.</summary>
    private static string? SharedKey(FileItem item)
    {
        if (item.Kind == ItemKind.Drive) return null;
        // Remote files have no local copy for the shell to look inside; their type icon is enough.
        if (item.IsRemote) return item.IsDirectory ? "<folder>" : "<ext>" + item.Extension;
        if (item.Kind == ItemKind.Folder)
        {
            // Special folders (Downloads, Documents…) and custom folder icons need these attributes to work.
            return (item.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0 || item.IsLink ? null : "<folder>";
        }
        return PerFileIcon.Contains(item.Extension) ? null : "<ext>" + item.Extension;
    }

    private static async Task LoadIconAsync(FileItem item, ShellIconSize size, string? key)
    {
        try
        {
            var image = await ShellThread.RunAsync(() =>
            {
                int index;
                if (key is not null && IndexByKey.TryGetValue(key, out var k))
                    index = k;
                else
                {
                    // Shared icons are looked up by a made-up name so the disk is never touched for them.
                    index = key is null
                        ? Platform.Win32.ShellIcons.GetIconIndex(item.Path, item.IsDirectory, extensionOnly: false)
                        : Platform.Win32.ShellIcons.GetIconIndex(item.IsDirectory ? "folder" : "file" + item.Extension, item.IsDirectory, extensionOnly: true);
                    if (index < 0) return null;
                    if (key is not null) IndexByKey[key] = index;
                }
                if (IconCache.TryGetValue((index, size), out var hit)) return hit;
                var raw = Platform.Win32.ShellIcons.GetIconImage(index, size);
                if (raw is null) return null;
                var bmp = ToBitmap(raw);
                IconCache[(index, size)] = bmp;
                return bmp;
            });
            if (image is not null)
                Dispatcher.UIThread.Post(() => item.Icon = image);
        }
        catch (Exception ex)
        {
            CrashLog.Write("icon " + item.Path, ex);
        }
    }

    // ---- Thumbnails ---------------------------------------------------------------------------------------------------

    private static readonly LinkedList<FileItem> ThumbOrder = new();
    private static readonly Dictionary<FileItem, LinkedListNode<FileItem>> ThumbNodes = new();
    private const int ThumbBudget = 700;

    /// <summary>Requests a thumbnail at least <paramref name="pixels"/> wide. Call on the UI thread.</summary>
    public static void RequestThumbnail(FileItem item, int pixels)
    {
        if (item.IsRemote) return;
        Touch(item);
        if (item.ThumbnailRequestedSize >= pixels) return;
        item.ThumbnailRequestedSize = pixels;
        _ = LoadThumbnailAsync(item, pixels);
    }

    private static void Touch(FileItem item)
    {
        if (ThumbNodes.TryGetValue(item, out var node))
        {
            ThumbOrder.Remove(node);
            ThumbOrder.AddFirst(node);
            return;
        }
        ThumbNodes[item] = ThumbOrder.AddFirst(item);
        // Bitmaps are big; drop the least recently shown ones once a folder of photos has been scrolled through.
        while (ThumbOrder.Count > ThumbBudget)
        {
            var old = ThumbOrder.Last!.Value;
            ThumbOrder.RemoveLast();
            ThumbNodes.Remove(old);
            old.Thumbnail = null;
            old.ThumbnailRequestedSize = 0;
        }
    }

    private static async Task LoadThumbnailAsync(FileItem item, int pixels)
    {
        try
        {
            IImage? image = null;
            if (OperatingSystem.IsWindows())
            {
                image = await ShellThread.RunAsync(() =>
                {
                    var raw = ShellThumbnails.GetThumbnail(item.Path, pixels, allowIconFallback: false);
                    return raw is null ? null : (IImage)ToBitmap(raw);
                });
            }
            else if (item.Kind == ItemKind.File && FileKinds.IsDecodableImage(item.Extension))
            {
                image = await Task.Run(() =>
                {
                    using var s = File.OpenRead(item.Path);
                    return (IImage)Bitmap.DecodeToWidth(s, pixels, BitmapInterpolationMode.MediumQuality);
                });
            }

            if (image is null)
            {
                // No thumbnail: fall back to the biggest icon so tiles never look empty.
                RequestIcon(item, pixels, 1);
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (item.ThumbnailRequestedSize > 0) item.Thumbnail = image;
            });
        }
        catch (Exception)
        {
            // Unreadable or still being written: the icon stays.
        }
    }

    /// <summary>A large preview image for the preview pane (not cached).</summary>
    public static async Task<IImage?> LoadPreviewAsync(string path, int pixels, CancellationToken ct)
    {
        var ext = FileItem.GetExtension(Path.GetFileName(path));
        if (FileKinds.IsDecodableImage(ext))
        {
            try
            {
                return await Task.Run(() =>
                {
                    using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bmp = new Bitmap(s);
                    if (bmp.PixelSize.Width <= pixels) return (IImage)bmp;
                    // Huge photos: keep memory sane by decoding a downscaled copy.
                    bmp.Dispose();
                    s.Position = 0;
                    return Bitmap.DecodeToWidth(s, pixels, BitmapInterpolationMode.HighQuality);
                }, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Fall through to the shell, which may have a codec Skia lacks.
            }
        }
        if (!OperatingSystem.IsWindows()) return null;
        return await ShellThread.RunAsync(() =>
        {
            var raw = ShellThumbnails.GetThumbnail(path, pixels, allowIconFallback: false);
            return raw is null ? null : (IImage)ToBitmap(raw);
        }, ct);
    }

    /// <summary>A big icon (for previews of things without thumbnails).</summary>
    public static async Task<IImage?> LoadLargeIconAsync(FileItem item)
    {
        if (!OperatingSystem.IsWindows()) return FallbackIcons.For(item);
        return await ShellThread.RunAsync(() =>
        {
            var index = item.IsRemote
                ? Platform.Win32.ShellIcons.GetIconIndex(item.IsDirectory ? "folder" : "file" + item.Extension, item.IsDirectory, extensionOnly: true)
                : Platform.Win32.ShellIcons.GetIconIndex(item.Path, item.IsDirectory, extensionOnly: false);
            if (index < 0) return null;
            var raw = Platform.Win32.ShellIcons.GetIconImage(index, ShellIconSize.Jumbo);
            return raw is null ? null : (IImage)ToBitmap(raw);
        }) ?? FallbackIcons.For(item);
    }

    public static Bitmap ToBitmap(RawImage raw)
    {
        var handle = GCHandle.Alloc(raw.Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, raw.Premultiplied ? AlphaFormat.Premul : AlphaFormat.Unpremul,
                handle.AddrOfPinnedObject(), new PixelSize(raw.Width, raw.Height), new Vector(96, 96), raw.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>Vector icons used until (or instead of) the shell's: a folder, a drive, or a page tinted by file kind.</summary>
public static class FallbackIcons
{
    private static readonly Dictionary<FileKind, IImage> Cache = new();

    public static IImage For(FileItem item) => For(FileKinds.Of(item));

    public static IImage For(FileKind kind)
    {
        if (Cache.TryGetValue(kind, out var img)) return img;
        img = Build(kind);
        Cache[kind] = img;
        return img;
    }

    public static Color KindColor(FileKind kind) => kind switch
    {
        FileKind.Folder => Color.Parse("#F5C451"),
        FileKind.Drive => Color.Parse("#9AA3B6"),
        FileKind.Image => Color.Parse("#B07CFF"),
        FileKind.Video => Color.Parse("#F2666A"),
        FileKind.Audio => Color.Parse("#F5934C"),
        FileKind.Document => Color.Parse("#00C8FF"),
        FileKind.Text => Color.Parse("#C9CEDA"),
        FileKind.Code => Color.Parse("#19F58C"),
        FileKind.Archive => Color.Parse("#F5D04C"),
        FileKind.Executable => Color.Parse("#4DD0E1"),
        _ => Color.Parse("#8A92A6"),
    };

    private static IImage Build(FileKind kind)
    {
        var color = KindColor(kind);
        var group = new DrawingGroup();
        if (kind == FileKind.Folder)
        {
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromRgb((byte)(color.R * 0.78), (byte)(color.G * 0.78), (byte)(color.B * 0.78))),
                Geometry = Geometry.Parse("M2,6 A2,2 0 0 1 4,4 L9,4 L11,6 L20,6 A2,2 0 0 1 22,8 L22,10 L2,10 Z"),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(color),
                Geometry = Geometry.Parse("M2,9 L22,9 L22,18 A2,2 0 0 1 20,20 L4,20 A2,2 0 0 1 2,18 Z"),
            });
        }
        else if (kind == FileKind.Drive)
        {
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.Parse("#2A303C")),
                Pen = new Pen(new SolidColorBrush(color), 1.5),
                Geometry = Geometry.Parse("M3,8 A2,2 0 0 1 5,6 L19,6 A2,2 0 0 1 21,8 L21,16 A2,2 0 0 1 19,18 L5,18 A2,2 0 0 1 3,16 Z"),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.Parse("#19F58C")),
                Geometry = Geometry.Parse("M6,13 L10,13 L10,14.5 L6,14.5 Z"),
            });
        }
        else
        {
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.Parse("#232935")),
                Pen = new Pen(new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)), 1.3),
                Geometry = Geometry.Parse("M14,2.5 L6,2.5 A1.5,1.5 0 0 0 4.5,4 L4.5,20 A1.5,1.5 0 0 0 6,21.5 L18,21.5 A1.5,1.5 0 0 0 19.5,20 L19.5,8 Z"),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(color),
                Geometry = Geometry.Parse("M14,2.5 L14,8 L19.5,8 Z"),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B)),
                Geometry = Geometry.Parse("M7.5,13 L16.5,13 L16.5,14.5 L7.5,14.5 Z M7.5,16.5 L13.5,16.5 L13.5,18 L7.5,18 Z"),
            });
        }
        // Keep a 24×24 frame so every icon lines up regardless of its shape's extent.
        group.Children.Insert(0, new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, 24, 24)) });
        return new DrawingImage(group);
    }
}
