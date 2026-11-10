using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace pxdCommander.Ops;

/// <summary>Win32 calls used by the engine. Only reached behind OperatingSystem.IsWindows() checks.</summary>
internal static class OpsNative
{
    public const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
    public const uint COPY_FILE_NO_BUFFERING = 0x00001000;
    public const uint PROGRESS_CONTINUE = 0;
    public const uint PROGRESS_CANCEL = 1;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_SAME_DEVICE = 17;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_FILENAME_EXCED_RANGE = 206;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_REQUEST_ABORTED = 1235;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CopyFileEx(
        string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine, IntPtr data, IntPtr cancel, uint copyFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        // FILETIMEs as DWORD pairs: a long here would add alignment padding and shift every later field.
        public uint ftCreationLow, ftCreationHigh, ftLastAccessLow, ftLastAccessHigh, ftLastWriteLow, ftLastWriteHigh;
        public uint nFileSizeHigh, nFileSizeLow;
        public uint dwReserved0; // the reparse tag when FILE_ATTRIBUTE_REPARSE_POINT is set
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileEx(string fileName, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);

    /// <summary>The reparse tag of a reparse point (0 if it has none), or null when it can't be read.</summary>
    public static uint? GetReparseTag(string path)
    {
        const int FindExInfoBasic = 1;
        var handle = FindFirstFileEx(OpsPath.ToNative(Path.TrimEndingDirectorySeparator(path)), FindExInfoBasic, out var data, 0, IntPtr.Zero, 0);
        if (handle == new IntPtr(-1)) return null;
        FindClose(handle);
        return (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : 0;
    }

    /// <summary>Path with every junction/symlink along it resolved, or null if the item can't be opened.</summary>
    public static string? GetFinalPath(string path)
    {
        const uint ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000; // backup semantics = allowed to open folders
        using var handle = CreateFile(OpsPath.ToNative(path), 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[512];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length > buffer.Length) return null;
        var result = new string(buffer, 0, (int)length);
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    /// <summary>The .NET exception a Win32 error would have produced, with the system's own message.</summary>
    public static Exception ToException(int error, string path)
    {
        var message = new Win32Exception(error).Message;
        return error switch
        {
            ERROR_FILE_NOT_FOUND => new FileNotFoundException(message, path),
            ERROR_PATH_NOT_FOUND => new DirectoryNotFoundException(message),
            ERROR_ACCESS_DENIED => new UnauthorizedAccessException(message),
            ERROR_FILENAME_EXCED_RANGE => new PathTooLongException(message),
            _ => new IOException(message, unchecked((int)0x80070000 | error)),
        };
    }
}
