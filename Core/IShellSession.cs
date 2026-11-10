namespace pxdCommander.Core;

/// <summary>
/// A running shell behind the docked terminal: an SSH shell on a server, or PowerShell on this PC. Output arrives on
/// a background thread; the shell reports its folder with an OSC 7 sequence after every command.
/// </summary>
public interface IShellSession : IDisposable
{
    event Action<string>? Output;
    event Action? Closed;
    bool IsOpen { get; }
    /// <summary>True when the shell doesn't echo typed commands itself, so the terminal shows them.</summary>
    bool NeedsLocalEcho { get; }
    /// <summary>Quietly sets the shell up (folder reporting) and moves it to <paramref name="folder"/>.</summary>
    void Start(string folder);
    /// <summary>Runs one line as typed.</summary>
    void SendLine(string line);
    /// <summary>Runs a multi-line command (a pasted script) as one unit.</summary>
    void SendBlock(string block);
    /// <summary>Sends raw keystrokes (Ctrl+C, a password answer).</summary>
    void SendRaw(string text);
    /// <summary>The command that moves the shell to <paramref name="folder"/> (sent when the file view moves).</summary>
    string CdCommand(string folder);
    /// <summary>Stops the running command. False when the shell can't, and has to be restarted instead.</summary>
    bool Interrupt();
}
