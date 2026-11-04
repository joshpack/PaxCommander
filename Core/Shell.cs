using System.Diagnostics;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Core;

/// <summary>Opening things with the operating system: default apps, Open with, properties, terminals, Explorer.</summary>
public static class Shell
{
    public static void Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ShellActions.Open(path, null, Path.GetDirectoryName(path));
            return;
        }
        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(opener) { ArgumentList = { path }, UseShellExecute = false });
    }

    public static void OpenWith(IntPtr owner, string path)
    {
        if (OperatingSystem.IsWindows()) ShellActions.OpenWith(owner, path);
    }

    public static void RunAsAdmin(string path)
    {
        if (OperatingSystem.IsWindows()) ShellActions.Open(path, "runas", Path.GetDirectoryName(path));
    }

    public static void Properties(IntPtr owner, IReadOnlyList<string> paths)
    {
        if (OperatingSystem.IsWindows() && paths.Count > 0) ShellActions.ShowProperties(owner, paths);
    }

    public static bool CanRunAsAdmin(FileItem item) => OperatingSystem.IsWindows() && item.Extension is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".com";

    /// <summary>Shows the item selected in the system file manager.</summary>
    public static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }
        Open(Path.GetDirectoryName(path) ?? path);
    }

    /// <summary>
    /// "Open terminal here": the user's own command if set ({path} is replaced), else Windows Terminal, else PowerShell
    /// 7, else Windows PowerShell; on Linux the default terminal emulator.
    /// </summary>
    public static void OpenTerminal(string folder)
    {
        var custom = AppSettings.Current.TerminalCommand.Trim();
        if (custom.Length > 0)
        {
            var (exe, args) = SplitCommand(custom.Replace("{path}", folder, StringComparison.OrdinalIgnoreCase));
            Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = folder, UseShellExecute = true });
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var (exe, args) in new[]
                     {
                         ("wt.exe", new[] { "-d", folder }),
                         ("pwsh.exe", new[] { "-NoExit", "-WorkingDirectory", folder }),
                         ("powershell.exe", new[] { "-NoExit", "-Command", $"Set-Location -LiteralPath '{folder.Replace("'", "''")}'" }),
                     })
            {
                try
                {
                    var psi = new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = true };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    Process.Start(psi);
                    return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Not installed; try the next one.
                }
            }
            return;
        }
        foreach (var exe in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = false });
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }
}
