using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

/// <summary>Turns GDI bitmaps and icons into <see cref="RawImage"/> pixels.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ShellBitmaps
{
    /// <summary>
    /// Copies any HBITMAP (DDB or DIB section, any depth) out as 32-bpp top-down BGRA. GDI converts the format for us;
    /// a 32-bpp source keeps its fourth byte, which is where icon and thumbnail alpha lives.
    /// </summary>
    internal static byte[]? ReadBits(IntPtr hbm, out int width, out int height)
    {
        width = height = 0;
        if (hbm == IntPtr.Zero)
            return null;

        NativeMethods.BITMAP bm;
        if (NativeMethods.GetObjectW(hbm, sizeof(NativeMethods.BITMAP), &bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0)
            return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if ((long)w * h > 64L * 1024 * 1024)
            return null;

        // Room for a colour table after the header: GetDIBits writes one for some formats even when asked for 32 bpp.
        byte* info = stackalloc byte[sizeof(NativeMethods.BITMAPINFOHEADER) + 256 * 4];
        new Span<byte>(info, sizeof(NativeMethods.BITMAPINFOHEADER) + 256 * 4).Clear();
        var header = (NativeMethods.BITMAPINFOHEADER*)info;
        header->biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER);
        header->biWidth = w;
        header->biHeight = -h; // negative = top-down rows
        header->biPlanes = 1;
        header->biBitCount = 32;
        header->biCompression = NativeMethods.BI_RGB;

        var pixels = new byte[w * h * 4];
        IntPtr hdc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return null;
        try
        {
            fixed (byte* p = pixels)
            {
                if (NativeMethods.GetDIBits(hdc, hbm, 0, (uint)h, p, info, NativeMethods.DIB_RGB_COLORS) != h)
                    return null;
            }
        }
        finally
        {
            NativeMethods.DeleteDC(hdc);
        }
        width = w;
        height = h;
        return pixels;
    }

    /// <summary>
    /// HICON → straight-alpha BGRA. 32-bpp icons carry their own alpha; older icons have zero alpha everywhere and
    /// express transparency through the AND mask instead. The caller still owns (and destroys) the icon.
    /// </summary>
    internal static RawImage? FromIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero || !NativeMethods.GetIconInfo(hIcon, out NativeMethods.ICONINFO ii))
            return null;
        try
        {
            if (ii.hbmColor != IntPtr.Zero)
            {
                byte[]? color = ReadBits(ii.hbmColor, out int w, out int h);
                if (color == null)
                    return null;
                if (!HasAlpha(color))
                {
                    byte[]? mask = ReadBits(ii.hbmMask, out int mw, out int mh);
                    if (mask != null && mw == w && mh >= h)
                        ApplyAndMask(color, mask);
                    else
                        MakeOpaque(color);
                }
                return new RawImage(w, h, color, Premultiplied: false);
            }

            // Monochrome icon: one bitmap twice as tall, AND mask on top and XOR (the image) below.
            byte[]? both = ReadBits(ii.hbmMask, out int mwidth, out int mheight);
            if (both == null || mheight < 2)
                return null;
            int half = mheight / 2, stride = mwidth * 4;
            var pixels = new byte[stride * half];
            Buffer.BlockCopy(both, stride * half, pixels, 0, stride * half);
            ApplyAndMask(pixels, both);
            return new RawImage(mwidth, half, pixels, Premultiplied: false);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero)
                NativeMethods.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero)
                NativeMethods.DeleteObject(ii.hbmMask);
        }
    }

    internal static bool HasAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
                return true;
        }
        return false;
    }

    internal static void MakeOpaque(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
    }

    /// <summary>
    /// AND-mask bit 0 = opaque pixel, 1 = transparent (screen shows through). The mask arrives expanded to 32 bpp,
    /// so a set bit reads as white. Transparent pixels are zeroed so the result is valid premultiplied data too.
    /// </summary>
    private static void ApplyAndMask(byte[] bgra, byte[] mask)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bool transparent = mask[i] != 0 || mask[i + 1] != 0 || mask[i + 2] != 0;
            if (transparent)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = bgra[i + 3] = 0;
            }
            else
            {
                bgra[i + 3] = 255;
            }
        }
    }
}
