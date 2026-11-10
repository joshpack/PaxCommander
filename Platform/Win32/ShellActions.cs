using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

[SupportedOSPlatform("windows")]
public static class ShellActions
{
    /// <summary>
    /// ShellExecuteEx with an optional verb ("open", "edit", "runas"…). Throws Win32Exception on failure; a cancelled
    /// prompt (UAC, "How do you want to open this?") is not a failure and returns quietly. Without a working directory
    /// the item's own folder is used, as Explorer does (installers and portable apps rely on it).
    /// Works from any thread: off an STA it runs on the shell's operations thread and waits.
    /// </summary>
    public static void Open(string path, string? verb = null, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? directory = string.IsNullOrEmpty(workingDirectory) ? DefaultDirectory(path) : workingDirectory;
        StaInvoker.Invoke(() =>
        {
            var info = new NativeMethods.SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFOW>(),
                // INVOKEIDLIST routes the verb through the item's context menu, so handler-provided verbs work too.
                fMask = string.IsNullOrEmpty(verb) ? 0 : NativeMethods.SEE_MASK_INVOKEIDLIST,
                lpVerb = string.IsNullOrEmpty(verb) ? null : verb,
                lpFile = path,
                lpDirectory = directory,
                nShow = NativeMethods.SW_SHOWNORMAL,
            };
            if (!NativeMethods.ShellExecuteExW(ref info))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ERROR_CANCELLED)
                    throw new Win32Exception(error);
            }
            return true;
        });
    }

    /// <summary>The system "Open with" chooser. Shows its own UI; failures are swallowed.</summary>
    public static void OpenWith(IntPtr owner, string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            StaInvoker.Invoke(() =>
            {
                var info = new NativeMethods.OPENASINFO
                {
                    pcszFile = path,
                    oaifInFlags = NativeMethods.OAIF_ALLOW_REGISTRATION | NativeMethods.OAIF_REGISTER_EXT | NativeMethods.OAIF_EXEC,
                };
                return NativeMethods.SHOpenWithDialog(owner, ref info);
            });
        }
        catch (Exception)
        {
            // Nothing useful to report: the dialog shows its own errors and cancelling is normal.
        }
    }

    /// <summary>The Explorer properties sheet for one or several items (several = combined sheet). Never throws.</summary>
    public static void ShowProperties(IntPtr owner, IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
            return;
        try
        {
            StaInvoker.Invoke(() => paths.Count == 1 ? ShowSingleProperties(owner, paths[0]) : ShowMultiProperties(owner, paths));
        }
        catch (Exception)
        {
        }
    }

    private static bool ShowSingleProperties(IntPtr owner, string path)
    {
        var info = new NativeMethods.SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFOW>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = NativeMethods.SW_SHOW,
        };
        // The sheet runs on a thread of its own inside this process; this returns once it is up.
        return NativeMethods.ShellExecuteExW(ref info);
    }

    private static bool ShowMultiProperties(IntPtr owner, IReadOnlyList<string> paths)
    {
        List<IntPtr>? pidls = ShellPidl.ParseAll(paths);
        if (pidls == null)
            return false;
        IShellFolder? parent = null;
        IShellItemArray? array = null;
        IntPtr dataObject = IntPtr.Zero;
        try
        {
            if (ShellPidl.ShareParent(paths))
            {
                if (NativeMethods.SHBindToParent(pidls[0], NativeMethods.IID_IShellFolder, out IntPtr folder, out _) < 0)
                    return false;
                parent = ComInterop.Take<IShellFolder>(folder);
                IntPtr[] children = pidls.Select(NativeMethods.ILFindLastID).ToArray();
                Guid iid = NativeMethods.IID_IDataObject;
                if (parent.GetUIObjectOf(owner, (uint)children.Length, children, ref iid, IntPtr.Zero, out dataObject) < 0)
                    return false;
            }
            else
            {
                // Items from different folders (search results): a shell item array can still produce one data object.
                if (NativeMethods.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out IntPtr items) < 0)
                    return false;
                array = ComInterop.Take<IShellItemArray>(items);
                Guid bhid = NativeMethods.BHID_DataObject, iid = NativeMethods.IID_IDataObject;
                if (array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out dataObject) < 0)
                    return false;
            }
            // The combined sheet keeps its own reference to the data object.
            return NativeMethods.SHMultiFileProperties(dataObject, 0) >= 0;
        }
        finally
        {
            if (dataObject != IntPtr.Zero)
                Marshal.Release(dataObject);
            ComInterop.Release(array);
            ComInterop.Release(parent);
            ShellPidl.FreeAll(pidls);
        }
    }

    private static string? DefaultDirectory(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || Directory.Exists(path))
                return null;
            return Path.GetDirectoryName(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
