using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace pxdCommander.Platform.Win32;

/// <summary>
/// File lists on the Windows clipboard in Explorer's format. Call on the UI thread: the clipboard owner is a helper
/// window on the calling thread, and Windows sends that window messages later (e.g. when another app empties the
/// clipboard), so the thread must keep pumping messages.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeClipboard
{
    private static readonly Lazy<uint> s_preferredDropEffect = new(() => NativeMethods.RegisterClipboardFormatW("Preferred DropEffect"));

    /// <summary>Puts files on the Windows clipboard the way Explorer does (CF_HDROP + Preferred DropEffect), so Explorer can paste them.</summary>
    public static bool SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths == null || paths.Count == 0)
            return false;
        IntPtr hDrop = IntPtr.Zero, hEffect = IntPtr.Zero;
        try
        {
            hDrop = CreateHGlobal(BuildDropFiles(paths));
            // Explorer's own values: move for cut; copy|link for copy (so "Paste shortcut" stays available).
            int effect = cut ? NativeMethods.DROPEFFECT_MOVE : NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_LINK;
            hEffect = CreateHGlobal(BitConverter.GetBytes(effect));
            if (hDrop == IntPtr.Zero || hEffect == IntPtr.Zero)
                return false;

            // EmptyClipboard makes the window passed to OpenClipboard the owner; with no window SetClipboardData fails.
            if (!OpenClipboard(ShellHelperWindow.ForCurrentThread()))
                return false;
            try
            {
                if (!NativeMethods.EmptyClipboard())
                    return false;
                if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hDrop) == IntPtr.Zero)
                    return false;
                hDrop = IntPtr.Zero; // owned by the clipboard from here on
                if (NativeMethods.SetClipboardData(s_preferredDropEffect.Value, hEffect) != IntPtr.Zero)
                    hEffect = IntPtr.Zero;
                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (hDrop != IntPtr.Zero)
                NativeMethods.GlobalFree(hDrop);
            if (hEffect != IntPtr.Zero)
                NativeMethods.GlobalFree(hEffect);
        }
    }

    /// <summary>Files on the clipboard (from Explorer or this app), or null. <paramref name="cut"/> tells a cut from a copy.</summary>
    public static IReadOnlyList<string>? GetFiles(out bool cut)
    {
        cut = false;
        try
        {
            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP) || !OpenClipboard(IntPtr.Zero))
                return null;
            try
            {
                IntPtr hDrop = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
                if (hDrop == IntPtr.Zero)
                    return null;
                // The clipboard keeps owning hDrop: read it, never DragFinish it.
                List<string> files = ReadHDrop(hDrop);
                IntPtr hEffect = NativeMethods.GetClipboardData(s_preferredDropEffect.Value);
                cut = hEffect != IntPtr.Zero && (ReadDropEffect(hEffect) & NativeMethods.DROPEFFECT_MOVE) != 0;
                return files.Count > 0 ? files : null;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            cut = false;
            return null;
        }
    }

    public static bool ContainsFiles()
    {
        try
        {
            return NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Another app (a clipboard manager, a remote desktop client) may hold the clipboard for a moment.</summary>
    private static bool OpenClipboard(IntPtr owner)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner))
                return true;
            Thread.Sleep(15 + attempt * 5);
        }
        return false;
    }

    /// <summary>A CF_HDROP block: DROPFILES header, then each path NUL-terminated as UTF-16, then a final NUL.</summary>
    internal static unsafe byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        int headerSize = sizeof(NativeMethods.DROPFILES);
        var text = new StringBuilder();
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0'))
                throw new ArgumentException("Clipboard paths must be non-empty and contain no NUL characters.", nameof(paths));
            text.Append(path).Append('\0');
        }
        text.Append('\0');

        byte[] list = Encoding.Unicode.GetBytes(text.ToString());
        var buffer = new byte[headerSize + list.Length];
        var header = new NativeMethods.DROPFILES { pFiles = (uint)headerSize, fWide = 1 };
        MemoryMarshal.Write(buffer, in header);
        list.CopyTo(buffer, headerSize);
        return buffer;
    }

    internal static IntPtr CreateHGlobal(byte[] data)
    {
        IntPtr handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (nuint)data.Length);
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;
        IntPtr p = NativeMethods.GlobalLock(handle);
        if (p == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return IntPtr.Zero;
        }
        Marshal.Copy(data, 0, p, data.Length);
        NativeMethods.GlobalUnlock(handle);
        return handle;
    }

    /// <summary>Paths in an HDROP (either our wide format or an old ANSI one; DragQueryFileW converts).</summary>
    internal static unsafe List<string> ReadHDrop(IntPtr hDrop)
    {
        uint count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)Math.Min(count, 4096u));
        for (uint i = 0; i < count; i++)
        {
            uint length = NativeMethods.DragQueryFileW(hDrop, i, null, 0);
            if (length == 0)
                continue;
            var chars = new char[length + 1];
            fixed (char* p = chars)
            {
                uint copied = NativeMethods.DragQueryFileW(hDrop, i, p, (uint)chars.Length);
                if (copied > 0)
                    files.Add(new string(p, 0, (int)copied));
            }
        }
        return files;
    }

    internal static int ReadDropEffect(IntPtr hEffect)
    {
        if (NativeMethods.GlobalSize(hEffect) < 4)
            return 0;
        IntPtr p = NativeMethods.GlobalLock(hEffect);
        if (p == IntPtr.Zero)
            return 0;
        try
        {
            return Marshal.ReadInt32(p);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hEffect);
        }
    }
}
