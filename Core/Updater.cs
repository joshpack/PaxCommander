using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pxdCommander.Core;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Notes, string SetupUrl, string ShaUrl, string PageUrl, long Size);

/// <summary>
/// Checks the public GitHub releases for a newer version, downloads its installer, checks it against the release's
/// .sha256 file and runs it. Since 1.4.1 this happens before the app opens (<see cref="BeforeStart"/>, no prompt);
/// "Check for updates" still runs it on demand. The check only reads GitHub's release list (no account, nothing about
/// the PC is sent beyond a generic user agent).
/// </summary>
public static partial class Updater
{
    public const string Repo = "joshpack/pxdCommander";

    /// <summary>
    /// Release API address. PXDCMD_UPDATE_API (the full "latest release" URL) or PXD_UPDATE_API (an API root, as for the
    /// other pxd apps) point tests at a local fake.
    /// </summary>
    private static string LatestUrl =>
        Environment.GetEnvironmentVariable("PXDCMD_UPDATE_API") is { Length: > 0 } u ? u
        : Environment.GetEnvironmentVariable("PXD_UPDATE_API") is { Length: > 0 } api ? $"{api.TrimEnd('/')}/repos/{Repo}/releases/latest"
        : $"https://api.github.com/repos/{Repo}/releases/latest";

    private static bool FakeApi =>
        Environment.GetEnvironmentVariable("PXDCMD_UPDATE_API") is { Length: > 0 } || Environment.GetEnvironmentVariable("PXD_UPDATE_API") is { Length: > 0 };

    // ---- Update before the app opens ----------------------------------------------------------------------------------

    /// <summary>
    /// First thing in Main: when a newer release is out, installs it before the app opens (no prompt) and starts the new
    /// copy with the same arguments. True when this process should now exit. The installed copy updates through the
    /// release's setup (quietly, in place, keeping its shortcut and folder-menu choices); a portable or Toolkit copy swaps
    /// its exe for the new single file. Never throws: offline, GitHub slower than 4 s or any failure just opens this copy.
    /// </summary>
    public static bool BeforeStart(string[] args)
    {
        try
        {
            if (!ShouldCheckBeforeStart(args, Environment.ProcessPath)) return false;
            // Another copy is open: this launch only hands its paths over. The open copy updates on its next start.
            if (SingleInstance.AnotherCopyRunning()) return false;
            UpdateInfo? info;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                info = CheckAsync(cts.Token).GetAwaiter().GetResult();
            if (info is null) return false;
            var setup = DownloadAsync(info, null, CancellationToken.None).GetAwaiter().GetResult();
            return LaunchBeforeStart(setup, args);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this start should look for an update first. Not for: --self-test, --write-icon, the setup and uninstall
    /// modes (and a file named like a setup), a copy just started by an update (--after-update / --no-update),
    /// PXD_NO_UPDATE=1, dev builds (bin\ or obj\, or a multi-file build with pxdCommander.dll beside it), test runs with
    /// their own data folder (PXDCMD_DATA) unless pointed at a fake release, and "Check for updates" switched off.
    /// </summary>
    public static bool ShouldCheckBeforeStart(string[] args, string? exe)
    {
        if (exe is null || !OperatingSystem.IsWindows()) return false;
        if (Environment.GetEnvironmentVariable("PXD_NO_UPDATE") == "1") return false;
        string[] skip = ["--self-test", "--write-icon", "--install", "--uninstall", "--quiet", "--after-update", "--no-update"];
        if (args.Any(a => skip.Contains(a, StringComparer.OrdinalIgnoreCase))) return false;
        if (Path.GetFileNameWithoutExtension(exe).Contains("setup", StringComparison.OrdinalIgnoreCase)) return false;
        var dir = Path.GetDirectoryName(exe) ?? "";
        if (dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(Path.Combine(dir, "pxdCommander.dll"))) return false;
        if (Environment.GetEnvironmentVariable("PXDCMD_DATA") is { Length: > 0 } && !FakeApi) return false;
        return UpdatesEnabled();
    }

    /// <summary>The "Check for updates" setting, read straight from settings.json (the app hasn't loaded yet).</summary>
    private static bool UpdatesEnabled()
    {
        try
        {
            var file = AppPaths.File("settings.json");
            if (!File.Exists(file)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return !(doc.RootElement.TryGetProperty("CheckForUpdates", out var v) && v.ValueKind == JsonValueKind.False);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Paths on the command line made absolute, so the new copy (started in another folder) finds them.</summary>
    public static string[] Absolute(string[] args) =>
        args.Select(a => a.StartsWith("--", StringComparison.Ordinal) ? a : TryFull(a)).ToArray();

    private static string TryFull(string a)
    {
        try { return Path.IsPathFullyQualified(a) || a.Contains("://") ? a : Path.GetFullPath(a); } catch { return a; }
    }

    /// <summary>The setup's arguments for an update before start: quiet in place, then reopen with <paramref name="args"/>.</summary>
    public static List<string> BeforeStartSetupArguments(string folder, int waitPid, bool desktop, bool folderMenu, string[] args)
    {
        List<string> list = ["--quiet", "--dir", folder, "--wait-pid", waitPid.ToString(), "--relaunch"];
        if (!desktop) list.Add("--no-desktop");
        if (!folderMenu) list.Add("--no-folder-menu");
        list.Add("--then");
        list.AddRange(Absolute(args));
        list.Add("--no-update");
        return list;
    }

    private static bool LaunchBeforeStart(string setupPath, string[] args)
    {
        if (RunningInstalledCopy)
        {
            var psi = new ProcessStartInfo(setupPath) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
            foreach (var a in BeforeStartSetupArguments(Installer.InstalledFolder!, Environment.ProcessId, Installer.HasDesktopShortcut, Installer.HasFolderMenu, args))
                psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return true;
        }
        if (RunningPortableCopy)
        {
            var exe = Environment.ProcessPath!;
            SwapExe(exe, setupPath);
            // The new copy waits for this one to close (--after-update), then opens with the same arguments.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--after-update");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (var a in Absolute(args)) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return true;
        }
        return false;
    }

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"pxd-Commander/{Installer.Version}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    });

    /// <summary>Where downloaded installers wait; cleared on the next start.</summary>
    public static string DownloadFolder => TestDownloadFolder ?? Path.Combine(Path.GetTempPath(), "pxdCommander-update");

    /// <summary>Set by the self-test so a test download never mixes with a real one.</summary>
    internal static string? TestDownloadFolder { get; set; }

    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate.TrimStart('v', 'V'), out var a) && System.Version.TryParse(current.TrimStart('v', 'V'), out var b) && a > b;

    /// <summary>The latest release if it's newer than this copy, else null. Throws on network trouble.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.Value.GetAsync(LatestUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!IsNewer(version, Installer.Version)) return null;

        string? setup = null, sha = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (SetupName().IsMatch(name))
            {
                setup = url;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (SetupShaName().IsMatch(name)) sha = url;
        }
        // No installer or no checksum: nothing we can safely install.
        if (setup is null || sha is null) return null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repo}/releases";
        return new UpdateInfo(version, NotesFrom(root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : ""), setup, sha, page, size);
    }

    [GeneratedRegex(@"^pxdCommander-[\d.]+-win-x64-setup\.exe$", RegexOptions.IgnoreCase)]
    internal static partial Regex SetupName();

    [GeneratedRegex(@"^pxdCommander-[\d.]+-win-x64-setup\.exe\.sha256$", RegexOptions.IgnoreCase)]
    internal static partial Regex SetupShaName();

    /// <summary>The "New in …" part of the release notes as plain lines (falls back to the start of the notes).</summary>
    public static string NotesFrom(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## New in", StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> pick = start >= 0
            ? lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("#") && !l.StartsWith("See ", StringComparison.Ordinal))
            : lines.Where(l => !l.StartsWith("#")).Take(8);
        var text = string.Join("\n", pick.Select(l => l.Replace("**", "").Replace("`", "").Trim()).Where(l => l.Length > 0));
        return text.Length > 900 ? text[..900].TrimEnd() + "…" : text;
    }

    /// <summary>Downloads the installer and checks its SHA-256 against the release's checksum file.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var expected = (await Http.Value.GetStringAsync(info.ShaUrl, ct)).Trim().Split(' ', '\t', '\n')[0].Trim().ToLowerInvariant();
        if (expected.Length != 64) throw new InvalidDataException("The release's checksum file is not readable.");

        Directory.CreateDirectory(DownloadFolder);
        // The name must contain "setup" so the downloaded copy runs as the installer.
        var target = Path.Combine(DownloadFolder, $"pxdcomm-setup-{info.Version}.exe");
        var partial = target + ".part";
        using (var response = await Http.Value.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.Size;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            TryDelete(partial);
            throw new InvalidDataException("The download doesn't match the release's checksum, so it was thrown away.");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Whether this copy is the installed one (then the update installs quietly in place and reopens). Portable or
    /// other copies get the normal setup screen instead.
    /// </summary>
    public static bool RunningInstalledCopy =>
        Installer.InstalledFolder is { } f && Path.GetDirectoryName(Environment.ProcessPath) is { } here &&
        string.Equals(Path.GetFullPath(f).TrimEnd('\\'), Path.GetFullPath(here).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Arguments for a quiet in-place update that keeps the current shortcut / folder-menu choices.</summary>
    public static string QuietArguments(string folder, int waitPid, bool desktop, bool folderMenu) =>
        $"--quiet --dir \"{folder}\" --wait-pid {waitPid} --relaunch" + (desktop ? "" : " --no-desktop") + (folderMenu ? "" : " --no-folder-menu");

    /// <summary>
    /// A self-contained single-file copy that isn't the setup-installed one: the portable zip, or a copy pxd Toolkit
    /// installed. The release's setup exe is the very same single file, so these update by swapping the exe in place.
    /// (A multi-file build has pxdCommander.dll beside it and gets the setup screen instead.)
    /// </summary>
    public static bool RunningPortableCopy =>
        !RunningInstalledCopy && Environment.ProcessPath is { } exe &&
        !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "pxdCommander.dll"));

    /// <summary>
    /// Replaces <paramref name="exe"/> with <paramref name="newExe"/>: the running file is renamed to .old (Windows
    /// allows that), the new one moved into its place; on failure the old one goes back.
    /// </summary>
    public static void SwapExe(string exe, string newExe)
    {
        var old = exe + ".old";
        TryDelete(old);
        var staged = exe + ".new";
        File.Copy(newExe, staged, overwrite: true);
        File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            File.Move(old, exe);
            TryDelete(staged);
            throw;
        }
    }

    /// <summary>Removes the .old exe left by an in-place update.</summary>
    public static void CleanUpOldExe()
    {
        if (Environment.ProcessPath is { } exe) TryDelete(exe + ".old");
    }

    /// <summary>Starts the installer. Returns true when the caller should now close so it can be replaced.</summary>
    public static bool Launch(string setupPath)
    {
        if (RunningPortableCopy)
        {
            var exe = Environment.ProcessPath!;
            SwapExe(exe, setupPath);
            // The new copy waits for us to close, then opens (otherwise it would just hand off to this window).
            Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            return true;
        }
        if (RunningInstalledCopy)
        {
            var folder = Installer.InstalledFolder!;
            Process.Start(new ProcessStartInfo(setupPath, QuietArguments(folder, Environment.ProcessId, Installer.HasDesktopShortcut, Installer.HasFolderMenu))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            return true;
        }
        Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() });
        return false;
    }

    /// <summary>Removes installers left over from an earlier update.</summary>
    public static void CleanUp()
    {
        try
        {
            Migration.RemoveFormerDownloads();
            if (Directory.Exists(DownloadFolder)) Directory.Delete(DownloadFolder, recursive: true);
        }
        catch
        {
            // Still running (the installer that just updated us): next start.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
