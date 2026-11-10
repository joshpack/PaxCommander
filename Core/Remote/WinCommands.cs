using System.Text.RegularExpressions;

namespace pxdCommander.Core.Remote;

/// <summary>
/// Lets Windows habits work in the SSH terminal: common cmd commands are rewritten to their Linux equivalents before
/// they're sent (dir → ls -la, ipconfig → ip addr, …). Anything else goes to the server unchanged.
/// </summary>
public static partial class WinCommands
{
    public sealed record Result(string Command, bool ClearScreen, string? Note);

    public static Result Translate(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return new(line, false, null);

        // "cd.." and "cd\" are valid in cmd without a space.
        if (trimmed.StartsWith("cd..", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("cd\\", StringComparison.Ordinal))
            trimmed = "cd " + trimmed[2..];

        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        var flags = Flags(rest, out var args);

        string? cmd = verb switch
        {
            "cls" => "",
            "dir" => flags.Contains('b') ? Join("ls -1", args) : Join(flags.Contains('s') ? "ls -laR" : "ls -la", args),
            "copy" => Join("cp", args),
            "xcopy" or "robocopy" => Join("cp -r", args),
            "move" => Join("mv", args),
            "ren" or "rename" => Join("mv", args),
            "del" or "erase" => Join(flags.Contains('s') ? "rm -r" : "rm", args),
            "rd" or "rmdir" when flags.Contains('s') => Join("rm -r", args),
            "rd" => Join("rmdir", args),
            "md" or "mkdir" => Join("mkdir -p", args),
            "chdir" => Join("cd", args),
            "cd" when args.Length > 0 => Join("cd", args),
            "type" when args.Length > 0 && (args.Contains('.') || args.Contains('/')) => Join("cat", args),
            "ipconfig" => "ip addr",
            "tasklist" => "ps aux",
            "taskkill" => TaskKill(rest),
            "findstr" => Join(flags.Contains('i') ? "grep -i" : "grep", args),
            "where" => Join("which", args),
            "ver" or "systeminfo" => "uname -a",
            "tracert" => Join("traceroute", args),
            "ping" => Ping(rest),
            "set" when rest.Length == 0 => "env",
            _ => null,
        };

        if (cmd == "") return new("", true, null);
        if (cmd is null)
        {
            // Unchanged command; still expand %VAR% to $VAR.
            var expanded = PercentVar().Replace(line, "$$$1");
            return new(expanded, false, null);
        }
        cmd = PercentVar().Replace(cmd, "$$$1");
        return new(cmd, false, cmd == trimmed ? null : $"{trimmed} → {cmd}");
    }

    /// <summary>Strips cmd switches like /s /q and returns their letters; paths keep forward slashes.</summary>
    private static string Flags(string rest, out string args)
    {
        var letters = "";
        var kept = new List<string>();
        foreach (var part in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 2 && part[0] == '/' && char.IsLetter(part[1]))
                letters += char.ToLowerInvariant(part[1]);
            else
                kept.Add(part.Replace('\\', '/'));
        }
        args = string.Join(' ', kept);
        return letters;
    }

    private static string Join(string cmd, string args) => args.Length == 0 ? cmd : cmd + " " + args;

    private static string Ping(string rest)
    {
        // Windows pings 4 times by default; Linux forever. -t means "forever" on Windows.
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var forever = parts.RemoveAll(p => p.Equals("-t", StringComparison.OrdinalIgnoreCase) || p.Equals("/t", StringComparison.OrdinalIgnoreCase)) > 0;
        for (var i = 0; i < parts.Count; i++)
            if (parts[i] is "-n" or "/n") parts[i] = "-c";
        if (!forever && !parts.Contains("-c")) parts.Insert(0, "-c 4");
        return Join("ping", string.Join(' ', parts));
    }

    private static string TaskKill(string rest)
    {
        var pid = Regex.Match(rest, @"/pid\s+(\d+)", RegexOptions.IgnoreCase);
        var force = Regex.IsMatch(rest, @"(^|\s)/f(\s|$)", RegexOptions.IgnoreCase) ? "-9 " : "";
        if (pid.Success) return $"kill {force}{pid.Groups[1].Value}";
        var image = Regex.Match(rest, @"/im\s+(\S+)", RegexOptions.IgnoreCase);
        return image.Success ? $"pkill {force}{Path.GetFileNameWithoutExtension(image.Groups[1].Value)}" : "kill " + rest;
    }

    [GeneratedRegex(@"%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex PercentVar();
}
