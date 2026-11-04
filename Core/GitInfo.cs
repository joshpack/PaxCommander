using System.ComponentModel;
using System.Diagnostics;

namespace PaxCommander.Core;

/// <summary>Light git awareness: the branch from .git/HEAD (no process), file states from `git status` when git exists.</summary>
public static class GitInfo
{
    public sealed record Repo(string Root, string Branch);

    private static bool _gitMissing;

    public static Repo? Find(string path)
    {
        try
        {
            for (var dir = path; !string.IsNullOrEmpty(dir) && !PathUtil.IsComputer(dir); dir = Path.GetDirectoryName(dir))
            {
                var dotGit = Path.Combine(dir, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit)) gitDir = dotGit;
                else if (File.Exists(dotGit))
                {
                    // Worktrees and submodules point at the real git dir from a .git file.
                    var line = File.ReadLines(dotGit).FirstOrDefault() ?? "";
                    if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                        gitDir = Path.GetFullPath(Path.Combine(dir, line[7..].Trim()));
                }
                if (gitDir is null) continue;
                var head = File.Exists(Path.Combine(gitDir, "HEAD")) ? File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim() : "";
                var branch = head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head[16..]
                    : head.Length >= 7 ? head[..7] : "HEAD";
                return new Repo(dir, branch);
            }
        }
        catch
        {
            // Unreadable parents (network roots, permissions) just mean "no repo".
        }
        return null;
    }

    /// <summary>Path (relative, '/' separated) → status mark, or null when git isn't available or took too long.</summary>
    public static async Task<Dictionary<string, char>?> StatusAsync(string root, CancellationToken ct)
    {
        if (_gitMissing) return null;
        try
        {
            var psi = new ProcessStartInfo("git", ["-C", root, "status", "--porcelain=v1", "-z"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var output = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            if (proc.ExitCode != 0) return null;

            var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
            var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var entry = parts[i];
                if (entry.Length < 4) continue;
                var x = entry[0];
                var y = entry[1];
                var file = entry[3..].TrimEnd('/');
                var mark = x == '?' ? '?' : x == 'R' || y == 'R' ? 'R' : x == 'A' ? 'A' : x == 'D' || y == 'D' ? 'D' : 'M';
                map[file] = mark;
                // Renames carry the old name as the next entry.
                if (x == 'R' || x == 'C') i++;
            }
            return map;
        }
        catch (Win32Exception)
        {
            _gitMissing = true;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Marks items in a listing; a folder gets the mark of anything changed inside it.</summary>
    public static void Apply(Repo repo, Dictionary<string, char> status, IEnumerable<FileItem> items)
    {
        foreach (var item in items)
        {
            var rel = Path.GetRelativePath(repo.Root, item.Path).Replace('\\', '/');
            if (status.TryGetValue(rel, out var mark))
            {
                item.Git = mark;
                continue;
            }
            if (item.IsDirectory)
            {
                var prefix = rel + "/";
                var inner = status.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                item.Git = inner.Key is null ? '\0' : inner.Value == '?' ? '?' : 'M';
            }
            else item.Git = '\0';
        }
    }
}
