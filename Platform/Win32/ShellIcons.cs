using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

[SupportedOSPlatform("windows")]
public static class ShellIcons
{
    // Image list RCWs per thread: the lists are process-wide objects, and a per-thread wrapper keeps every call in
    // the caller's own apartment (see ComInterop.Take). Indexed by SHIL value.
    [ThreadStatic] private static IImageList?[]? t_imageLists;

    private static readonly int[] s_standardSizes = [16, 20, 24, 32, 40, 48, 64, 72, 96, 128];

    /// <summary>
    /// Index of the item's icon in the system image list, or -1. <paramref name="extensionOnly"/> = true never touches the
    /// disk (SHGFI_USEFILEATTRIBUTES) and is right for ordinary documents; false reads per-file icons (exe, ico, lnk, url,
    /// folders with desktop.ini, drive roots). Call on a <see cref="ShellThread"/>.
    /// </summary>
    public static int GetIconIndex(string path, bool isDirectory, bool extensionOnly)
    {
        if (string.IsNullOrEmpty(path))
            return -1;
        try
        {
            var info = new NativeMethods.SHFILEINFOW();
            uint size = (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>();
            if (!extensionOnly)
            {
                if (NativeMethods.SHGetFileInfoW(path, 0, ref info, size, NativeMethods.SHGFI_SYSICONINDEX) != IntPtr.Zero)
                    return info.iIcon;
                // The item vanished, is offline or the path is too long for the shell: the type's icon beats none.
            }

            // Only the extension matters here, so a short stand-in name also sidesteps MAX_PATH limits.
            string query = isDirectory ? "folder" : "file" + Path.GetExtension(path);
            uint attributes = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
            IntPtr ok = NativeMethods.SHGetFileInfoW(query, attributes, ref info, size,
                NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_USEFILEATTRIBUTES);
            return ok != IntPtr.Zero ? info.iIcon : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Pixels of a system image list icon at the given size, or null. Call on a <see cref="ShellThread"/>.
    /// Pixels are straight (not premultiplied) alpha. Jumbo asks for 256 px, but for icons that have no large artwork
    /// the shell parks the small image in the top-left corner of the 256 px cell; those come back cropped to that
    /// image (e.g. 48×48), so check <see cref="RawImage.Width"/> rather than assuming the requested size.
    /// </summary>
    public static RawImage? GetIconImage(int index, ShellIconSize size) => GetIconImage(index, size, cropParkedJumbo: true);

    internal static RawImage? GetIconImage(int index, ShellIconSize size, bool cropParkedJumbo)
    {
        if (index < 0)
            return null;
        try
        {
            IImageList? list = GetImageList(size);
            if (list == null || list.GetIcon(index, NativeMethods.ILD_TRANSPARENT, out IntPtr hIcon) < 0 || hIcon == IntPtr.Zero)
                return null;
            try
            {
                RawImage? image = ShellBitmaps.FromIcon(hIcon);
                return image != null && cropParkedJumbo && size == ShellIconSize.Jumbo ? CropParkedSmallIcon(image) : image;
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IImageList? GetImageList(ShellIconSize size)
    {
        // By name, not by value: the enum's Small/Large numbers are the reverse of SHIL_SMALL/SHIL_LARGE.
        int shil = size switch
        {
            ShellIconSize.Small => NativeMethods.SHIL_SMALL,
            ShellIconSize.Large => NativeMethods.SHIL_LARGE,
            ShellIconSize.ExtraLarge => NativeMethods.SHIL_EXTRALARGE,
            ShellIconSize.Jumbo => NativeMethods.SHIL_JUMBO,
            _ => -1,
        };
        if (shil < 0)
            return null;
        var lists = t_imageLists ??= new IImageList?[5];
        if (lists[shil] != null)
            return lists[shil];
        if (NativeMethods.SHGetImageList(shil, NativeMethods.IID_IImageList, out IntPtr unknown) < 0 || unknown == IntPtr.Zero)
            return null;
        return lists[shil] = ComInterop.Take<IImageList>(unknown);
    }

    /// <summary>
    /// If everything visible sits inside the top-left quarter of a jumbo cell, the icon has no large artwork and the
    /// shell put its biggest small image there unscaled; return just that image so the UI can centre or scale it.
    /// </summary>
    private static RawImage CropParkedSmallIcon(RawImage image)
    {
        int w = image.Width, h = image.Height;
        if (w < 128 || h < 128)
            return image;
        int maxX = -1, maxY = -1;
        byte[] px = image.Pixels;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (px[row + x * 4 + 3] == 0)
                    continue;
                if (x >= w / 2 || y >= h / 2)
                    return image; // real large artwork
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0)
            return image; // fully transparent: nothing to judge by

        int extent = Math.Max(maxX, maxY) + 1;
        int side = s_standardSizes.FirstOrDefault(s => s >= extent);
        if (side == 0)
            side = extent;
        var cropped = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            Buffer.BlockCopy(px, y * w * 4, cropped, y * side * 4, side * 4);
        return new RawImage(side, side, cropped, image.Premultiplied);
    }
}
