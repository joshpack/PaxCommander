using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

/// <summary>RCW plumbing shared by the shell classes.</summary>
[SupportedOSPlatform("windows")]
internal static class ComInterop
{
    /// <summary>
    /// Wraps a raw interface pointer (whose reference we own) in an RCW that nobody else shares. The runtime normally
    /// keeps one RCW per COM identity process-wide, so singletons such as the desktop folder, the system image lists or
    /// cached property descriptions would otherwise be shared between the UI thread and the shell workers: calls would
    /// be marshalled across apartments, and one thread's FinalReleaseComObject would kill the other's object.
    /// </summary>
    internal static T Take<T>(IntPtr unknown) where T : class
    {
        object rcw;
        try
        {
            rcw = Marshal.GetUniqueObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
        if (rcw is T typed)
            return typed;
        Marshal.FinalReleaseComObject(rcw);
        throw new InvalidCastException($"COM object does not implement {typeof(T).Name}.");
    }

    /// <summary>Releases an RCW from <see cref="Take{T}"/> right away instead of leaving it to the finalizer thread.</summary>
    internal static void Release(object? rcw)
    {
        if (rcw != null && Marshal.IsComObject(rcw))
            Marshal.FinalReleaseComObject(rcw);
    }
}

/// <summary>Item ID list helpers. PIDLs from SHParseDisplayName are CoTaskMem allocations.</summary>
[SupportedOSPlatform("windows")]
internal static class ShellPidl
{
    internal static IntPtr Parse(string path)
    {
        if (string.IsNullOrEmpty(path))
            return IntPtr.Zero;
        return NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) >= 0 ? pidl : IntPtr.Zero;
    }

    /// <summary>Parses every path; null (with nothing leaked) if any of them can't be parsed.</summary>
    internal static List<IntPtr>? ParseAll(IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>(paths.Count);
        foreach (string path in paths)
        {
            IntPtr pidl = Parse(path);
            if (pidl == IntPtr.Zero)
            {
                FreeAll(pidls);
                return null;
            }
            pidls.Add(pidl);
        }
        return pidls;
    }

    internal static void Free(IntPtr pidl)
    {
        if (pidl != IntPtr.Zero)
            Marshal.FreeCoTaskMem(pidl);
    }

    internal static void FreeAll(List<IntPtr>? pidls)
    {
        if (pidls == null)
            return;
        foreach (IntPtr pidl in pidls)
            Free(pidl);
        pidls.Clear();
    }

    /// <summary>The desktop's own PIDL is just the terminator.</summary>
    internal static bool IsEmpty(IntPtr pidl) => pidl == IntPtr.Zero || Marshal.ReadInt16(pidl) == 0;

    /// <summary>
    /// The folder that contains a file system path, compared case-insensitively; null for drive roots and UNC shares
    /// (their parent is a virtual folder such as This PC or Network).
    /// </summary>
    internal static string? ParentDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True when every path has the same parent folder (the precondition for IShellFolder.GetUIObjectOf).</summary>
    internal static bool ShareParent(IReadOnlyList<string> paths)
    {
        string? first = ParentDirectory(paths[0]);
        for (int i = 1; i < paths.Count; i++)
        {
            if (!string.Equals(first, ParentDirectory(paths[i]), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
