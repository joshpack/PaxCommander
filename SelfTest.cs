using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using pxdCommander.Core;

namespace pxdCommander;

/// <summary>
/// "--self-test &lt;report.txt&gt;": checks the rename migration and the installer without a window and without touching
/// the real settings, registry or shortcuts. Settings go to a temp folder, the registry is a private hive file
/// (RegLoadAppKey: not part of the user's registry), and the Start menu / desktop are temp folders.
/// </summary>
internal static class SelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        var fails = 0;
        void Check(bool ok, string what)
        {
            sb.AppendLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok) fails++;
        }

        var root = Path.Combine(Path.GetTempPath(), "pxdCommander-selftest-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("PXDCMD_DATA", Path.Combine(root, "data"));
        RegistryKey? hive = null;
        try
        {
            Directory.CreateDirectory(root);
            Check(AppPaths.Data == Path.Combine(root, "data"), "data folder override (PXDCMD_DATA) is used, real folder untouched");

            // ---- settings folder ----
            var oldData = Path.Combine(root, "appdata", Migration.FormerName(Migration.AppId));
            var newData = Path.Combine(root, "appdata", Migration.AppId);
            Directory.CreateDirectory(oldData);
            File.WriteAllText(Path.Combine(oldData, "settings.json"), "{\"Theme\":\"Light\"}");
            Check(Migration.Folder(oldData, newData) && !Directory.Exists(oldData) && File.Exists(Path.Combine(newData, "settings.json")),
                "migration: old settings folder moved to " + Migration.AppId);
            Directory.CreateDirectory(oldData);
            Check(!Migration.Folder(oldData, newData) && File.ReadAllText(Path.Combine(newData, "settings.json")).Contains("Light"),
                "migration: second run is a no-op (new folder kept)");
            Check(!Migration.Folder(Path.Combine(root, "missing"), Path.Combine(root, "x")), "migration: nothing to move is a no-op");

            // ---- folder names ----
            var programs = Path.Combine(root, "Programs");
            Check(Migration.MapFolder(Path.Combine(programs, Migration.FormerDisplayName)) == Path.Combine(programs, Installer.AppName),
                "install folder with the former default name maps to …\\" + Installer.AppName);
            Check(Migration.MapFolder(@"D:\Tools\Files") == @"D:\Tools\Files" && Migration.MapFolder(null) is null, "other install folders are kept");
            Check(Updater.SetupName().IsMatch("pxdCommander-1.4.0-win-x64-setup.exe") && Updater.SetupShaName().IsMatch("pxdCommander-1.4.0-win-x64-setup.exe.sha256"),
                "update check finds pxdCommander-<v>-win-x64-setup.exe + .sha256");

            // ---- installer: a former install in a private registry hive and temp shortcut folders ----
            if (OperatingSystem.IsWindows())
            {
                var hiveFile = Path.Combine(root, "hive.dat");
                var rc = RegLoadAppKeyW(hiveFile, out var handle, 0xF003F /* KEY_ALL_ACCESS */, 0, 0);
                Check(rc == 0, $"private registry hive loaded (RegLoadAppKey {rc})");
                if (rc == 0)
                {
                    hive = RegistryKey.FromHandle(handle);
                    Installer.Root = hive;
                    Installer.Testing = true;
                    Installer.TestStartMenuFolder = Path.Combine(root, "StartMenu");
                    Installer.TestDesktopFolder = Path.Combine(root, "Desktop");
                    Directory.CreateDirectory(Installer.TestStartMenuFolder);
                    Directory.CreateDirectory(Installer.TestDesktopFolder);

                    var oldFolder = Path.Combine(programs, Migration.FormerDisplayName);
                    Directory.CreateDirectory(oldFolder);
                    File.WriteAllText(Path.Combine(oldFolder, Migration.FormerExeName), "old exe");
                    using (var k = hive.CreateSubKey(Migration.FormerUninstallKey))
                    {
                        k.SetValue("InstallLocation", oldFolder);
                        k.SetValue("DisplayVersion", "1.3.10");
                    }
                    foreach (var m in Migration.FormerMenuKeys)
                    {
                        using var k = hive.CreateSubKey(m);
                        using var c = k.CreateSubKey("command");
                        c.SetValue("", "old");
                    }
                    var oldStart = Path.Combine(Installer.StartMenuFolder, Migration.FormerDisplayName + ".lnk");
                    var oldDesk = Path.Combine(Installer.DesktopFolder, Migration.FormerDisplayName + ".lnk");
                    File.WriteAllText(oldStart, "lnk");
                    File.WriteAllText(oldDesk, "lnk");

                    var newFolder = Path.Combine(programs, Installer.AppName);
                    Check(Installer.SuggestedFolder == newFolder, "setup suggests " + newFolder + " for a former install");
                    Check(Installer.HasDesktopShortcut && Installer.HasFolderMenu && Migration.FormerInstalledVersion == "1.3.10",
                        "former desktop shortcut, folder menu and version are seen (an update keeps those choices)");

                    var exe = Installer.InstallAsync(newFolder, desktop: true, folderMenu: true, _ => { }).GetAwaiter().GetResult();
                    Check(File.Exists(exe) && Path.GetFileName(exe) == "pxdCommander.exe", "installed " + exe);
                    Check(File.Exists(Path.Combine(Installer.StartMenuFolder, Installer.AppName + ".lnk")) &&
                          File.Exists(Path.Combine(Installer.DesktopFolder, Installer.AppName + ".lnk")), "new Start menu and desktop shortcuts");
                    Check(Installer.InstalledFolder == newFolder && Installer.InstalledVersion == Installer.Version, "new Apps entry (Uninstall\\pxdCommander)");
                    using (var m = hive.OpenSubKey(@"Software\Classes\Directory\shell\pxdCommander\command"))
                        Check(m?.GetValue("") is string cmd && cmd.Contains(exe), "new folder right-click entry points at the new exe");
                    Check(hive.OpenSubKey(Migration.FormerUninstallKey) is null && Migration.FormerMenuKeys.All(p => hive.OpenSubKey(p) is null),
                        "former Apps entry and folder right-click entries removed");
                    Check(!File.Exists(oldStart) && !File.Exists(oldDesk), "former Start menu and desktop shortcuts removed");
                    Check(!Directory.Exists(oldFolder), "former exe and its empty folder removed");

                    Check(!Migration.RemoveFormerInstall(newFolder), "migration: second run is a no-op");
                    exe = Installer.InstallAsync(newFolder, desktop: false, folderMenu: false, _ => { }).GetAwaiter().GetResult();
                    Check(File.Exists(exe) && !Installer.HasDesktopShortcut && !Installer.HasFolderMenu, "reinstall without desktop shortcut / folder menu");

                    // A former install somewhere the user chose keeps that folder; only the old exe goes.
                    var custom = Path.Combine(root, "Tools");
                    Directory.CreateDirectory(custom);
                    File.WriteAllText(Path.Combine(custom, Migration.FormerExeName), "old exe");
                    File.WriteAllText(Path.Combine(custom, "notes.txt"), "keep me");
                    using (var k = hive.CreateSubKey(Migration.FormerUninstallKey)) k.SetValue("InstallLocation", custom);
                    Check(Migration.MapFolder(Migration.FormerInstalledFolder) == custom, "former custom install folder is kept as is");
                    Check(Migration.RemoveFormerInstall(newFolder) && !File.Exists(Path.Combine(custom, Migration.FormerExeName)) &&
                          File.Exists(Path.Combine(custom, "notes.txt")), "former exe removed from a custom folder, other files kept");
                }
            }

            UpdateChecks(root, Check);
        }
        catch (Exception ex)
        {
            Check(false, "crashed: " + ex);
        }
        finally
        {
            Installer.Root = Registry.CurrentUser;
            hive?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        sb.AppendLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return sb.ToString();
    }

    /// <summary>
    /// Update before start: which starts may update, and a fake GitHub release on a local HttpListener (check, download,
    /// checksum, exe swap). Nothing is installed and no process is started.
    /// </summary>
    private static void UpdateChecks(string root, Action<bool, string> check)
    {
        check(!Updater.BeforeStart(["--self-test"]), "update before start: never during --self-test");
        var apps = Path.Combine(root, "Apps", "pxdCommander", "pxdCommander.exe");
        check(!Updater.ShouldCheckBeforeStart([], Path.Combine(root, @"src\bin\Release\net10.0\win-x64\pxdCommander.exe")), "update before start: never for a bin\\ build");
        check(!Updater.ShouldCheckBeforeStart(["--write-icon", "x.ico"], apps) && !Updater.ShouldCheckBeforeStart(["--quiet"], apps) &&
              !Updater.ShouldCheckBeforeStart(["--uninstall"], apps) && !Updater.ShouldCheckBeforeStart(["--after-update", "1"], apps) &&
              !Updater.ShouldCheckBeforeStart(["--no-update"], apps) && !Updater.ShouldCheckBeforeStart([], Path.Combine(root, "pxdcomm-setup.exe")),
            "update before start: skipped for --write-icon, setup/uninstall, a just-updated copy and a setup-named file");

        var setupBytes = System.Text.Encoding.ASCII.GetBytes("pretend setup " + Guid.NewGuid());
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(setupBytes)).ToLowerInvariant();
        var tag = "v9.9.9";
        var badSum = false;
        var port = new Random().Next(41000, 49000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var v = tag.TrimStart('v');
                byte[] body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/repos/joshpack/pxdCommander/releases/latest" => System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false,\"body\":\"## New in {v}\\n- Faster\",\"assets\":[" +
                        $"{{\"name\":\"pxdCommander-{v}-win-x64-portable.zip\",\"browser_download_url\":\"{prefix}zip\",\"size\":3}}," +
                        $"{{\"name\":\"pxdCommander-{v}-win-x64-setup.exe\",\"browser_download_url\":\"{prefix}setup\",\"size\":{setupBytes.Length}}}," +
                        $"{{\"name\":\"pxdCommander-{v}-win-x64-setup.exe.sha256\",\"browser_download_url\":\"{prefix}sum\"}}]}}"),
                    "/setup" => setupBytes,
                    "/sum" => System.Text.Encoding.ASCII.GetBytes((badSum ? new string('0', 64) : hash) + $"  pxdCommander-{v}-win-x64-setup.exe"),
                    _ => [],
                };
                ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
                try { await ctx.Response.OutputStream.WriteAsync(body); } catch { }
                ctx.Response.Close();
            }
        });

        Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));
        Updater.TestDownloadFolder = Path.Combine(root, "update-download");
        try
        {
            check(Updater.ShouldCheckBeforeStart(["C:\\Some folder"], apps), "update before start: an installed / Toolkit copy checks (fake release)");
            Environment.SetEnvironmentVariable("PXD_NO_UPDATE", "1");
            check(!Updater.ShouldCheckBeforeStart([], apps), "update before start: PXD_NO_UPDATE=1 turns it off");
            Environment.SetEnvironmentVariable("PXD_NO_UPDATE", null);
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(AppPaths.File("settings.json"), "{\"CheckForUpdates\": false}");
            check(!Updater.ShouldCheckBeforeStart([], apps), "update before start: \"Check for updates\" off in settings turns it off");
            File.Delete(AppPaths.File("settings.json"));

            var info = Updater.CheckAsync().GetAwaiter().GetResult();
            check(info is { Version: "9.9.9" } && info.SetupUrl.EndsWith("/setup"), "update: newer release found, setup asset picked (not the portable zip)");

            badSum = true;
            var refused = false;
            try { Updater.DownloadAsync(info!, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { refused = true; }
            check(refused && !Directory.EnumerateFiles(Updater.DownloadFolder, "*.exe").Any(), "update: a bad checksum is refused and nothing is kept");
            badSum = false;
            var setup = Updater.DownloadAsync(info!, null, CancellationToken.None).GetAwaiter().GetResult();
            check(File.ReadAllBytes(setup).SequenceEqual(setupBytes) && Path.GetFileName(setup).Contains("setup"), "update: setup downloaded and verified");

            tag = "v" + Installer.Version;
            check(Updater.CheckAsync().GetAwaiter().GetResult() is null, "update: the current version is left alone");

            var a = Updater.BeforeStartSetupArguments(@"C:\Programs\pxd Commander", 42, desktop: false, folderMenu: true, ["--no-update-test", "rel"]);
            var then = a.IndexOf("--then");
            check(a.Take(then).SequenceEqual(["--quiet", "--dir", @"C:\Programs\pxd Commander", "--wait-pid", "42", "--relaunch", "--no-desktop"]) &&
                  a[then + 1] == "--no-update-test" && Path.IsPathFullyQualified(a[then + 2]) && a[^1] == "--no-update",
                "update: quiet setup in place, then reopens with the same arguments (paths made absolute)");

            var swapDir = Path.Combine(root, "swap");
            Directory.CreateDirectory(swapDir);
            var exe = Path.Combine(swapDir, "pxdCommander.exe");
            File.WriteAllText(exe, "old");
            Updater.SwapExe(exe, setup);
            check(File.ReadAllBytes(exe).SequenceEqual(setupBytes) && File.ReadAllText(exe + ".old") == "old", "update: a portable copy's exe is swapped, the old one kept as .old");
        }
        catch (Exception ex)
        {
            check(false, "update checks crashed: " + ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
            Environment.SetEnvironmentVariable("PXD_NO_UPDATE", null);
            Updater.TestDownloadFolder = null;
            listener.Stop();
            serving.Wait(2000);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle key, int samDesired, int options, int reserved);
}
