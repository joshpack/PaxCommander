using System.Runtime.InteropServices;

namespace pxdCommander.Platform.Win32;

// Classic COM interop declarations. Every method is [PreserveSig] so failures come back as HRESULTs (the callers turn
// them into null / empty results) and interface pointers come back raw, to be wrapped by ComInterop.Take — an RCW
// private to the calling thread. Derived interfaces repeat their base methods because [ComImport] inheritance does
// not lay out the base vtable.

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
    [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
    [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
}

[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(NativeMethods.SIZE size, int flags, out IntPtr phbm);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(ref NativeMethods.PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IntPtr ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

/// <summary>comctl32's image list as returned by SHGetImageList (commoncontrols.h order).</summary>
[ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
    [PreserveSig] int Draw(IntPtr pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    [PreserveSig] int GetImageInfo(int i, IntPtr pImageInfo);
    [PreserveSig] int Copy(int iDst, IntPtr punkSrc, int iSrc, int uFlags);
    [PreserveSig] int Merge(int i1, IntPtr punk2, int i2, int dx, int dy, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Clone(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetImageRect(int i, IntPtr prc);
    [PreserveSig] int GetIconSize(out int cx, out int cy);
    [PreserveSig] int SetIconSize(int cx, int cy);
    [PreserveSig] int GetImageCount(out int pi);
    [PreserveSig] int SetImageCount(int uNewCount);
    [PreserveSig] int SetBkColor(int clrBk, out int pclr);
    [PreserveSig] int GetBkColor(out int pclr);
    [PreserveSig] int BeginDrag(int iTrack, int dxHotspot, int dyHotspot);
    [PreserveSig] int EndDrag();
    [PreserveSig] int DragEnter(IntPtr hwndLock, int x, int y);
    [PreserveSig] int DragLeave(IntPtr hwndLock);
    [PreserveSig] int DragMove(int x, int y);
    [PreserveSig] int SetDragCursorImage(IntPtr punk, int iDrag, int dxHotspot, int dyHotspot);
    [PreserveSig] int DragShowNolock(int fShow);
    [PreserveSig] int GetDragImage(IntPtr ppt, IntPtr pptHotspot, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetItemFlags(int i, out int dwFlags);
    [PreserveSig] int GetOverlayImage(int iOverlay, out int piIndex);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint cProps);
    [PreserveSig] int GetAt(uint iProp, out NativeMethods.PROPERTYKEY pkey);
    [PreserveSig] int GetValue(ref NativeMethods.PROPERTYKEY key, out NativeMethods.PROPVARIANT pv);
    [PreserveSig] int SetValue(ref NativeMethods.PROPERTYKEY key, ref NativeMethods.PROPVARIANT propvar);
    [PreserveSig] int Commit();
}

[ComImport, Guid("6F79D558-3E96-4549-A1D1-7D75D2288814"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyDescription
{
    [PreserveSig] int GetPropertyKey(out NativeMethods.PROPERTYKEY pkey);
    [PreserveSig] int GetCanonicalName(out IntPtr ppszName);
    [PreserveSig] int GetPropertyType(out ushort pvartype);
    [PreserveSig] int GetDisplayName(out IntPtr ppszName);
    [PreserveSig] int GetEditInvitation(out IntPtr ppszInvite);
    [PreserveSig] int GetTypeFlags(int mask, out int ppdtFlags);
    [PreserveSig] int GetViewFlags(out int ppdvFlags);
    [PreserveSig] int GetDefaultColumnWidth(out uint pcxChars);
    [PreserveSig] int GetDisplayType(out int pdisplaytype);
    [PreserveSig] int GetColumnState(out int pcsFlags);
    [PreserveSig] int GetGroupingRange(out int pgr);
    [PreserveSig] int GetRelativeDescriptionType(out int prdt);
    [PreserveSig] int GetRelativeDescription(ref NativeMethods.PROPVARIANT propvar1, ref NativeMethods.PROPVARIANT propvar2, out IntPtr ppszDesc1, out IntPtr ppszDesc2);
    [PreserveSig] int GetSortDescription(out int psd);
    [PreserveSig] int GetSortDescriptionLabel(int fDescending, out IntPtr ppszDescription);
    [PreserveSig] int GetAggregationType(out int paggtype);
    [PreserveSig] int GetConditionType(out int pcontype, out int popDefault);
    [PreserveSig] int GetEnumTypeList(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CoerceToCanonicalValue(ref NativeMethods.PROPVARIANT ppropvar);
    [PreserveSig] int FormatForDisplay(ref NativeMethods.PROPVARIANT propvar, int pdfFlags, out IntPtr ppszDisplay);
    [PreserveSig] int IsValueCanonical(ref NativeMethods.PROPVARIANT propvar);
}
