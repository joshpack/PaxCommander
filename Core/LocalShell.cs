using System.Diagnostics;
using System.Text;

namespace PaxCommander.Core;

/// <summary>
/// PowerShell on this PC for the docked terminal (pwsh when installed, else Windows PowerShell). Commands go in on
/// stdin, one line at a time; after each, a marker reports the folder (OSC 7) so the file view can follow.
/// </summary>
public sealed class LocalShell : IShellSession
{
    // Prints ESC ] 7 ; <folder> BEL — the same folder report the SSH shell sends.
    private const string Marker = "[Console]::Out.Write([char]27 + ']7;' + (Get-Location).ProviderPath + [char]7)";

    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();

    private LocalShell(Process process)
    {
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Closed?.Invoke();
        _ = Pump(_process.StandardOutput.BaseStream);
        _ = Pump(_process.StandardError.BaseStream);
    }

    public event Action<string>? Output;
    public event Action? Closed;
    public bool IsOpen => !_process.HasExited;
    public bool NeedsLocalEcho => true;

    public static LocalShell Start()
    {
        var exe = FindOnPath("pwsh.exe") ?? "powershell.exe";
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "-" }) psi.ArgumentList.Add(a);
        return new LocalShell(Process.Start(psi) ?? throw new IOException("PowerShell didn't start."));
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    private async Task Pump(Stream stream)
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(bytes, _cts.Token)) > 0)
            {
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch
        {
            // Closed.
        }
    }

    private void Write(string line)
    {
        if (!IsOpen) return;
        // A redirected PowerShell reads stdin in the old OEM code page, which mangles ü, ✓, 日本 and the like (and
        // switching [Console]::InputEncoding later doesn't reach its reader). Lines with such characters travel as
        // UTF-8 Base64 and run in place with Invoke-Expression, in the same scope as if typed.
        if (line.Any(c => c > 127))
            line = "Invoke-Expression ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                   Convert.ToBase64String(Encoding.UTF8.GetBytes(line)) + "')))";
        try
        {
            _process.StandardInput.Write(line + "\n");
            _process.StandardInput.Flush();
        }
        catch (IOException)
        {
        }
    }

    public void Start(string folder)
    {
        Write("[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + CdCommand(folder));
        Write(Marker);
    }

    public void SendLine(string line)
    {
        Write(line);
        Write(Marker);
    }

    /// <summary>A pasted script runs as one piece (loops and braces across lines intact), in the current scope.</summary>
    public void SendBlock(string block)
    {
        Write("Invoke-Expression ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(block)) + "')))");
        Write(Marker);
    }

    public void SendRaw(string text) => Write(text);

    public string CdCommand(string folder) => "Set-Location -LiteralPath '" + folder.Replace("'", "''") + "'";

    /// <summary>A redirected PowerShell can't take Ctrl+C; the terminal restarts it instead.</summary>
    public bool Interrupt() => false;

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        _process.Dispose();
    }
}
