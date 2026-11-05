using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;
using PaxCommander.Core.Remote;

namespace PaxCommander.Views;

/// <summary>
/// The SSH terminal docked under a server's file view. Line-based: you type into the input row and Enter sends it, so
/// copy/paste and editing work like any text box. Windows commands are translated (see <see cref="WinCommands"/>), the
/// shell's folder is reported back after every command so the files above can follow, and full-screen programs
/// (vim, top) are pointed at Windows Terminal instead.
/// </summary>
/// <summary>What a terminal talks to: an SSH server, or PowerShell on this PC.</summary>
public sealed record TerminalSpec(
    string Key,
    string Title,
    string Greeting,
    string Placeholder,
    bool Local,
    Func<Task<IShellSession>> Open,
    Func<string, WinCommands.Result> Translate)
{
    public static TerminalSpec Ssh(Connection c) => new(
        c.Id,
        $"SSH  {c.User}@{c.Host}",
        $"Connected to {c.User}@{c.Host}. Linux commands run as usual; Windows ones like dir, cls, copy, del, type and ipconfig are translated.",
        "Type a command — Linux or Windows (dir, cls, copy, ipconfig…) — and press Enter",
        false,
        async () => await RemoteSessions.OpenShellAsync(c),
        WinCommands.Translate);

    public static TerminalSpec PowerShell() => new(
        "local",
        "PowerShell  this PC",
        "PowerShell on this PC. Linux habits work too: ls -la, grep, rm -rf, touch, which, head, tail…",
        "Type a command — PowerShell, cmd or Linux style (ls -la, grep, rm -rf…) — and press Enter",
        true,
        () => Task.Run<IShellSession>(LocalShell.Start),
        LinuxCommands.Translate);
}

public sealed partial class TerminalPane : UserControl, IDisposable
{
    private const int MaxChars = 400_000;

    private readonly TerminalSpec _spec;
    private readonly TextBox _output;
    private readonly TextBox _input;
    private readonly TextBlock _title;
    private readonly StringBuilder _buf = new();
    private readonly DispatcherTimer _flush;
    private readonly List<string> _history = [];
    private int _historyAt;
    private IShellSession? _shell;
    private bool _dirty, _hidden, _closed, _crPending;

    // Escape-sequence parser state, kept across chunks because sequences can be split.
    private enum Esc { None, Start, Csi, Osc, OscEsc, Charset }
    private Esc _esc;
    private readonly StringBuilder _seq = new();

    public TerminalPane(TerminalSpec spec, string folder, Action openExternal, Action close)
    {
        _spec = spec;
        Cwd = folder;

        _title = new TextBlock { Text = spec.Title, FontSize = 12, Foreground = Skin.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var external = new Button { Content = "Windows Terminal", FontSize = 11.5, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(external, "Open this in Windows Terminal, for full-screen programs like vim, nano or top");
        external.Click += (_, _) => openExternal();
        var closeBtn = new Button { Content = Glyphs.Icon("close", 14), Margin = new Thickness(4, 0, 0, 0) };
        closeBtn.Classes.Add("icon");
        ToolTip.SetTip(closeBtn, "Close the terminal (Ctrl+`)");
        closeBtn.Click += (_, _) => close();
        var header = new DockPanel { Height = 30, Margin = new Thickness(8, 0, 6, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        right.Children.Add(_extra);
        right.Children.Add(external);
        right.Children.Add(closeBtn);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        // The title shortens itself ("PowerShell  th…") when the buttons need the room in a narrow pane.
        var left = new DockPanel { ClipToBounds = true };
        left.Children.Add(new GlyphIcon { Glyph = "terminal", Width = 14, Height = 14, Brush = Skin.Accent, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(_title);
        header.Children.Add(left);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Skin.Mono,
            FontSize = 12.5,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Skin.Text,
            Padding = new Thickness(10, 4),
            CaretBrush = Brushes.Transparent,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_output, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        // Clicking the output (without selecting) puts you back in the input row.
        _output.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (string.IsNullOrEmpty(_output.SelectedText)) _input?.Focus();
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        _input = new TextBox
        {
            FontFamily = Skin.Mono,
            FontSize = 12.5,
            PlaceholderText = spec.Placeholder,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6),
            InnerLeftContent = new TextBlock { Text = "›", FontFamily = Skin.Mono, FontSize = 14, Foreground = Skin.Accent, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 5, 0, 0) },
            // Pasted scripts keep their lines; the box grows to about ten lines, then scrolls.
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 200,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_input, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);

        var inputRow = new Border { Child = _input, BorderBrush = Skin.PanelBorder, BorderThickness = new Thickness(0, 1, 0, 0) };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(inputRow);
        root.Children.Add(_output);
        Content = new Border { Background = Skin.CodeBg, Child = root };
        InitCommands(right, inputRow);

        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Render());
        _flush.Start();
        _extra.Click += (_, _) => _extraAction?.Invoke();
        _ = ConnectAsync();
    }

    /// <summary>Which shell this is: the connection id, or "local".</summary>
    public string ShellKey => _spec.Key;
    public bool IsLocal => _spec.Local;
    /// <summary>What this terminal is, e.g. "PowerShell  this PC" or "SSH  me@server".</summary>
    public string TitleText => _spec.Title;
    /// <summary>The shell's current folder, as last reported.</summary>
    public string Cwd { get; private set; }
    /// <summary>At a prompt, with nothing typed: safe to send a cd for the file view.</summary>
    public bool IsIdle { get; private set; }
    /// <summary>The shell changed folder (or finished a command in the same one).</summary>
    public event Action<string, bool>? FolderReported;

    public void FocusInput() => _input.Focus();

    /// <summary>
    /// Follows the file view: cd the shell there and show it in the output. If a command is still running, the move
    /// waits for the next prompt (only the latest folder counts).
    /// </summary>
    public void ChangeDirectory(string folder)
    {
        if (_shell is not { IsOpen: true } || SameFolder(folder, Cwd))
        {
            _pendingCd = null;
            return;
        }
        if (!IsIdle)
        {
            _pendingCd = folder;
            return;
        }
        _pendingCd = null;
        var cd = _shell.CdCommand(folder);
        // The SSH terminal echoes the cd itself; PowerShell's is shown here, as if typed.
        if (_shell.NeedsLocalEcho) Append($"PS {Cwd}> cd {folder}\n");
        Cwd = folder;
        IsIdle = false;
        _shell.SendLine(cd);
    }

    private string? _pendingCd;

    // ---- Scripts run on request (Microsoft 365 connections) ------------------------------------------------------------

    private readonly Button _extra = new() { FontSize = 11.5, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private Action? _extraAction;
    private readonly Queue<M365.Step> _queuedSteps = new();

    /// <summary>
    /// Runs these steps in the shell, showing each as it's sent (the shown text never includes secrets). Waits for the
    /// shell to be ready if it's still starting.
    /// </summary>
    public void Run(string note, IEnumerable<M365.Step> steps)
    {
        Append($"# {note}\n");
        foreach (var s in steps) _queuedSteps.Enqueue(s);
        FlushSteps();
    }

    private void FlushSteps()
    {
        if (_shell is not { IsOpen: true } || _hidden || !IsIdle) return;
        while (_queuedSteps.TryDequeue(out var step))
        {
            if (step.Shown.Length > 0) Append($"PS {Cwd}> {step.Shown}\n");
            NoteCommand(step.Shown, remember: false);
            _shell.SendLine(step.Command);
        }
        IsIdle = false;
    }

    /// <summary>An extra header button for this session, e.g. "Sign-in code" opening the one-time-code link.</summary>
    public void SetHeaderAction(string? label, Action? action, string? tip = null)
    {
        _extraAction = action;
        _extra.IsVisible = label is not null;
        _extra.Content = label;
        ToolTip.SetTip(_extra, tip);
    }

    private bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), _spec.Local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task ConnectAsync(bool quiet = false)
    {
        if (!quiet) Append(_spec.Local ? "Starting PowerShell…\n" : $"Connecting…\n");
        try
        {
            var shell = await _spec.Open();
            if (_closed) { shell.Dispose(); return; }
            _shell = shell;
            shell.Output += text => Dispatcher.UIThread.Post(() =>
            {
                if (_shell == shell) Feed(text);
            });
            shell.Closed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_shell != shell) return; // replaced on purpose (Ctrl+C restart)
                Append("\n[session ended — press Enter to start again]\n");
                IsIdle = false;
            });
            _hidden = !quiet;
            shell.Start(Cwd);
            // Shells that don't run PROMPT_COMMAND/precmd never report a folder; stop hiding output after a moment.
            DispatcherTimer.RunOnce(() =>
            {
                if (!_hidden || _shell != shell) return;
                _hidden = false;
                Greet();
                if (!_spec.Local) shell.SendRaw("\r");
            }, TimeSpan.FromSeconds(2.5));
            _input.Focus();
        }
        catch (OperationCanceledException)
        {
            Append("Cancelled. Press Enter to try again.\n");
        }
        catch (Exception ex)
        {
            Append($"Couldn't open a shell: {ex.Message}\nPress Enter to try again.\n");
        }
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (SuggestionKey(e))
        {
            e.Handled = true;
            return;
        }
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.OemTilde && ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            MaximizeRequested?.Invoke();
            return;
        }
        switch (e.Key)
        {
            // Shift+Enter starts another line (the box grows); Enter runs everything typed or pasted.
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                break;
            case Key.Enter:
                e.Handled = true;
                Submit();
                break;
            // In a multi-line command the arrows move between its lines instead of through history.
            case Key.Up or Key.Down when (_input.Text ?? "").Contains('\n'):
                break;
            case Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyAt = Math.Max(0, _historyAt - 1);
                SetInput(_history[_historyAt]);
                break;
            case Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyAt = Math.Min(_history.Count, _historyAt + 1);
                SetInput(_historyAt < _history.Count ? _history[_historyAt] : "");
                break;
            case Key.C when ctrl && string.IsNullOrEmpty(_input.SelectedText):
                e.Handled = true;
                Interrupt();
                break;
            case Key.D when ctrl && !_spec.Local:
                e.Handled = true;
                _shell?.SendRaw("\x04");
                break;
            case Key.L when ctrl:
                e.Handled = true;
                _buf.Clear();
                _dirty = true;
                if (!_spec.Local) _shell?.SendRaw("\r");
                break;
            case Key.Tab:
                e.Handled = true; // keep focus here; completion isn't available in a line-based terminal
                break;
        }
    }

    private void SetInput(string text)
    {
        _suppressSuggest = true;
        _input.Text = text;
        _suppressSuggest = false;
        _suggestPopup.IsOpen = false;
        _input.CaretIndex = text.Length;
    }

    /// <summary>Ctrl+C: stop the running command; a local PowerShell can't take it, so it's restarted in place.</summary>
    private void Interrupt()
    {
        if (_shell is null || _shell.Interrupt()) return;
        Append("^C\n");
        var old = _shell;
        _shell = null;
        old.Dispose();
        _ = ConnectAsync(quiet: true);
    }

    private void Submit()
    {
        var line = _input.Text ?? "";
        _input.Text = "";
        if (_shell is not { IsOpen: true })
        {
            _shell?.Dispose();
            _shell = null;
            _ = ConnectAsync();
            return;
        }

        var secret = _input.PasswordChar != '\0';
        if (secret)
        {
            _input.PasswordChar = '\0';
            _shell.SendRaw(line + (_spec.Local ? "" : "\r"));
            return;
        }

        if (line.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != line)) _history.Add(line);
        _historyAt = _history.Count;
        _suggestPopup.IsOpen = false;
        line = line.Replace("\r\n", "\n").TrimEnd('\n');
        if (line.Contains('\n'))
        {
            SubmitBlock(line);
            return;
        }
        var t = _spec.Translate(line);
        NoteCommand(line, remember: true);
        if (t.Command != line) NoteCommand(t.Command, remember: false);
        if (t.ClearScreen)
        {
            _buf.Clear();
            _dirty = true;
            if (!_spec.Local) _shell.SendRaw("\r");
            return;
        }
        if (_shell.NeedsLocalEcho) Append($"PS {Cwd}> {line}\n");
        if (t.Note is not null) Append($"# {t.Note}\n");
        IsIdle = false;
        _shell.SendLine(t.Command);
    }

    /// <summary>
    /// A pasted (or Shift+Enter) multi-line command runs as one unit, exactly as written: a PowerShell script block
    /// with its loops and braces intact, or a series of lines for the SSH shell. Windows/Linux translation only
    /// applies to single lines.
    /// </summary>
    private void SubmitBlock(string block)
    {
        if (_shell is null) return;
        if (block.Length <= 2000) NoteCommand(block, remember: true);
        foreach (var l in block.Split('\n')) NoteCommand(l, remember: false);
        if (_shell.NeedsLocalEcho)
        {
            var lines = block.Split('\n');
            Append($"PS {Cwd}> {lines[0]}\n");
            foreach (var l in lines.Skip(1)) Append($">> {l}\n");
        }
        IsIdle = false;
        _shell.SendBlock(block);
    }

    // ---- Output ---------------------------------------------------------------------------------------------------------

    private void Feed(string text)
    {
        foreach (var ch in text)
        {
            switch (_esc)
            {
                case Esc.Start:
                    _esc = ch switch { '[' => Esc.Csi, ']' => Esc.Osc, '(' or ')' => Esc.Charset, _ => Esc.None };
                    _seq.Clear();
                    continue;
                case Esc.Charset:
                    _esc = Esc.None;
                    continue;
                case Esc.Csi:
                    _seq.Append(ch);
                    if (ch >= '@' && ch <= '~')
                    {
                        _esc = Esc.None;
                        var s = _seq.ToString();
                        if (s is "?1049h" or "?47h" or "?1047h")
                            Append("\n[This program needs a full-screen terminal. Press Ctrl+C to stop it, or use “Open in Windows Terminal”.]\n");
                        else if (s is "2J" or "H2J") { _buf.Clear(); _dirty = true; }
                    }
                    continue;
                case Esc.Osc:
                    if (ch == '\a') { EndOsc(); continue; }
                    if (ch == '\x1b') { _esc = Esc.OscEsc; continue; }
                    _seq.Append(ch);
                    continue;
                case Esc.OscEsc:
                    EndOsc();
                    continue;
            }

            switch (ch)
            {
                case '\x1b':
                    _esc = Esc.Start;
                    break;
                case '\r':
                    _crPending = true;
                    break;
                case '\n':
                    _crPending = false;
                    Put('\n');
                    break;
                case '\b':
                    if (!_hidden && _buf.Length > 0 && _buf[^1] != '\n') { _buf.Length--; _dirty = true; }
                    break;
                case '\t':
                    Put('\t');
                    break;
                default:
                    if (ch < ' ') break;
                    if (_crPending)
                    {
                        // A bare carriage return redraws the line (progress bars): drop what's on it.
                        _crPending = false;
                        if (!_hidden)
                        {
                            var nl = LastNewline();
                            _buf.Length = nl + 1;
                        }
                    }
                    Put(ch);
                    break;
            }
        }
        if (!_hidden) UpdatePasswordMode();
    }

    private void EndOsc()
    {
        _esc = Esc.None;
        var s = _seq.ToString();
        if (!s.StartsWith("7;", StringComparison.Ordinal)) return;
        var folder = s[2..];
        if (folder.StartsWith("file://", StringComparison.Ordinal))
        {
            var slash = folder.IndexOf('/', 7);
            folder = slash < 0 ? "/" : Uri.UnescapeDataString(folder[slash..]);
        }
        if (_hidden)
        {
            _hidden = false;
            Greet();
        }
        // PowerShell has no visible prompt here; keep each command's output on its own lines.
        else if (_spec.Local && _buf.Length > 0 && _buf[^1] != '\n') Append("\n");
        var changed = !SameFolder(folder, Cwd);
        Cwd = folder;
        IsIdle = true;
        FolderReported?.Invoke(folder, changed);
        // The file view moved while a command was running: catch up now that the shell is at a prompt.
        if (_queuedSteps.Count > 0)
        {
            FlushSteps();
            return;
        }
        if (_pendingCd is { } pending && !changed) ChangeDirectory(pending);
        else _pendingCd = null;
    }

    private void Greet()
    {
        _buf.Clear();
        Append(_spec.Greeting + "\n\n");
    }

    private int LastNewline()
    {
        for (var i = _buf.Length - 1; i >= 0; i--)
            if (_buf[i] == '\n') return i;
        return -1;
    }

    private void Put(char ch)
    {
        if (_hidden) return;
        _buf.Append(ch);
        _dirty = true;
    }

    private void Append(string text)
    {
        _buf.Append(text);
        _dirty = true;
    }

    /// <summary>sudo/ssh asking for a password: mask what's typed and keep it out of the history.</summary>
    private void UpdatePasswordMode()
    {
        var tail = _buf.Length - (LastNewline() + 1);
        var line = tail > 0 ? _buf.ToString(_buf.Length - tail, Math.Min(tail, 200)) : "";
        _input.PasswordChar = PasswordPrompt().IsMatch(line) ? '•' : '\0';
        _input.PlaceholderText = _input.PasswordChar != '\0'
            ? "Password (sent to the server, never saved)"
            : _spec.Placeholder;
    }

    private void Render()
    {
        if (!_dirty) return;
        _dirty = false;
        if (_buf.Length > MaxChars) _buf.Remove(0, _buf.Length - MaxChars * 3 / 4);
        _output.Text = _buf.ToString();
        _output.CaretIndex = _output.Text.Length;
    }

    public void Dispose()
    {
        _closed = true;
        _flush.Stop();
        _shell?.Dispose();
        _shell = null;
    }

    [GeneratedRegex(@"(password|passphrase|passcode)[^\n]*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();
}
