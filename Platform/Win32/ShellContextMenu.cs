using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PaxCommander.Platform.Win32;

/// <summary>
/// Native Explorer context menus. Call on the UI thread (an STA with a message loop): the menu runs a modal loop on
/// the calling thread and its handlers may show UI owned by <c>owner</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellContextMenu
{
    private static readonly ContextMenuResult s_nothing = new(null, false);

    /// <summary>The verbs the app performs itself by default: rename, delete, cut, copy, paste (case-insensitive).</summary>
    public static IReadOnlySet<string> DefaultAppVerbs { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rename", "delete", "cut", "copy", "paste" };

    /// <summary>
    /// Shows the native Explorer context menu for items that share one parent folder, at screen pixel coordinates, and
    /// runs the chosen command. The verbs rename, delete, cut, copy and paste are NOT run — they come back with
    /// Invoked = false so the app does them itself (inline rename, undoable delete…).
    /// Items from different folders (search results) get the basic combined menu Explorer builds from the desktop folder.
    /// A "paste" from a folder item's menu means paste into that folder.
    /// </summary>
    public static ContextMenuResult ShowForItems(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool extendedVerbs) =>
        ShowForItems(owner, paths, screenX, screenY, extendedVerbs, DefaultAppVerbs);

    /// <summary>
    /// As above, with the app handling every verb in <paramref name="appVerbs"/> itself — e.g. <see cref="DefaultAppVerbs"/>
    /// plus "open", "opennewwindow" and "opennewtab" when the selection is folders, so they open in-app rather than in
    /// a new Explorer window.
    /// </summary>
    public static ContextMenuResult ShowForItems(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool extendedVerbs,
        IReadOnlySet<string> appVerbs)
    {
        try
        {
            using ShellMenuHost? menu = ShellMenuHost.ForItems(owner, paths, extendedVerbs);
            return menu?.ShowAndRun(screenX, screenY, appVerbs ?? DefaultAppVerbs) ?? s_nothing;
        }
        catch (Exception)
        {
            return s_nothing;
        }
    }

    /// <summary>The native menu for a folder's empty background ("New ▸", "Paste", "Properties"…); same rules as above.</summary>
    public static ContextMenuResult ShowForBackground(IntPtr owner, string folder, int screenX, int screenY, bool extendedVerbs)
    {
        try
        {
            using ShellMenuHost? menu = ShellMenuHost.ForBackground(owner, folder, extendedVerbs);
            return menu?.ShowAndRun(screenX, screenY, DefaultAppVerbs) ?? s_nothing;
        }
        catch (Exception)
        {
            return s_nothing;
        }
    }

    /// <summary>
    /// Optional: builds and discards an item menu on a <see cref="ShellThread"/> worker so the shell extensions it loads
    /// are already in memory at the first right-click (measured: first menu 2.0 s → 0.8 s). Call once after startup with
    /// any existing file or folder. Never throws.
    /// </summary>
    public static Task WarmUpAsync(string samplePath) => ShellThread.RunAsync(() =>
    {
        try
        {
            using ShellMenuHost? menu = ShellMenuHost.ForItems(IntPtr.Zero, [samplePath], extendedVerbs: false);
            return menu != null;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// Test hook: builds the item menu without showing it and returns one line per entry ("label [verb] &gt;").
    /// <paramref name="openSubmenus"/> sends each submenu the WM_INITMENUPOPUP a real menu would, so lazily filled
    /// submenus (Send to, Open with, New) are listed too.
    /// </summary>
    internal static IReadOnlyList<string> DescribeItemsMenu(IReadOnlyList<string> paths, bool extendedVerbs, bool openSubmenus)
    {
        using ShellMenuHost? menu = ShellMenuHost.ForItems(IntPtr.Zero, paths, extendedVerbs);
        return menu?.Describe(openSubmenus) ?? [];
    }

    /// <summary>Test hook: same as <see cref="DescribeItemsMenu"/> for a folder background.</summary>
    internal static IReadOnlyList<string> DescribeBackgroundMenu(string folder, bool extendedVerbs, bool openSubmenus)
    {
        using ShellMenuHost? menu = ShellMenuHost.ForBackground(IntPtr.Zero, folder, extendedVerbs);
        return menu?.Describe(openSubmenus) ?? [];
    }
}

/// <summary>One built context menu: the shell's IContextMenu, the HMENU it filled and everything they depend on.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ShellMenuHost : IDisposable
{
    // Shell handlers number their items from FirstCommandId to LastCommandId; items we add ourselves sit above that.
    internal const uint FirstCommandId = 1;
    internal const uint LastCommandId = 0x7EFF;
    private const uint PasteCommandId = 0x7F00;

    private readonly IntPtr _owner;
    private readonly string? _directory;
    private List<IntPtr>? _pidls;
    private IShellFolder? _source; // the folder the menu came from; kept alive as long as the menu
    private IContextMenu? _menu;
    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;
    private IntPtr _hmenu;

    private ShellMenuHost(IntPtr owner, string? directory, List<IntPtr> pidls)
    {
        _owner = owner;
        _directory = directory;
        _pidls = pidls;
    }

    internal static ShellMenuHost? ForItems(IntPtr owner, IReadOnlyList<string> paths, bool extendedVerbs)
    {
        if (paths == null || paths.Count == 0)
            return null;
        List<IntPtr>? pidls = ShellPidl.ParseAll(paths);
        if (pidls == null)
            return null;
        bool shareParent = ShellPidl.ShareParent(paths);
        var host = new ShellMenuHost(owner, shareParent ? ShellPidl.ParentDirectory(paths[0]) : null, pidls);
        uint flags = NativeMethods.CMF_NORMAL | NativeMethods.CMF_EXPLORE | NativeMethods.CMF_CANRENAME
                     | (extendedVerbs ? NativeMethods.CMF_EXTENDEDVERBS : 0);
        return host.Build(() => shareParent ? host.CreateForSiblings() : host.CreateForMixedParents(), flags, addPaste: false);
    }

    internal static ShellMenuHost? ForBackground(IntPtr owner, string folder, bool extendedVerbs)
    {
        IntPtr pidl = ShellPidl.Parse(folder);
        if (pidl == IntPtr.Zero)
            return null;
        // Virtual folders ("::{CLSID}" parsing names) have no working directory to hand to handlers.
        var host = new ShellMenuHost(owner, Directory.Exists(folder) ? folder : null, [pidl]);
        uint flags = NativeMethods.CMF_NORMAL | NativeMethods.CMF_EXPLORE | (extendedVerbs ? NativeMethods.CMF_EXTENDEDVERBS : 0);
        return host.Build(host.CreateForBackground, flags, addPaste: true);
    }

    private ShellMenuHost? Build(Func<IntPtr> createMenu, uint flags, bool addPaste)
    {
        try
        {
            IntPtr unknown = createMenu();
            if (unknown == IntPtr.Zero)
            {
                Dispose();
                return null;
            }
            _menu = ComInterop.Take<IContextMenu>(unknown);
            // Same private RCW, other interfaces: these are what make submenus and owner-drawn items work.
            _menu3 = _menu as IContextMenu3;
            _menu2 = _menu as IContextMenu2;

            _hmenu = NativeMethods.CreatePopupMenu();
            if (_hmenu == IntPtr.Zero || _menu.QueryContextMenu(_hmenu, 0, FirstCommandId, LastCommandId, flags) < 0)
            {
                Dispose();
                return null;
            }
            if (addPaste)
                InsertPasteItem();
            return this;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// In Explorer the background "Paste" comes from the folder view (DefView), not from the folder's menu object, so
    /// it is added here, at the top and greyed when the clipboard holds no files, as Explorer shows it.
    /// </summary>
    private void InsertPasteItem()
    {
        IntPtr text = Marshal.StringToHGlobalUni("&Paste");
        try
        {
            var paste = new NativeMethods.MENUITEMINFOW
            {
                cbSize = Marshal.SizeOf<NativeMethods.MENUITEMINFOW>(),
                fMask = NativeMethods.MIIM_ID | NativeMethods.MIIM_STRING | NativeMethods.MIIM_STATE,
                fState = NativeClipboard.ContainsFiles() ? 0 : NativeMethods.MFS_DISABLED,
                wID = PasteCommandId,
                dwTypeData = text,
            };
            if (!NativeMethods.InsertMenuItemW(_hmenu, 0, true, ref paste) || NativeMethods.GetMenuItemCount(_hmenu) < 2)
                return;
            var separator = new NativeMethods.MENUITEMINFOW
            {
                cbSize = Marshal.SizeOf<NativeMethods.MENUITEMINFOW>(),
                fMask = NativeMethods.MIIM_FTYPE,
                fType = NativeMethods.MFT_SEPARATOR,
            };
            NativeMethods.InsertMenuItemW(_hmenu, 1, true, ref separator);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>Items in one folder: the parent folder's own context menu object, as Explorer uses.</summary>
    private IntPtr CreateForSiblings()
    {
        if (NativeMethods.SHBindToParent(_pidls![0], NativeMethods.IID_IShellFolder, out IntPtr folder, out _) < 0 || folder == IntPtr.Zero)
            return IntPtr.Zero;
        var parent = ComInterop.Take<IShellFolder>(folder);
        _source = parent;
        // Child IDs point into the absolute PIDLs, which stay allocated until Dispose.
        IntPtr[] children = _pidls.Select(NativeMethods.ILFindLastID).ToArray();
        Guid iid = NativeMethods.IID_IContextMenu;
        return parent.GetUIObjectOf(_owner, (uint)children.Length, children, ref iid, IntPtr.Zero, out IntPtr menu) >= 0 ? menu : IntPtr.Zero;
    }

    /// <summary>
    /// Items from several folders (search results): the desktop folder accepts their full PIDLs and builds the combined
    /// menu (Open, Cut, Copy, Create shortcut, Delete, Rename, Properties). A shell item array's SFUIObject fails here.
    /// </summary>
    private IntPtr CreateForMixedParents()
    {
        if (NativeMethods.SHGetDesktopFolder(out IntPtr desktop) < 0 || desktop == IntPtr.Zero)
            return IntPtr.Zero;
        var folder = ComInterop.Take<IShellFolder>(desktop);
        _source = folder;
        Guid iid = NativeMethods.IID_IContextMenu;
        return folder.GetUIObjectOf(_owner, (uint)_pidls!.Count, [.. _pidls], ref iid, IntPtr.Zero, out IntPtr menu) >= 0 ? menu : IntPtr.Zero;
    }

    /// <summary>The folder's background menu comes from its view object, as for an Explorer window.</summary>
    private IntPtr CreateForBackground()
    {
        IntPtr pidl = _pidls![0];
        IntPtr folder;
        int hr = ShellPidl.IsEmpty(pidl)
            ? NativeMethods.SHGetDesktopFolder(out folder) // the desktop can't be bound to from itself
            : NativeMethods.SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, NativeMethods.IID_IShellFolder, out folder);
        if (hr < 0 || folder == IntPtr.Zero)
            return IntPtr.Zero;
        var shellFolder = ComInterop.Take<IShellFolder>(folder);
        _source = shellFolder;
        Guid iid = NativeMethods.IID_IContextMenu;
        return shellFolder.CreateViewObject(_owner, ref iid, out IntPtr menu) >= 0 ? menu : IntPtr.Zero;
    }

    internal ContextMenuResult ShowAndRun(int screenX, int screenY, IReadOnlySet<string> appVerbs)
    {
        int command = Track(screenX, screenY);
        if (command == PasteCommandId)
            return new ContextMenuResult("paste", false);
        if (command < FirstCommandId || command > LastCommandId)
            return new ContextMenuResult(null, false);

        uint offset = (uint)command - FirstCommandId;
        string? verb = GetVerb(offset);
        // Lower-cased for the comparison and the result, whatever case the handler registered the verb in.
        if (verb != null && (appVerbs.Contains(verb) || appVerbs.Contains(verb.ToLowerInvariant())))
            return new ContextMenuResult(verb.ToLowerInvariant(), false);
        Invoke(offset, screenX, screenY);
        return new ContextMenuResult(verb, true);
    }

    private int Track(int screenX, int screenY)
    {
        IntPtr helper = ShellHelperWindow.ForCurrentThread();
        ShellMenuHost? previous = ShellHelperWindow.ActiveMenu; // a handler could open a nested menu from within ours
        ShellHelperWindow.ActiveMenu = this;
        try
        {
            // The classic TrackPopupMenu fix: the menu only closes on a click elsewhere if this thread owns the foreground
            // window. Normally it already does (the user right-clicked our window), and stealing the foreground for the
            // hidden helper would deactivate that window and strand keyboard focus, so only take it when it isn't ours.
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(foreground, out _) != NativeMethods.GetCurrentThreadId())
                NativeMethods.SetForegroundWindow(helper);

            int command = NativeMethods.TrackPopupMenuEx(_hmenu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                screenX, screenY, helper, IntPtr.Zero);
            NativeMethods.PostMessageW(helper, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return command;
        }
        finally
        {
            ShellHelperWindow.ActiveMenu = previous;
        }
    }

    /// <summary>Called by the helper window's WndProc while this menu is up; null = not handled.</summary>
    internal IntPtr? HandleMenuMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_menu3 != null)
            return _menu3.HandleMenuMsg2(msg, wParam, lParam, out IntPtr result) >= 0 ? result : null;
        if (_menu2 != null && msg != NativeMethods.WM_MENUCHAR && _menu2.HandleMenuMsg(msg, wParam, lParam) >= 0)
            return msg == NativeMethods.WM_INITMENUPOPUP ? IntPtr.Zero : 1; // TRUE = owner-draw message handled
        return null;
    }

    /// <summary>Canonical (language-independent) verb of a command, e.g. "open", "delete"; null if it has none.</summary>
    internal unsafe string? GetVerb(uint offset)
    {
        if (_menu == null)
            return null;
        const int capacity = 256;
        char* buffer = stackalloc char[capacity + 1];
        var span = new Span<char>(buffer, capacity + 1);
        try
        {
            span.Clear();
            if (_menu.GetCommandString(offset, NativeMethods.GCS_VERBW, IntPtr.Zero, (IntPtr)buffer, capacity) >= 0)
            {
                string verb = new(buffer);
                if (IsPlausibleVerb(verb))
                    return verb;
            }
            // Old handlers implement only the ANSI query, or answer the Unicode one with ANSI bytes.
            span.Clear();
            if (_menu.GetCommandString(offset, NativeMethods.GCS_VERBA, IntPtr.Zero, (IntPtr)buffer, capacity) >= 0)
            {
                string verb = Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "";
                if (IsPlausibleVerb(verb))
                    return verb;
            }
        }
        catch (Exception)
        {
            // Some third-party handlers throw for commands that aren't theirs or have no verb.
        }
        return null;
    }

    private static bool IsPlausibleVerb(string verb)
    {
        if (verb.Length == 0)
            return false;
        foreach (char c in verb)
        {
            if (c is < ' ' or > '~')
                return false;
        }
        return true;
    }

    private void Invoke(uint offset, int screenX, int screenY)
    {
        IntPtr directoryW = IntPtr.Zero, directoryA = IntPtr.Zero;
        try
        {
            if (_directory != null)
            {
                // Handlers such as "Open in Terminal" read the working folder from here.
                directoryW = Marshal.StringToHGlobalUni(_directory);
                directoryA = Marshal.StringToHGlobalAnsi(_directory);
            }
            uint mask = NativeMethods.CMIC_MASK_UNICODE | NativeMethods.CMIC_MASK_PTINVOKE;
            if (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) < 0)
                mask |= NativeMethods.CMIC_MASK_SHIFT_DOWN;
            if (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) < 0)
                mask |= NativeMethods.CMIC_MASK_CONTROL_DOWN;
            var info = new NativeMethods.CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = _owner,
                // MAKEINTRESOURCE(offset): identifies the command by position rather than by verb string.
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectory = directoryA,
                lpDirectoryW = directoryW,
                nShow = NativeMethods.SW_SHOWNORMAL,
                ptInvoke = new NativeMethods.POINT(screenX, screenY),
            };
            _menu!.InvokeCommand(ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(directoryW);
            Marshal.FreeHGlobal(directoryA);
        }
    }

    internal List<string> Describe(bool openSubmenus)
    {
        var lines = new List<string>();
        DescribeMenu(_hmenu, 0, lines, openSubmenus);
        return lines;
    }

    private unsafe void DescribeMenu(IntPtr hmenu, int depth, List<string> lines, bool openSubmenus)
    {
        int count = NativeMethods.GetMenuItemCount(hmenu);
        for (int i = 0; i < count; i++)
        {
            var item = new NativeMethods.MENUITEMINFOW
            {
                cbSize = Marshal.SizeOf<NativeMethods.MENUITEMINFOW>(),
                fMask = NativeMethods.MIIM_FTYPE | NativeMethods.MIIM_ID | NativeMethods.MIIM_SUBMENU | NativeMethods.MIIM_STATE | NativeMethods.MIIM_STRING,
            };
            if (!NativeMethods.GetMenuItemInfoW(hmenu, (uint)i, true, ref item))
                continue;
            string indent = new(' ', depth * 2);
            if ((item.fType & NativeMethods.MFT_SEPARATOR) != 0)
            {
                lines.Add(indent + "-----");
                continue;
            }

            uint id = item.wID, type = item.fType, state = item.fState;
            IntPtr submenu = item.hSubMenu;
            string text = (type & NativeMethods.MFT_OWNERDRAW) != 0 ? "(owner-drawn)" : "";
            if (item.cch > 0)
            {
                var chars = new char[item.cch + 1];
                fixed (char* p = chars)
                {
                    item.fMask = NativeMethods.MIIM_STRING;
                    item.dwTypeData = (IntPtr)p;
                    item.cch = (uint)chars.Length;
                    if (NativeMethods.GetMenuItemInfoW(hmenu, (uint)i, true, ref item))
                        text = new string(p).Replace("&", "");
                }
            }

            string? verb = id == PasteCommandId ? "paste"
                : submenu == IntPtr.Zero && id is >= FirstCommandId and <= LastCommandId ? GetVerb(id - FirstCommandId) : null;
            lines.Add(indent + text
                      + (verb != null ? $" [{verb}]" : "")
                      + ((state & NativeMethods.MFS_DEFAULT) != 0 ? " (default)" : "")
                      + ((state & NativeMethods.MFS_DISABLED) != 0 ? " (disabled)" : "")
                      + (submenu != IntPtr.Zero ? " >" : ""));
            if (submenu != IntPtr.Zero)
            {
                if (openSubmenus)
                    HandleMenuMessage(NativeMethods.WM_INITMENUPOPUP, submenu, i);
                DescribeMenu(submenu, depth + 1, lines, openSubmenus);
            }
        }
    }

    public void Dispose()
    {
        // Menu first, then the handlers that may still reference it, then the folder and the PIDLs they were built on.
        if (_hmenu != IntPtr.Zero)
        {
            NativeMethods.DestroyMenu(_hmenu);
            _hmenu = IntPtr.Zero;
        }
        ComInterop.Release(_menu);
        _menu = null;
        _menu2 = null;
        _menu3 = null;
        ComInterop.Release(_source);
        _source = null;
        ShellPidl.FreeAll(_pidls);
        _pidls = null;
    }
}
