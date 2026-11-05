using System.Text;
using Renci.SshNet;

namespace PaxCommander.Core.Remote;

/// <summary>
/// An interactive SSH shell (a "dumb" terminal, so programs keep their output plain). Output arrives on a background
/// thread as decoded text; the shell reports its working folder after every command so the file view can follow.
/// </summary>
public sealed class RemoteShell : IShellSession
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    private RemoteShell(Connection c, SshClient client, ShellStream stream)
    {
        Connection = c;
        _client = client;
        _stream = stream;
        _stream.DataReceived += (_, e) =>
        {
            var chars = new char[_decoder.GetCharCount(e.Data, 0, e.Data.Length)];
            _decoder.GetChars(e.Data, 0, e.Data.Length, chars, 0);
            Output?.Invoke(new string(chars));
        };
        _stream.Closed += (_, _) => Closed?.Invoke();
    }

    public Connection Connection { get; }
    public event Action<string>? Output;
    public event Action? Closed;
    public bool IsOpen => _client.IsConnected && _stream.CanWrite;

    /// <summary>
    /// Sent once after login, quietly: report $PWD as an OSC 7 sequence at every prompt (bash and zsh), then go to
    /// <paramref name="folder"/>. The terminal hides output until that first report arrives.
    /// </summary>
    public static string SetupLine(string folder) =>
        " __pax(){ printf '\\033]7;%s\\007' \"$PWD\"; }; PROMPT_COMMAND=__pax; precmd(){ __pax; }; cd -- " + Quote(folder) + " 2>/dev/null\r";

    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static RemoteShell Connect(Connection c, string? secret)
    {
        var client = new SshClient(SftpFs.BuildInfo(c, secret));
        client.HostKeyReceived += (_, e) => e.CanTrust = SftpFs.CheckHostKey(c, e);
        client.Connect();
        var stream = client.CreateShellStream("dumb", 160, 48, 0, 0, 64 * 1024);
        return new RemoteShell(c, client, stream);
    }

    public void Send(string text)
    {
        if (!IsOpen) return;
        _stream.Write(text);
        _stream.Flush();
    }

    // The server's terminal echoes what we type, so no local echo.
    public bool NeedsLocalEcho => false;
    public void Start(string folder) => Send(SetupLine(folder));
    public void SendLine(string line) => Send(line + "\r");
    public void SendRaw(string text) => Send(text);
    /// <summary>A pasted script: its lines, as if typed one after another (bash and zsh handle continuations).</summary>
    public void SendBlock(string block) => Send(block.Replace("\n", "\r") + "\r");
    public string CdCommand(string folder) => " cd -- " + Quote(folder);

    public bool Interrupt()
    {
        Send("\x03");
        return true;
    }

    public void Dispose()
    {
        var (client, stream) = (_client, _stream);
        Task.Run(() =>
        {
            try { stream.Dispose(); } catch { }
            try { client.Disconnect(); } catch { }
            client.Dispose();
        });
    }
}
