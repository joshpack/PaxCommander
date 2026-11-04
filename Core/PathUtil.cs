using System.Text.RegularExpressions;
using PaxCommander.Core.Remote;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Core;

public sealed record Crumb(string Name, string Path);

public sealed record KnownPlace(string Name, string Path, string Glyph);

public static class PathUtil
{
    /// <summary>The virtual "This PC" location listing every drive.</summary>
    public const string ComputerPath = "::computer";

    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsComputer(string? path) => path == ComputerPath;

    /// <summary>The virtual "Network" location listing the computers on the local network.</summary>
    public const string NetworkPath = "::network";

    public static bool IsNetworkRoot(string? path) => path == NetworkPath;

    /// <summary>\\SERVER on its own (no share): lists that computer's shared folders.</summary>
    public static bool IsServer(string? path) =>
        path is { Length: > 2 } && path.StartsWith(@"\\", StringComparison.Ordinal) && path.TrimEnd('\\').IndexOf('\\', 2) < 0;

    /// <summary>Places that are lists rather than folders: This PC, Network and a computer's shares.</summary>
    public static bool IsVirtual(string? path) => IsComputer(path) || IsNetworkRoot(path) || IsServer(path);

    /// <summary>A folder on an SFTP/FTP server (see <see cref="RemotePath"/>).</summary>
    public static bool IsRemote(string? path) => RemotePath.IsRemote(path);

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Trim(a), Trim(b), IsRemote(a) ? StringComparison.Ordinal : Comparison);

    /// <summary>Removes a trailing separator except on roots ("C:\", "/").</summary>
    public static string Trim(string path)
    {
        if (path.Length <= 1 || IsComputer(path) || IsNetworkRoot(path)) return path;
        if (IsServer(path)) return path.TrimEnd('\\');
        if (IsRemote(path)) return RemotePath.Trim(path);
        var root = SafeRoot(path);
        if (root is not null && path.Length <= root.Length) return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? SafeRoot(string path)
    {
        try { return Path.GetPathRoot(path); }
        catch { return null; }
    }

    public static bool IsRoot(string path)
    {
        if (IsVirtual(path)) return false;
        if (IsRemote(path)) return RemotePath.IsRoot(path);
        var root = SafeRoot(path);
        return root is not null && string.Equals(Trim(root), Trim(path), Comparison);
    }

    /// <summary>The folder one level up; roots go to "This PC"; "This PC" has none.</summary>
    public static string? Parent(string path)
    {
        if (IsComputer(path) || IsNetworkRoot(path)) return null;
        if (IsServer(path)) return NetworkPath;
        // \\server\share is a root; one up is the computer itself, listing its shares.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && IsRoot(path)) return @"\\" + Trim(path).TrimStart('\\').Split('\\')[0];
        if (IsRemote(path)) return RemotePath.Parent(path);
        if (IsRoot(path)) return OperatingSystem.IsWindows() ? ComputerPath : null;
        try { return Path.GetDirectoryName(Trim(path)) ?? ComputerPath; }
        catch { return null; }
    }

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or lies inside it.</summary>
    public static bool IsSameOrInside(string parent, string child)
    {
        var p = Trim(parent);
        var c = Trim(child);
        var remote = IsRemote(p);
        if (remote != IsRemote(c)) return false;
        var cmp = remote ? StringComparison.Ordinal : Comparison;
        if (string.Equals(p, c, cmp)) return true;
        var sep = remote ? '/' : Path.DirectorySeparatorChar;
        var withSep = p.EndsWith(sep) ? p : p + sep;
        return c.StartsWith(withSep, cmp);
    }

    /// <summary>Same disk (moves are renames), or the same server for remote paths.</summary>
    public static bool SameVolume(string a, string b)
    {
        if (IsRemote(a) || IsRemote(b))
            return IsRemote(a) && IsRemote(b) && RemotePath.Prefix(a) == RemotePath.Prefix(b);
        return string.Equals(SafeRoot(a), SafeRoot(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>UNC paths and mapped network drives: slow, no Recycle Bin, watchers unreliable.</summary>
    public static bool IsNetwork(string path)
    {
        if (IsComputer(path)) return false;
        if (IsRemote(path)) return true;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = SafeRoot(path);
            return root is not null && root.Length >= 2 && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    public static bool IsLocalFixed(string path)
    {
        if (IsRemote(path)) return false;
        try
        {
            var root = SafeRoot(path);
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Ram;
        }
        catch { return false; }
    }

    /// <summary>Short label for tabs and titles.</summary>
    public static string DisplayName(string path)
    {
        if (IsComputer(path)) return "This PC";
        if (IsNetworkRoot(path)) return "Network";
        if (IsServer(path)) return Trim(path).TrimStart('\\');
        if (IsRemote(path))
            return RemotePath.IsRoot(path) && RemotePath.TryParse(path, out var conn, out _) && conn is not null ? conn.Display : RemotePath.Name(path);
        if (IsRoot(path))
        {
            var root = Trim(path);
            // \\server\share shows as just "share" (its computer is the crumb before it).
            return root.StartsWith(@"\\", StringComparison.Ordinal) ? root.TrimEnd('\\').Split('\\')[^1] : root.TrimEnd('\\', '/');
        }
        var name = Path.GetFileName(Trim(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Breadcrumb trail: This PC › C: › Users › me.</summary>
    public static List<Crumb> Crumbs(string path)
    {
        var list = new List<Crumb>();
        // Network paths: Network › SERVER › share › …
        var network = IsNetworkRoot(path) || path.StartsWith(@"\\", StringComparison.Ordinal);
        if (OperatingSystem.IsWindows() && !IsRemote(path))
            list.Add(network ? new Crumb("Network", NetworkPath) : new Crumb("This PC", ComputerPath));
        if (IsComputer(path) || IsNetworkRoot(path)) return list;

        var chain = new List<string>();
        for (string? p = Trim(path); p is not null && !IsComputer(p) && !IsNetworkRoot(p); p = Parent(p))
        {
            chain.Add(p);
            if (IsRoot(p) && !network) break;
        }
        chain.Reverse();
        foreach (var p in chain)
            list.Add(new Crumb(DisplayName(p), p));
        return list;
    }

    /// <summary>
    /// Turns whatever the user typed into a full path: environment variables, ~, shell:downloads style names, paths
    /// relative to <paramref name="current"/>, quotes, and forward slashes. Returns null for nonsense.
    /// </summary>
    public static string? Resolve(string input, string? current)
    {
        var text = input.Trim().Trim('"');
        if (text.Length == 0) return null;
        if (text.Equals("this pc", StringComparison.OrdinalIgnoreCase) || text.Equals("computer", StringComparison.OrdinalIgnoreCase))
            return ComputerPath;
        if (text.Equals("network", StringComparison.OrdinalIgnoreCase) || text.Equals("shell:networkplacesfolder", StringComparison.OrdinalIgnoreCase))
            return NetworkPath;
        if (IsServer(text.Replace('/', '\\'))) return Trim(text.Replace('/', '\\'));

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return ShellFolder(text[6..]);

        // Our own remote paths pass through; typed server URLs map to a saved connection (or a session-only one).
        if (IsRemote(text) && RemotePath.TryParse(text, out _, out _)) return RemotePath.Trim(text);
        if (Regex.IsMatch(text, "^(sftp|ssh|scp|ftps?)://", RegexOptions.IgnoreCase))
        {
            var conn = Connections.FromUrl(text, out var remotePath);
            if (conn is null) return null;
            if (Connections.Find(conn.Id) is null) Connections.AddTransient(conn);
            return RemotePath.Make(conn, remotePath);
        }
        // Relative paths inside a remote folder.
        if (current is not null && IsRemote(current))
        {
            var parts = new List<string>();
            var typed = text.Replace('\\', '/');
            var abs = typed.StartsWith('/') ? typed : RemotePath.RemotePart(current).TrimEnd('/') + "/" + typed;
            foreach (var seg in abs.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == ".") continue;
                if (seg == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(seg);
            }
            return RemotePath.Prefix(current) + "/" + string.Join('/', parts);
        }

        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.LocalPath;

        text = Environment.ExpandEnvironmentVariables(text);
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith(@"~\", StringComparison.Ordinal))
            text = Path.Combine(Home, text.Length > 2 ? text[2..] : "");

        if (OperatingSystem.IsWindows())
        {
            text = text.Replace('/', '\\');
            if (text.Length == 2 && text[1] == ':') text += "\\";
        }

        try
        {
            if (!Path.IsPathRooted(text))
            {
                if (current is null || IsComputer(current)) return null;
                text = Path.Combine(current, text);
            }
            return Trim(Path.GetFullPath(text));
        }
        catch
        {
            return null;
        }
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Downloads
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var p = ShellInfo.GetKnownFolderPath(ShellInfo.Downloads);
                if (!string.IsNullOrEmpty(p)) return p;
            }
            return Path.Combine(Home, "Downloads");
        }
    }

    private static string? ShellFolder(string name) => name.Trim().ToLowerInvariant() switch
    {
        "downloads" => Downloads,
        "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "documents" or "personal" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "pictures" or "my pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "music" or "my music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "videos" or "my video" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "profile" or "home" => Home,
        "appdata" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "local appdata" or "localappdata" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "startup" => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "programfiles" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "windows" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "temp" => Trim(Path.GetTempPath()),
        "mycomputerfolder" or "this pc" => ComputerPath,
        _ => null,
    };

    /// <summary>The standard user folders that exist on this machine, for the sidebar's defaults.</summary>
    public static List<KnownPlace> KnownPlaces()
    {
        var list = new List<KnownPlace>
        {
            new("Home", Home, "home"),
            new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "desktop"),
            new("Downloads", Downloads, "download"),
            new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "doc"),
            new("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "image"),
            new("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "audio"),
            new("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "video"),
        };
        return list.Where(p => !string.IsNullOrEmpty(p.Path) && Directory.Exists(p.Path)).ToList();
    }

    /// <summary>"New folder", then "New folder (2)", "(3)"… — the first name not taken in <paramref name="folder"/>.</summary>
    public static string UniqueName(string folder, string baseName, string extension = "")
    {
        var candidate = baseName + extension;
        for (var n = 2; Exists(Path.Combine(folder, candidate)); n++)
            candidate = $"{baseName} ({n}){extension}";
        return candidate;
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Null when the name is fine, otherwise why not.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name can't be empty.";
        if (name.IndexOfAny(Invalid) >= 0) return "A name can't contain any of  \\ / : * ? \" < > |";
        if (OperatingSystem.IsWindows())
        {
            if (name.EndsWith('.') || name.EndsWith(' ')) return "Windows doesn't allow a name ending in a dot or space.";
            var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                return $"“{stem}” is reserved by Windows.";
        }
        return null;
    }
}
