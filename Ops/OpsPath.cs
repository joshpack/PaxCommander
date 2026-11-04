namespace PaxCommander.Ops;

/// <summary>What an entry is, for the engine's purposes. File symlinks count as files (copying them copies the target's content).</summary>
internal enum OpsItemKind { File, Directory, Link }

internal static class OpsPath
{
    // NTFS/APFS are case-insensitive by default, so "C:\Data" and "c:\data" are the same folder there.
    public static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public static readonly StringComparer Comparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static readonly StringComparison Comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Attributes that can be written back with SetAttributes (compression/encryption etc. can't).</summary>
    public const FileAttributes SettableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    /// <summary>One shared listing setup: hidden and system entries included (the default skips them), errors surface.</summary>
    public static readonly EnumerationOptions Listing = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>
    /// Full path without a trailing separator (roots keep theirs). Relative paths are left alone on purpose so validation
    /// can reject them — resolving them against the process's current directory could delete the wrong thing.
    /// </summary>
    public static string Normalize(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return path;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public static bool PathEquals(string? a, string? b) => string.Equals(a, b, Comparison);

    public static bool IsRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && PathEquals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies anywhere below it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        if (PathEquals(path, folder)) return true;
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The path with links resolved (Windows), so a destination reached through a junction is recognised.</summary>
    public static string RealPath(string path) =>
        OperatingSystem.IsWindows() ? OpsNative.GetFinalPath(path) ?? path : path;

    /// <summary>Display name of a folder for titles: its name, or "C:" for a drive root.</summary>
    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (!string.IsNullOrEmpty(name)) return name;
        var root = Path.GetPathRoot(path) ?? path;
        var trimmed = root.TrimEnd('\\', '/');
        return trimmed.Length > 0 ? trimmed : root;
    }

    /// <summary>The item's attributes without following links, or null when nothing is there.</summary>
    public static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Like <see cref="GetAttributes"/>, but any failure reads as "not there" (scanning only).</summary>
    public static FileAttributes? GetAttributesQuiet(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static bool Exists(string path) => GetAttributes(path) is not null;

    /// <summary>
    /// True for junctions, directory symlinks and other name-surrogate reparse points: entries that stand for another
    /// path. They are never recursed into — a junction pointing back up the tree would loop forever, and following one
    /// during a delete would wipe the folder it points at. Other directory reparse points (OneDrive placeholders,
    /// dedup, projected file systems) are ordinary folders.
    /// </summary>
    public static bool IsLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        if (!OperatingSystem.IsWindows()) return true;
        const uint NameSurrogate = 0x20000000;
        // Unknown tag (lookup failed) → treat as a link: skipping a real folder is recoverable, following a link isn't.
        return OpsNative.GetReparseTag(path) is not uint tag || (tag & NameSurrogate) != 0;
    }

    public static OpsItemKind Classify(string path, FileAttributes attributes) =>
        (attributes & FileAttributes.Directory) == 0 ? OpsItemKind.File
        : IsLink(path, attributes) ? OpsItemKind.Link
        : OpsItemKind.Directory;

    public static OpsItemKind Classify(FileSystemInfo info) => Classify(info.FullName, info.Attributes);

    /// <summary>FileInfo or DirectoryInfo for an existing path; throws FileNotFoundException when it's gone.</summary>
    public static FileSystemInfo Stat(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        return info;
    }

    public static (string Stem, string Extension) SplitName(string name, bool isDirectory)
    {
        if (isDirectory) return (name, "");
        var dot = name.LastIndexOf('.');
        // No dot, or a dot-file like ".gitignore": the whole name is the stem.
        return dot <= 0 ? (name, "") : (name[..dot], name[dot..]);
    }

    /// <summary>Explorer's naming for a copy dropped into its own folder: "name - Copy.ext", then "name - Copy (2).ext"…</summary>
    public static string CopyName(string folder, string name, bool isDirectory)
    {
        var (stem, ext) = SplitName(name, isDirectory);
        var candidate = Path.Combine(folder, $"{stem} - Copy{ext}");
        for (var n = 2; Exists(candidate); n++) candidate = Path.Combine(folder, $"{stem} - Copy ({n}){ext}");
        return candidate;
    }

    /// <summary>Keep-both naming: "name (2).ext", "name (3).ext"… — the first one that's free.</summary>
    public static string NumberedName(string path, bool isDirectory)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var (stem, ext) = SplitName(Path.GetFileName(path), isDirectory);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!Exists(candidate)) return candidate;
        }
    }

    /// <summary>Clears read-only (and hidden/system, which make CreateFile refuse to overwrite) before replacing a file.</summary>
    public static void MakeWritable(string path)
    {
        const FileAttributes Blocking = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        if (GetAttributes(path) is not { } a || (a & Blocking) == 0) return;
        var rest = a & ~Blocking & SettableAttributes;
        File.SetAttributes(path, rest == 0 ? FileAttributes.Normal : rest);
    }

    /// <summary>Deletes one file, clearing read-only first (DeleteFile refuses read-only files).</summary>
    public static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException) when (GetAttributes(path) is { } a && (a & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    /// <summary>Removes an empty folder; a folder that is already gone counts as removed.</summary>
    public static void DeleteEmptyFolder(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            Directory.Delete(path, recursive: false);
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }

    /// <summary>Removes a junction or symlink itself — never anything it points at.</summary>
    public static void DeleteLink(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // RemoveDirectory on a junction/directory symlink deletes the reparse point only.
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            if ((a & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
            else File.Delete(path);
        }
        else
        {
            File.Delete(path); // unlink(2) removes the symlink, not its target
        }
    }

    /// <summary>Removes a partially written file after a failed or cancelled write. Never throws.</summary>
    public static void DeletePartial(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"\\?\" form for paths handed straight to Win32, which otherwise stop at MAX_PATH.</summary>
    public static string ToNative(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// <summary>Short user-facing reason for a failure.</summary>
    public static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access is denied.",
        PathTooLongException => "The path is too long.",
        FileNotFoundException or DirectoryNotFoundException => "It no longer exists.",
        _ => ex.Message,
    };
}
