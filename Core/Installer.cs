using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace pxdCommander.Core;

/// <summary>
/// pxd Commander installs itself: the single-file build named pxdcomm-setup.exe copies itself to
/// %LOCALAPPDATA%\Programs\pxd Commander, adds Start menu / desktop shortcuts, an "Open in pxd Commander" folder
/// menu entry and an Apps &amp; features entry. Per-user, so no administrator rights are needed, and self-contained,
/// so the PC needs no .NET.
/// </summary>
public static class Installer
{
    public const string AppName = "pxd Commander";
    public const string AppUserModelId = "pxd.Commander";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\pxdCommander";
    private const string ExeName = "pxdCommander.exe";
    private static readonly string[] MenuKeys =
    [
        @"Software\Classes\Directory\shell\pxdCommander",
        @"Software\Classes\Directory\Background\shell\pxdCommander",
        @"Software\Classes\Drive\shell\pxdCommander",
    ];

    /// <summary>Registry root for everything the installer writes. The self-test points it at a private hive file.</summary>
    internal static RegistryKey Root { get; set; } = Registry.CurrentUser;

    /// <summary>Set by the self-test: shortcut folders and the registry are throwaway copies, nothing is announced to the shell.</summary>
    internal static bool Testing { get; set; }

    internal static string? TestStartMenuFolder { get; set; }
    internal static string? TestDesktopFolder { get; set; }

    public static string StartMenuFolder => TestStartMenuFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string DesktopFolder => TestDesktopFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// The folder an install goes to when none is given: where it's installed now, else where the version from before
    /// the rename was installed (its default folder name becomes the new one), else the default.
    /// </summary>
    public static string SuggestedFolder => InstalledFolder ?? Migration.MapFolder(Migration.FormerInstalledFolder) ?? DefaultFolder;

    public static string Version =>
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    /// <summary>Where it's installed now, if it is (from the Apps &amp; features entry).</summary>
    public static string? InstalledFolder
    {
        get
        {
            using var key = Root.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string is { Length: > 0 } f && File.Exists(Path.Combine(f, ExeName)) ? f : null;
        }
    }

    public static string? InstalledVersion
    {
        get
        {
            using var key = Root.OpenSubKey(UninstallKey);
            return key?.GetValue("DisplayVersion") as string;
        }
    }

    /// <summary>A copy named like a setup program (pxdcomm-setup.exe) runs the installer instead of the app.</summary>
    public static bool LooksLikeSetup() =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    private static string StartMenuShortcut =>
        Path.Combine(StartMenuFolder, AppName + ".lnk");

    private static string DesktopShortcut =>
        Path.Combine(DesktopFolder, AppName + ".lnk");

    public static async Task<string> InstallAsync(string folder, bool desktop, bool folderMenu, Action<string> status)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the setup program.");
        var exe = Path.Combine(folder, ExeName);

        status("Closing pxd Commander if it's running…");
        await CloseRunningAsync(exe);
        if (Migration.FormerInstalledFolder is { } former) await CloseRunningAsync(Path.Combine(former, Migration.FormerExeName));

        status("Copying files…");
        Directory.CreateDirectory(folder);
        // Copy beside, then swap: a half-written exe never replaces a working one.
        var temp = exe + ".new";
        await Task.Run(() => File.Copy(source, temp, overwrite: true));
        File.Move(temp, exe, overwrite: true);

        status("Adding shortcuts…");
        CreateShortcut(StartMenuShortcut, exe, folder);
        if (desktop) CreateShortcut(DesktopShortcut, exe, folder);
        else TryDelete(DesktopShortcut);

        status("Registering…");
        using (var key = Root.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "pxd");
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", folder);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }

        if (folderMenu)
        {
            foreach (var path in MenuKeys)
            {
                using var key = Root.CreateSubKey(path);
                key.SetValue("MUIVerb", "Open in pxd Commander");
                key.SetValue("Icon", exe + ",0");
                using var cmd = key.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" \"%V\"");
            }
        }
        else RemoveFolderMenu();

        Migration.RemoveFormerInstall(folder);
        if (!Testing) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: refresh icons and menus
        status("Done");
        return exe;
    }

    /// <summary>Removes shortcuts, menu entries and the program; settings, connections and passwords only if asked.</summary>
    public static void Uninstall(bool removeSettings)
    {
        var folder = InstalledFolder ?? Path.GetDirectoryName(Environment.ProcessPath) ?? DefaultFolder;
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        RemoveFolderMenu();
        Root.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Migration.RemoveFormerInstall(folder); // leftovers of the version from before the rename, if any

        if (removeSettings)
        {
            Remote.Connections.Load();
            foreach (var c in Remote.Connections.All.ToList()) Remote.Connections.SetSecret(c, null);
            try { Directory.Delete(AppPaths.Data, recursive: true); } catch { }
        }

        // The running exe can't delete itself: a short-lived cmd removes the folder once we've exited.
        if (Directory.Exists(folder) && File.Exists(Path.Combine(folder, ExeName)))
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{folder}\"",
                WorkingDirectory = Path.GetTempPath(),
            };
            Process.Start(psi);
        }
        if (!Testing) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Includes the shortcut the version from before the rename made, so an update keeps the choice.</summary>
    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut) || Migration.FormerDesktopShortcut;

    public static bool HasFolderMenu
    {
        get
        {
            using var key = Root.OpenSubKey(MenuKeys[0]);
            return key is not null || Migration.FormerFolderMenu;
        }
    }

    /// <summary>For updates: waits (up to 30 s) for the copy being replaced to close.</summary>
    public static void WaitForExit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(30000);
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Opens the freshly installed app (after a quiet update).</summary>
    public static void Relaunch(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch
        {
        }
    }

    private static void RemoveFolderMenu()
    {
        foreach (var path in MenuKeys) Root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    private static async Task CloseRunningAsync(string exe)
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            try
            {
                if (p.Id == me || !string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                p.CloseMainWindow();
                await Task.Run(() => p.WaitForExit(8000));
                if (!p.HasExited) p.Kill();
            }
            catch
            {
                // Not ours to close (another user, access denied).
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ---- Shortcuts (IShellLink), stamped with our AppUserModelID so taskbar pins group with the running window ----

    private static void CreateShortcut(string path, string target, string workingFolder)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(workingFolder);
        link.SetIconLocation(target, 0);
        link.SetDescription("Dual-pane file manager with FTP, SFTP and a built-in terminal");
        var store = (IPropertyStore)link;
        var aumidKey = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        var value = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(AppUserModelId) }; // VT_LPWSTR
        try
        {
            store.SetValue(ref aumidKey, ref value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.p);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
        Marshal.ReleaseComObject(link);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }
}
