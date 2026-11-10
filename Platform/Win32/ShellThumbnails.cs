using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

[SupportedOSPlatform("windows")]
public static class ShellThumbnails
{
    /// <summary>
    /// Explorer's thumbnail for the item (images, video frames, PDFs, Office files… whatever handlers are installed),
    /// fitted inside <paramref name="sizePx"/>. Null when there is none and <paramref name="allowIconFallback"/> is false.
    /// Call on a <see cref="ShellThread"/>. Pixels are straight-alpha BGRA (fully opaque when the source has no alpha).
    /// </summary>
    public static RawImage? GetThumbnail(string path, int sizePx, bool allowIconFallback)
    {
        if (string.IsNullOrEmpty(path) || sizePx <= 0)
            return null;
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, NativeMethods.IID_IShellItemImageFactory, out IntPtr unknown) < 0
                || unknown == IntPtr.Zero)
                return null;
            factory = ComInterop.Take<IShellItemImageFactory>(unknown);

            int flags = NativeMethods.SIIGBF_RESIZETOFIT | (allowIconFallback ? 0 : NativeMethods.SIIGBF_THUMBNAILONLY);
            if (factory.GetImage(new NativeMethods.SIZE(sizePx, sizePx), flags, out hbm) < 0 || hbm == IntPtr.Zero)
                return null;

            byte[]? pixels = ShellBitmaps.ReadBits(hbm, out int width, out int height);
            if (pixels == null)
                return null;
            // Opaque sources (JPEG, video frames) come back with the alpha byte left at zero.
            if (!ShellBitmaps.HasAlpha(pixels))
                ShellBitmaps.MakeOpaque(pixels);
            // Not premultiplied: measured on Windows 11 for PNG thumbnails, icon fallbacks, folders and drives alike
            // (a 50 % red PNG reads B,G,R,A = 0,0,254,127).
            return new RawImage(width, height, pixels, Premultiplied: false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero)
                NativeMethods.DeleteObject(hbm);
            ComInterop.Release(factory);
        }
    }
}
