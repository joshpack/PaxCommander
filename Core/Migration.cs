using System.Diagnostics;
using Microsoft.Win32;

namespace pxdCommander.Core;

/// <summary>
/// One-time moves from what versions before the pxd rename left behind: the settings folder, saved passwords, and
/// everything the old installer registered (Apps entry, folder right-click entries, Start menu / desktop shortcuts,
/// the old exe). Every step is safe to repeat (it does nothing once the old thing is gone) and never throws.
/// This is the only place that names the former prefix.
/// </summary>
public static class Migration
{
    /// <summary>The prefix the app had before the pxd rename; used only to find what older versions left behind.</summary>
    public const string FormerPrefix = "Pax";

    public const string AppId = "pxdCommander";

    /// <summary>"pxdCommander" → its former id.</summary>
    public static string FormerName(string pxdName) =>
        pxdName.StartsWith("pxd", StringComparison.Ordinal) ? FormerPrefix + pxdName[3..] : pxdName;

    /// <summary>The former display name (folder and shortcut names of the old installer).</summary>
    public static string FormerDisplayName => FormerPrefix.ToUpperInvariant() + Installer.AppName[3..];

    public static string FormerExeName => FormerName(AppId) + ".exe";

    /// <summary>Saved passwords in Windows Credential Manager were stored under this prefix.</summary>
    public static string FormerCredentialPrefix => FormerName(AppId) + ":";

    public static string FormerUninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + FormerName(AppId);

    public static string[] FormerMenuKeys =>
    [
        @"Software\Classes\Directory\shell\" + FormerName(AppId),
        @"Software\Classes\Directory\Background\shell\" + FormerName(AppId),
        @"Software\Classes\Drive\shell\" + FormerName(AppId),
    ];

    /// <summary>Where the former updater left downloaded installers.</summary>
    public static string FormerDownloadFolder => Path.Combine(Path.GetTempPath(), FormerName(AppId) + "-update");

    // ---- Settings folder ----------------------------------------------------------------------------------------------

    /// <summary>%APPDATA%\pxdCommander, moved over from the former folder the first time. Runs before settings are read.</summary>
    public static string DataFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, AppId);
        Folder(Path.Combine(appData, FormerName(AppId)), dir);
        return dir;
    }

    /// <summary>Moves <paramref name="oldDir"/> to <paramref name="newDir"/> when only the old one exists. True if it moved.</summary>
    public static bool Folder(string oldDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            try
            {
                Directory.Move(oldDir, newDir);
            }
            catch (IOException)
            {
                // Another drive, or a file in use: copy what can be copied and leave the old folder.
                CopyTree(oldDir, newDir);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false); } catch { /* skip */ }
        foreach (var d in Directory.GetDirectories(from))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---- The former installation ------------------------------------------------------------------------------------------

    /// <summary>Where the former installer put the app, if it's still registered and there.</summary>
    public static string? FormerInstalledFolder
    {
        get
        {
            try
            {
                using var key = Installer.Root.OpenSubKey(FormerUninstallKey);
                return key?.GetValue("InstallLocation") as string is { Length: > 0 } f && File.Exists(Path.Combine(f, FormerExeName)) ? f : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static string? FormerInstalledVersion
    {
        get
        {
            try
            {
                using var key = Installer.Root.OpenSubKey(FormerUninstallKey);
                return key?.GetValue("DisplayVersion") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool FormerDesktopShortcut => File.Exists(Path.Combine(Installer.DesktopFolder, FormerDisplayName + ".lnk"));

    public static bool FormerFolderMenu
    {
        get
        {
            try
            {
                using var key = Installer.Root.OpenSubKey(FormerMenuKeys[0]);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The folder to install into when <paramref name="folder"/> is the former default folder name: same place, new
    /// name (…\Programs\(former name) → …\Programs\pxd Commander). Any other folder stays as it is.
    /// </summary>
    public static string? MapFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return folder;
        var trimmed = folder.TrimEnd('\\', '/');
        return string.Equals(Path.GetFileName(trimmed), FormerDisplayName, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(trimmed) is { } parent
            ? Path.Combine(parent, Installer.AppName)
            : folder;
    }

    /// <summary>
    /// After a successful install into <paramref name="newFolder"/>: removes what the former installer registered (its
    /// Apps entry, folder right-click entries, Start menu and desktop shortcuts) and its exe (plus the folder, when that's
    /// left empty). Only runs when the former Apps entry exists, so a hand-made shortcut of a dev copy is left alone.
    /// True if there was a former installation.
    /// </summary>
    public static bool RemoveFormerInstall(string newFolder)
    {
        try
        {
            string? oldFolder;
            using (var key = Installer.Root.OpenSubKey(FormerUninstallKey))
            {
                if (key is null) return false;
                oldFolder = key.GetValue("InstallLocation") as string;
            }

            TryDelete(Path.Combine(Installer.StartMenuFolder, FormerDisplayName + ".lnk"));
            TryDelete(Path.Combine(Installer.DesktopFolder, FormerDisplayName + ".lnk"));
            foreach (var path in FormerMenuKeys)
                try { Installer.Root.DeleteSubKeyTree(path, throwOnMissingSubKey: false); } catch { }
            try { Installer.Root.DeleteSubKeyTree(FormerUninstallKey, throwOnMissingSubKey: false); } catch { }

            if (oldFolder is { Length: > 0 } && Directory.Exists(oldFolder))
            {
                var exe = Path.Combine(oldFolder, FormerExeName);
                var removed = TryDelete(exe) & TryDelete(exe + ".old");
                var sameFolder = string.Equals(Path.GetFullPath(oldFolder).TrimEnd('\\'), Path.GetFullPath(newFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (!sameFolder)
                {
                    if (removed) TryDeleteEmptyFolder(oldFolder);
                    else if (!Installer.Testing) DeleteLater(exe, oldFolder);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The old exe is still running (it waits to be replaced): remove it, and its empty folder, a moment later.</summary>
    private static void DeleteLater(string exe, string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // rmdir without /s only removes the folder when nothing else is in it.
                Arguments = $"/c ping 127.0.0.1 -n 6 > nul & del /f /q \"{exe}\" & rmdir \"{folder}\"",
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch
        {
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch
        {
        }
    }

    /// <summary>Removes installers the former updater downloaded.</summary>
    public static void RemoveFormerDownloads()
    {
        try
        {
            if (Directory.Exists(FormerDownloadFolder)) Directory.Delete(FormerDownloadFolder, recursive: true);
        }
        catch
        {
        }
    }
}
