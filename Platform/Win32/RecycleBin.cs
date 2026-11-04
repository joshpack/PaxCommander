using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace PaxCommander.Platform.Win32;

[SupportedOSPlatform("windows")]
public static class RecycleBin
{
    private static readonly Lazy<string?> s_userSid = new(() =>
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>True when deleting under <paramref name="path"/> can go to the Recycle Bin (local fixed volume, not UNC/network/removable).</summary>
    public static bool IsSupported(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                return false;
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return false; // UNC shares, and \\?\ paths SHFileOperation can't take anyway
            string? root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends items to the Recycle Bin without the shell's own confirmation UI. Throws if anything was not recycled.
    /// Blocking; safe from any thread (MTA callers are marshalled to the shell's STA operations thread). The shell may
    /// still ask before permanently deleting an item that can't be recycled (e.g. too big for the bin); "No" leaves the
    /// item in place and this throws.
    /// </summary>
    public static void Recycle(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            return;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full = NormalizePath(path)
                ?? throw new IOException($"'{path}' is not a full local path and can't be sent to the Recycle Bin.");
            if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"A drive root ('{full}') can't be sent to the Recycle Bin.");
            unique.Add(full);
        }
        // An item inside a folder that is also on the list goes with that folder; naming it again would make
        // SHFileOperation report it missing once its folder has moved.
        List<string> items = unique.Where(p => !unique.Any(other =>
            p.Length > other.Length && p.StartsWith(other, StringComparison.OrdinalIgnoreCase) && p[other.Length] == '\\')).ToList();
        StaInvoker.Invoke(() =>
        {
            RecycleCore(items);
            return true;
        });
    }

    private static void RecycleCore(List<string> items)
    {
        // pFrom is a list of NUL-terminated paths ended by an extra NUL; the marshaller doesn't do embedded NULs, so build it by hand.
        IntPtr from = Marshal.StringToHGlobalUni(string.Join('\0', items) + "\0\0");
        int result;
        bool aborted;
        try
        {
            // NO_CONNECTED_ELEMENTS: recycle exactly what was asked (not "page_files" next to "page.htm"), so the app's
            // bookkeeping and undo stay accurate. WANTNUKEWARNING: never permanently delete silently.
            const ushort flags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_NOERRORUI
                                 | NativeMethods.FOF_SILENT | NativeMethods.FOF_WANTNUKEWARNING | NativeMethods.FOF_NO_CONNECTED_ELEMENTS;
            if (IntPtr.Size == 8)
            {
                var op = new NativeMethods.SHFILEOPSTRUCTW64 { wFunc = NativeMethods.FO_DELETE, pFrom = from, fFlags = flags };
                result = NativeMethods.SHFileOperationW(ref op);
                aborted = op.fAnyOperationsAborted != 0;
            }
            else
            {
                var op = new NativeMethods.SHFILEOPSTRUCTW32 { wFunc = NativeMethods.FO_DELETE, pFrom = from, fFlags = flags };
                result = NativeMethods.SHFileOperationW32(ref op);
                aborted = op.fAnyOperationsAborted != 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }

        List<string> left = items.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (result == 0 && !aborted && left.Count == 0)
            return;

        var message = new StringBuilder();
        message.Append(left.Count > 0
            ? $"{left.Count} of {items.Count} item(s) could not be moved to the Recycle Bin"
            : "The Recycle Bin operation did not complete");
        if (result != 0)
            message.Append(": ").Append(DescribeShellError(result));
        else if (aborted)
            message.Append(": the operation was cancelled");
        if (left.Count > 0)
            message.Append(". Not recycled: ").Append(string.Join(", ", left.Take(5).Select(Path.GetFileName)))
                   .Append(left.Count > 5 ? ", …" : "");
        throw new IOException(message.Append('.').ToString());
    }

    /// <summary>SHFileOperation still returns some pre-Win32 DE_* codes; translate those, defer to Win32 for the rest.</summary>
    private static string DescribeShellError(int code) => code switch
    {
        0x71 => "source and destination are the same file",
        0x74 or 0x10074 => "the item is a drive root or system folder",
        0x75 or NativeMethods.ERROR_CANCELLED => "the operation was cancelled",
        0x78 => "access denied",
        0x79 or 0x81 => "the path is too long",
        0x7C => "the item doesn't exist or the path is invalid",
        0x7E or 0x80 => "a file and a folder have the same name",
        0x85 => "the file is too large",
        0x86 or 0x87 or 0x88 => "the item is on read-only media",
        0x402 => "an unknown error occurred",
        _ => $"{new Win32Exception(code).Message} (0x{code:X})",
    };

    /// <summary>Recycle Bin entries whose original path is one of <paramref name="originalPaths"/>, deleted at or after <paramref name="deletedSinceUtc"/>.</summary>
    /// <remarks>Newest first, so when one path was recycled several times the latest copy comes first (and is what <see cref="Restore"/> puts back).</remarks>
    public static IReadOnlyList<RecycledEntry> Find(IReadOnlyCollection<string> originalPaths, DateTime deletedSinceUtc)
    {
        try
        {
            if (originalPaths == null || originalPaths.Count == 0 || s_userSid.Value is not { } sid)
                return [];
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in originalPaths)
            {
                if (NormalizePath(path) is { } full)
                    wanted.Add(full);
            }

            DateTime since = deletedSinceUtc.Kind == DateTimeKind.Local ? deletedSinceUtc.ToUniversalTime() : deletedSinceUtc;
            // Clock granularity and the time the caller took its timestamp: allow a little slack.
            since = since > DateTime.MinValue.AddSeconds(2) ? since.AddSeconds(-2) : DateTime.MinValue;
            // The $I file is written at deletion time, so older ones can be skipped without opening them.
            DateTime writtenAfter = since > DateTime.MinValue.AddHours(1) ? since.AddHours(-1) : DateTime.MinValue;

            var found = new List<RecycledEntry>();
            foreach (string root in wanted.Select(Path.GetPathRoot).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bin = new DirectoryInfo(Path.Combine(root, "$Recycle.Bin", sid));
                if (!bin.Exists)
                    continue;
                foreach (FileInfo info in bin.EnumerateFiles("$I*"))
                {
                    if (info.LastWriteTimeUtc < writtenAfter || info.Length > 65536)
                        continue;
                    if (!TryReadInfoFile(info.FullName, out string original, out long size, out DateTime deletedUtc))
                        continue;
                    if (deletedUtc < since || NormalizePath(original) is not { } normalized || !wanted.Contains(normalized))
                        continue;
                    string dataFile = Path.Combine(bin.FullName, "$R" + info.Name[2..]);
                    found.Add(new RecycledEntry(normalized, deletedUtc, size, info.FullName, dataFile));
                }
            }
            found.Sort((a, b) => b.DeletedUtc.CompareTo(a.DeletedUtc));
            return found;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Puts entries back where they came from (skipping any whose original path is occupied); returns the restored paths.</summary>
    public static IReadOnlyList<string> Restore(IReadOnlyList<RecycledEntry> entries)
    {
        var restored = new List<string>();
        if (entries == null || entries.Count == 0)
            return restored;
        var bins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecycledEntry entry in entries)
        {
            try
            {
                if (!LooksLikeRecycleBinPair(entry))
                    continue;
                string target = entry.OriginalPath;
                if (File.Exists(target) || Directory.Exists(target))
                    continue;
                bool isDirectory = Directory.Exists(entry.DataFile);
                if (!isDirectory && !File.Exists(entry.DataFile))
                    continue;

                string? parent = Path.GetDirectoryName(target);
                if (parent != null)
                    Directory.CreateDirectory(parent);
                // The bin folder lives on the item's own volume, so this is a rename, not a copy.
                if (isDirectory)
                    Directory.Move(entry.DataFile, target);
                else
                    File.Move(entry.DataFile, target);
                try
                {
                    File.Delete(entry.InfoFile);
                }
                catch (Exception)
                {
                    // An orphaned $I file only leaves a dead entry in the bin's listing; the restore itself succeeded.
                }
                restored.Add(target);
                if (Path.GetDirectoryName(entry.InfoFile) is { } bin)
                    bins.Add(bin);
                if (parent != null)
                    parents.Add(parent);
            }
            catch (Exception)
            {
                // Locked, access denied or the bin changed underneath us: skip this one, carry on with the rest.
            }
        }
        if (restored.Count > 0)
            NotifyRestored(bins, parents);
        return restored;
    }

    /// <summary>Opens the Recycle Bin in Explorer.</summary>
    public static void Open()
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using Process? process = Process.Start(new ProcessStartInfo(explorer, "shell:RecycleBinFolder") { UseShellExecute = false });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Full path without a \\?\ prefix or trailing separator (roots keep theirs), or null when it isn't a full path.
    /// The shell records paths longer than MAX_PATH in $I files with the \\?\ prefix.
    /// </summary>
    private static string? NormalizePath(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return null;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                path = path[4..];
            if (!Path.IsPathFullyQualified(path))
                return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a $I file: int64 version, int64 size, int64 FILETIME of deletion, then the original path — a fixed
    /// 260-char buffer in version 1 (Vista–8.1), or an int32 length (chars incl. NUL) and the path in version 2 (Win10+).
    /// </summary>
    internal static bool TryReadInfoFile(string file, out string originalPath, out long size, out DateTime deletedUtc)
    {
        originalPath = "";
        size = 0;
        deletedUtc = DateTime.MinValue;
        try
        {
            byte[] data = File.ReadAllBytes(file);
            if (data.Length < 24)
                return false;
            long version = BitConverter.ToInt64(data, 0);
            size = BitConverter.ToInt64(data, 8);
            long fileTime = BitConverter.ToInt64(data, 16);
            deletedUtc = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;

            int start, byteCount;
            if (version == 1)
            {
                start = 24;
                byteCount = Math.Min(520, data.Length - start);
            }
            else if (version == 2)
            {
                if (data.Length < 28)
                    return false;
                start = 28;
                int chars = BitConverter.ToInt32(data, 24);
                byteCount = Math.Min(Math.Max(chars, 0) * 2, data.Length - start);
            }
            else
            {
                return false;
            }
            string text = Encoding.Unicode.GetString(data, start, byteCount & ~1);
            int nul = text.IndexOf('\0');
            originalPath = nul >= 0 ? text[..nul] : text;
            return originalPath.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Restore moves files around, so only accept a $I/$R pair from one Recycle Bin folder.</summary>
    private static bool LooksLikeRecycleBinPair(RecycledEntry entry)
    {
        if (NormalizePath(entry.InfoFile) is not { } info || NormalizePath(entry.DataFile) is not { } data
            || NormalizePath(entry.OriginalPath) is null)
            return false;
        string infoName = Path.GetFileName(info), dataName = Path.GetFileName(data);
        return infoName.StartsWith("$I", StringComparison.OrdinalIgnoreCase)
               && dataName.StartsWith("$R", StringComparison.OrdinalIgnoreCase)
               && string.Equals(infoName[2..], dataName[2..], StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetDirectoryName(info), Path.GetDirectoryName(data), StringComparison.OrdinalIgnoreCase)
               && info.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tells Explorer windows (and the bin's own view and desktop icon) that things moved.</summary>
    private static void NotifyRestored(IEnumerable<string> bins, IEnumerable<string> parents)
    {
        try
        {
            foreach (string folder in bins.Concat(parents))
                NotifyUpdateDir(folder);

            if (NativeMethods.SHGetKnownFolderIDList(NativeMethods.FOLDERID_RecycleBinFolder, 0, IntPtr.Zero, out IntPtr pidl) >= 0)
            {
                try
                {
                    NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_IDLIST, pidl, IntPtr.Zero);
                }
                finally
                {
                    ShellPidl.Free(pidl);
                }
            }
        }
        catch (Exception)
        {
            // Notifications are cosmetic; the restore already happened.
        }
    }

    private static void NotifyUpdateDir(string folder)
    {
        IntPtr text = Marshal.StringToHGlobalUni(folder);
        try
        {
            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW, text, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }
}
