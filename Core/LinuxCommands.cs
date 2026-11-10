using System.Text.RegularExpressions;
using pxdCommander.Core.Remote;

namespace pxdCommander.Core;

/// <summary>
/// The other direction from <see cref="WinCommands"/>: Linux habits in the local PowerShell terminal (ls -la, grep,
/// rm -rf, touch, which, head/tail, …). PowerShell already understands ls, cat, cp, mv, pwd, echo and ps by name;
/// this covers the flags and commands it doesn't.
/// </summary>
public static partial class LinuxCommands
{
    public static WinCommands.Result Translate(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return new(line, false, null);
        if (trimmed.StartsWith("sudo ", StringComparison.Ordinal)) trimmed = trimmed[5..].TrimStart();

        var space = trimmed.IndexOf(' ');
        var verb = space < 0 ? trimmed : trimmed[..space];
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        var flags = Flags(rest, out var args);

        string? cmd = verb switch
        {
            "clear" or "cls" => "",
            "ls" or "ll" or "la" when flags.Length > 0 || verb != "ls" =>
                Join("Get-ChildItem" + (flags.Contains('a') || verb != "ls" ? " -Force" : "") + (flags.Contains('R') ? " -Recurse" : ""), args),
            "rm" => Join("Remove-Item" + (flags.Contains('r') || flags.Contains('R') ? " -Recurse" : "") + (flags.Contains('f') ? " -Force" : ""), args),
            "cp" when flags.Length > 0 => Join("Copy-Item" + (flags.Contains('r') || flags.Contains('R') ? " -Recurse" : "") + (flags.Contains('f') ? " -Force" : ""), args),
            "mkdir" when flags.Contains('p') => Join("New-Item -ItemType Directory -Force", args),
            "touch" => $"New-Item -ItemType File -Force {args} | Out-Null",
            "which" => $"(Get-Command {args}).Source",
            "grep" => Grep(flags, args),
            "head" => HeadTail("-TotalCount", rest),
            "tail" when Regex.IsMatch(rest, @"(^|\s)-f(\s|$)") => $"Get-Content -Wait {Regex.Replace(rest, @"(^|\s)-f(\s|$)", " ").Trim()}",
            "tail" => HeadTail("-Tail", rest),
            "ifconfig" => "ipconfig",
            "ip" when args is "a" or "addr" or "address" => "ipconfig",
            "df" => "Get-PSDrive -PSProvider FileSystem",
            "uname" => "[Environment]::OSVersion.VersionString",
            "kill" when flags.Contains('9') => Join("Stop-Process -Force -Id", args),
            "export" when args.Contains('=') => $"$env:{args[..args.IndexOf('=')]} = '{args[(args.IndexOf('=') + 1)..].Trim('"', '\'')}'",
            "nano" or "vim" or "vi" => Join("notepad", args),
            "open" or "xdg-open" => Join("Invoke-Item", args),
            _ => null,
        };

        if (cmd == "") return new("", true, null);
        cmd ??= trimmed;
        // "… | grep x" filters lines in PowerShell with Select-String.
        cmd = PipeGrep().Replace(cmd, "| Select-String ");
        return new(cmd, false, cmd == line.Trim() ? null : $"{line.Trim()} → {cmd}");
    }

    /// <summary>Splits "-la", "-rf" style flags (letters, case kept) from the arguments.</summary>
    private static string Flags(string rest, out string args)
    {
        var letters = "";
        var kept = new List<string>();
        foreach (var part in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 2 && part[0] == '-' && part[1] != '-' && part.Skip(1).All(char.IsLetterOrDigit))
                letters += part[1..];
            else
                kept.Add(part);
        }
        args = string.Join(' ', kept);
        return letters;
    }

    private static string Join(string cmd, string args) => args.Length == 0 ? cmd : cmd + " " + args;

    private static string Grep(string flags, string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Select-String";
        var pattern = parts[0];
        var files = parts.Length > 1 ? parts[1] : null;
        var cs = flags.Contains('i') ? "" : " -CaseSensitive";
        if (files is null) return $"Select-String {pattern}{cs}";
        return flags.Contains('r') || flags.Contains('R')
            ? $"Get-ChildItem -Recurse -File {files} | Select-String {pattern}{cs}"
            : $"Select-String {pattern} {files}{cs}";
    }

    private static string HeadTail(string param, string rest)
    {
        var m = Regex.Match(rest, @"-n\s*(\d+)|-(\d+)");
        var count = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "10";
        var file = Regex.Replace(rest, @"-n\s*\d+|-\d+", "").Trim();
        return $"Get-Content {file} {param} {count}";
    }

    [GeneratedRegex(@"\|\s*grep(\s+-\w+)*\s+")]
    private static partial Regex PipeGrep();
}
