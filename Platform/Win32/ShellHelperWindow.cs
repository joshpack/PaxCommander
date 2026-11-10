using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

/// <summary>
/// A hidden top-level window per thread. It owns native context menus (TrackPopupMenuEx needs an owner window on the
/// calling thread, and that owner receives the menu's WM_INITMENUPOPUP / owner-draw traffic, which must be forwarded
/// to the shell's IContextMenu2/3) and the clipboard (SetClipboardData fails without an owner window).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellHelperWindow
{
    private const string ClassName = "pxdCommander.ShellHelperWindow";

    // Rooted for the life of the process: the registered window class keeps calling through this delegate.
    private static readonly NativeMethods.WndProc s_wndProc = WndProc;
    private static readonly object s_classLock = new();
    private static bool s_classRegistered;

    [ThreadStatic] private static IntPtr t_hwnd;
    [ThreadStatic] private static ShellMenuHost? t_activeMenu;

    /// <summary>The menu currently tracked on this thread; its owner-draw and popup messages are forwarded to it.</summary>
    internal static ShellMenuHost? ActiveMenu
    {
        get => t_activeMenu;
        set => t_activeMenu = value;
    }

    /// <summary>The calling thread's helper window, created on first use and kept until the thread ends.</summary>
    internal static IntPtr ForCurrentThread()
    {
        if (t_hwnd != IntPtr.Zero && NativeMethods.IsWindow(t_hwnd))
            return t_hwnd;

        EnsureClassRegistered();
        IntPtr hwnd = NativeMethods.CreateWindowExW(NativeMethods.WS_EX_TOOLWINDOW, ClassName, "pxdCommander shell helper",
            NativeMethods.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the shell helper window.");
        t_hwnd = hwnd;
        return hwnd;
    }

    private static void EnsureClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_classRegistered)
                return;
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = NativeMethods.GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassExW(ref wc) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
                    throw new Win32Exception(error, "Could not register the shell helper window class.");
            }
            s_classRegistered = true;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            ShellMenuHost? menu = t_activeMenu;
            if (menu != null && msg is NativeMethods.WM_INITMENUPOPUP or NativeMethods.WM_DRAWITEM
                    or NativeMethods.WM_MEASUREITEM or NativeMethods.WM_MENUCHAR)
            {
                IntPtr? handled = menu.HandleMenuMessage(msg, wParam, lParam);
                if (handled.HasValue)
                    return handled.Value;
            }
        }
        catch (Exception)
        {
            // A managed exception must never unwind into user32's menu loop; fall back to default handling.
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
