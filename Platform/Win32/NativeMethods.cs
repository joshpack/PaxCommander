using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

/// <summary>
/// Win32 declarations for the shell layer. Structs and constants are nested here so none of these short Win32 names
/// (POINT, SIZE, MSG…) leak into the pxdCommander.Platform.Win32 namespace that the UI imports.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class NativeMethods
{
    // ---- Win32 error codes ----
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_CANCELLED = 1223;
    internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    internal const int ERROR_CONNECTION_UNAVAIL = 1201;

    // ---- File attributes / SHGetFileInfo ----
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    internal const uint SHGFI_TYPENAME = 0x400;
    internal const uint SHGFI_SYSICONINDEX = 0x4000;

    // ---- Image lists / thumbnails ----
    internal const int SHIL_LARGE = 0;
    internal const int SHIL_SMALL = 1;
    internal const int SHIL_EXTRALARGE = 2;
    internal const int SHIL_JUMBO = 4;
    internal const int ILD_TRANSPARENT = 0x1;
    internal const int SIIGBF_RESIZETOFIT = 0x0;
    internal const int SIIGBF_THUMBNAILONLY = 0x8;

    // ---- Property system ----
    internal const int GPS_BESTEFFORT = 0x40;
    internal const int PDFF_DEFAULT = 0;
    internal const ushort VT_EMPTY = 0;

    // ---- ShellExecuteEx / Open With ----
    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNORMAL = 1;
    internal const int OAIF_ALLOW_REGISTRATION = 0x1;
    internal const int OAIF_REGISTER_EXT = 0x2;
    internal const int OAIF_EXEC = 0x4;
    internal const uint KF_FLAG_DEFAULT = 0;

    // ---- Context menus ----
    internal const uint CMF_NORMAL = 0x0;
    internal const uint CMF_EXPLORE = 0x4;
    internal const uint CMF_CANRENAME = 0x10;
    internal const uint CMF_EXTENDEDVERBS = 0x100;
    internal const uint GCS_VERBA = 0x0;
    internal const uint GCS_VERBW = 0x4;
    internal const uint CMIC_MASK_UNICODE = 0x00004000;
    internal const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    internal const uint CMIC_MASK_PTINVOKE = 0x20000000;
    internal const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint MIIM_STATE = 0x1;
    internal const uint MIIM_ID = 0x2;
    internal const uint MIIM_SUBMENU = 0x4;
    internal const uint MIIM_STRING = 0x40;
    internal const uint MIIM_FTYPE = 0x100;
    internal const uint MFT_SEPARATOR = 0x800;
    internal const uint MFT_OWNERDRAW = 0x100;
    internal const uint MFS_DISABLED = 0x3;
    internal const uint MFS_DEFAULT = 0x1000;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;

    // ---- Window messages ----
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_INITMENUPOPUP = 0x0117;
    internal const uint WM_MENUCHAR = 0x0120;
    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint PM_REMOVE = 0x1;
    internal const uint QS_ALLINPUT = 0x04FF;
    internal const uint MWMO_INPUTAVAILABLE = 0x4;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint INFINITE = 0xFFFFFFFF;

    // ---- Clipboard ----
    internal const uint CF_HDROP = 15;
    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint GMEM_ZEROINIT = 0x0040;
    internal const int DROPEFFECT_COPY = 1;
    internal const int DROPEFFECT_MOVE = 2;
    internal const int DROPEFFECT_LINK = 4;

    // ---- SHFileOperation / change notifications ----
    internal const uint FO_DELETE = 3;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOERRORUI = 0x0400;
    internal const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;
    internal const int SHCNE_UPDATEDIR = 0x00001000;
    internal const uint SHCNF_IDLIST = 0x0000;
    internal const uint SHCNF_PATHW = 0x0005;

    // ---- GDI ----
    internal const uint BI_RGB = 0;
    internal const uint DIB_RGB_COLORS = 0;

    // ---- Interface / folder ids ----
    internal static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    internal static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    internal static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    internal static readonly Guid IID_IPropertyDescription = new("6F79D558-3E96-4549-A1D1-7D75D2288814");
    internal static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    internal static readonly Guid FOLDERID_RecycleBinFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");

    // ---- Structs ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>Only the header is read directly; values go through PSFormatForDisplayAlloc and PropVariantClear.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcszClass;
        public int oaifInFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MENUITEMINFOW
    {
        public int cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>Header of a CF_HDROP block; the file list follows at <see cref="pFiles"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DROPFILES
    {
        public uint pFiles;
        public POINT pt;
        public int fNC;
        public int fWide;
    }

    // shellapi.h packs its structs to 1 byte on 32-bit Windows only, and the WORD fFlags makes that visible here,
    // so each bitness gets its own layout.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHFILEOPSTRUCTW64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SHFILEOPSTRUCTW32
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- shell32 ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetImageList(int iImageList, in Guid riid, out IntPtr ppvObj);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHGetPropertyStoreFromParsingName(string pszPath, IntPtr pbc, int flags, in Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetKnownFolderIDList(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW pExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHMultiFileProperties(IntPtr pdtobj, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHBindToParent(IntPtr pidl, in Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHBindToObject(IntPtr psf, IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHCreateShellItemArrayFromIDLists(uint cidl, [In] IntPtr[] rgpidl, out IntPtr ppsiItemArray);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCTW64 lpFileOp);

    [DllImport("shell32.dll", ExactSpelling = true, EntryPoint = "SHFileOperationW")]
    internal static extern int SHFileOperationW32(ref SHFILEOPSTRUCTW32 lpFileOp);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, char* lpszFile, uint cch);

    // ---- propsys ----

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int PSGetPropertyKeyFromName(string pszName, out PROPERTYKEY ppropkey);

    [DllImport("propsys.dll", ExactSpelling = true)]
    internal static extern int PSGetPropertyDescription(in PROPERTYKEY propkey, in Guid riid, out IntPtr ppv);

    [DllImport("propsys.dll", ExactSpelling = true)]
    internal static extern int PSFormatForDisplayAlloc(in PROPERTYKEY key, in PROPVARIANT propvar, int pdff, out IntPtr ppszDisplay);

    // ---- ole32 ----

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int PropVariantClear(ref PROPVARIANT pvar);

    // ---- mpr ----

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int WNetGetConnectionW(string lpLocalName, char* lpRemoteName, ref int lpnLength);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int WNetGetUniversalNameW(string lpLocalPath, int dwInfoLevel, void* lpBuffer, ref int lpBufferSize);

    internal const int UNIVERSAL_NAME_INFO_LEVEL = 1;

    // ---- user32 ----

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InsertMenuItemW(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFOW lpmi);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuItemInfoW(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFOW lpmii);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr DispatchMessageW(in MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr* pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RegisterClipboardFormatW(string lpszFormat);

    // ---- gdi32 ----

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int GetObjectW(IntPtr h, int c, void* pv);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, void* lpvBits, void* lpbmi, uint usage);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr ho);

    // ---- kernel32 ----

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern nuint GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetCurrentThreadId();
}
