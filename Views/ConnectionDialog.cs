using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PaxCommander.Core;
using PaxCommander.Core.Remote;

namespace PaxCommander.Views;

/// <summary>Add or edit a saved SFTP/FTP connection, with a Test button. Secrets go to Windows Credential Manager.</summary>
public static class ConnectionDialog
{
    private static readonly (string Label, RemoteProtocol Protocol)[] Protocols =
    [
        ("SFTP (SSH)", RemoteProtocol.Sftp),
        ("FTPS (FTP over TLS)", RemoteProtocol.Ftps),
        ("FTP (unencrypted)", RemoteProtocol.Ftp),
    ];

    public static Task<Connection?> ShowAsync(Connection? existing) => Dialogs.ShowAsync<Connection?>(tcs =>
    {
        var editing = existing is not null && Connections.Find(existing.Id) is not null;
        var c = existing?.Clone() ?? new Connection();
        var savedSecret = editing ? Connections.GetSecret(c) : null;

        var name = new TextBox { Text = c.Name, PlaceholderText = "My server" };
        var protocol = new ComboBox { ItemsSource = Protocols.Select(p => p.Label).ToList(), SelectedIndex = Array.FindIndex(Protocols, p => p.Protocol == c.Protocol), HorizontalAlignment = HorizontalAlignment.Stretch };
        var host = new TextBox { Text = c.Host, PlaceholderText = "server.example.com or 10.0.0.5" };
        var port = new TextBox { Text = c.Port > 0 ? c.Port.ToString() : "", PlaceholderText = "22" };
        var user = new TextBox { Text = c.User, PlaceholderText = "username" };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = savedSecret is not null ? "•••••• (saved — leave empty to keep)" : "password" };
        var remember = new CheckBox { Content = "Remember password in Windows Credential Manager", IsChecked = savedSecret is not null || !editing };
        var keyFile = new TextBox { Text = c.KeyFile, PlaceholderText = "optional: C:\\Users\\you\\.ssh\\id_ed25519" };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        var start = new TextBox { Text = c.StartPath, PlaceholderText = "optional: /home/you (default: wherever the server starts you)" };
        var warning = new TextBlock { Foreground = Skin.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var keyRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        keyRow.Children.Add(browse);
        keyRow.Children.Add(keyFile);
        var keyBlock = Field("PRIVATE KEY (SFTP)", keyRow);

        void ProtocolChanged()
        {
            var p = Protocols[Math.Max(0, protocol.SelectedIndex)].Protocol;
            port.PlaceholderText = p == RemoteProtocol.Sftp ? "22" : "21";
            keyBlock.IsVisible = p == RemoteProtocol.Sftp;
            warning.Text = p == RemoteProtocol.Ftp ? "Plain FTP sends your password and files unencrypted. Prefer SFTP or FTPS when the server supports it." : "";
            warning.IsVisible = warning.Text.Length > 0;
            password.PlaceholderText = p == RemoteProtocol.Sftp && keyFile.Text?.Length > 0 ? "key passphrase (if any)" : savedSecret is not null ? "•••••• (saved — leave empty to keep)" : "password";
        }
        protocol.SelectionChanged += (_, _) => ProtocolChanged();
        keyFile.TextChanged += (_, _) => ProtocolChanged();
        ProtocolChanged();

        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(browse)?.StorageProvider is not { } sp) return;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Private key", AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } p) keyFile.Text = p;
        };

        Connection? Read(out string? error)
        {
            error = null;
            var h = (host.Text ?? "").Trim();
            if (h.Length == 0) error = "Enter the server's address.";
            var portText = (port.Text ?? "").Trim();
            var portValue = 0;
            if (portText.Length > 0 && (!int.TryParse(portText, out portValue) || portValue is < 1 or > 65535)) error = "The port must be a number from 1 to 65535.";
            if (error is not null) return null;
            var result = c.Clone();
            result.Name = (name.Text ?? "").Trim();
            result.Protocol = Protocols[Math.Max(0, protocol.SelectedIndex)].Protocol;
            result.Host = h;
            result.Port = portValue;
            result.User = (user.Text ?? "").Trim();
            result.KeyFile = result.Protocol == RemoteProtocol.Sftp ? (keyFile.Text ?? "").Trim() : "";
            result.StartPath = (start.Text ?? "").Trim();
            if (result.Host != c.Host || result.EffectivePort != c.EffectivePort) result.HostKey = result.CertThumbprint = null;
            return result;
        }

        string? Secret() => string.IsNullOrEmpty(password.Text) ? savedSecret : password.Text;

        var test = Dialogs.Btn("Test", null);
        test.Click += async (_, _) =>
        {
            if (Read(out var err) is not { } candidate)
            {
                status.Text = err;
                status.Foreground = Skin.Bad;
                return;
            }
            status.Text = "Connecting…";
            status.Foreground = Skin.Muted;
            test.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await RemoteSessions.TestAsync(candidate, Secret(), cts.Token);
                c.HostKey = candidate.HostKey;
                c.CertThumbprint = candidate.CertThumbprint;
                status.Text = "✓ Connected and listed the start folder.";
                status.Foreground = Skin.Good;
            }
            catch (Exception ex)
            {
                status.Text = "✗ " + ex.Message;
                status.Foreground = Skin.Bad;
            }
            finally
            {
                test.IsEnabled = true;
            }
        };

        var save = Dialogs.Btn(editing ? "Save" : "Save and connect", "primary", () =>
        {
            if (Read(out var err) is not { } result)
            {
                status.Text = err;
                status.Foreground = Skin.Bad;
                return;
            }
            // The test may have trusted a host key or certificate; keep it.
            result.HostKey ??= c.HostKey;
            result.CertThumbprint ??= c.CertThumbprint;
            Connections.Save(result);
            if (remember.IsChecked == true)
            {
                if (!string.IsNullOrEmpty(password.Text)) Connections.SetSecret(result, password.Text);
            }
            else Connections.SetSecret(result, null);
            tcs.TrySetResult(result);
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,110") };
        grid.Children.Add(Field("SERVER", host));
        var portField = Field("PORT", port);
        Grid.SetColumn(portField, 2);
        grid.Children.Add(portField);

        return new ScrollViewer
        {
            MaxHeight = 680,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Dialogs.Title(editing ? $"Edit {c.Display}" : "New connection"),
                    Field("NAME", name),
                    Field("TYPE", protocol),
                    warning,
                    grid,
                    Field("USERNAME", user),
                    Field("PASSWORD", password),
                    remember,
                    keyBlock,
                    Field("START FOLDER", start),
                    new TextBlock
                    {
                        Text = "Passwords are kept in Windows Credential Manager, encrypted for your Windows account — never in PAX Commander's own files. SSH servers are checked against their saved fingerprint on every connect.",
                        FontSize = 11.5,
                        Foreground = Skin.Faint,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    status,
                    Dialogs.Buttons(Dialogs.Btn("Cancel", null, () => tcs.TrySetResult(null)), test, save),
                },
            },
        };
    }, null, width: 520);

    private static StackPanel Field(string label, Control input) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, Classes = { "label" } }, input },
    };
}
