using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using pxdCommander.Platform.Win32;

namespace pxdCommander.Views;

/// <summary>
/// Map network drive: a drive letter for \\server\share, optionally reconnecting at sign-in and with a different
/// account. "Remember my credentials" saves them in Windows Credential Manager, exactly like Explorer does.
/// </summary>
public static class MapDriveDialog
{
    /// <summary>The mapped drive's root (e.g. "Z:\"), or null when cancelled.</summary>
    public static Task<string?> ShowAsync(string? share) => Dialogs.ShowAsync<string?>(tcs =>
    {
        var letters = NetworkBrowse.FreeLetters();
        var letter = new ComboBox { ItemsSource = letters, SelectedIndex = letters.Count > 0 ? 0 : -1, Width = 90 };
        var folder = new TextBox { Text = share ?? "", PlaceholderText = @"\\server\share" };
        var reconnect = new CheckBox { Content = "Reconnect at sign-in", IsChecked = true };
        var other = new CheckBox { Content = "Connect using a different account", IsChecked = false };
        var user = new TextBox { PlaceholderText = @"DOMAIN\user, server\user or user@domain" };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "password" };
        var remember = new CheckBox { Content = "Remember my credentials (Windows Credential Manager)", IsChecked = true };
        var creds = new StackPanel { Spacing = 8, IsVisible = false, Margin = new Thickness(24, 0, 0, 0), Children = { Field("USERNAME", user), Field("PASSWORD", password), remember } };
        other.IsCheckedChanged += (_, _) => creds.IsVisible = other.IsChecked == true;
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };

        Button? map = null;
        map = Dialogs.Btn("Map", "primary", async () =>
        {
            var target = (folder.Text ?? "").Trim().Replace('/', '\\');
            if (!target.StartsWith(@"\\", StringComparison.Ordinal) || target.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                status.Text = @"Enter the shared folder as \\server\share.";
                status.Foreground = Skin.Bad;
                return;
            }
            if (letter.SelectedItem is not string drive)
            {
                status.Text = "No drive letters are free.";
                status.Foreground = Skin.Bad;
                return;
            }
            var withAccount = other.IsChecked == true && (user.Text ?? "").Trim().Length > 0;
            status.Text = "Connecting…";
            status.Foreground = Skin.Muted;
            map!.IsEnabled = false;
            try
            {
                var u = withAccount ? user.Text!.Trim() : null;
                var p = withAccount ? password.Text ?? "" : null;
                await Task.Run(() => NetworkBrowse.MapDrive(drive, target, reconnect.IsChecked == true, u, p, withAccount && remember.IsChecked == true));
                tcs.TrySetResult(drive + "\\");
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                status.Foreground = Skin.Bad;
                map.IsEnabled = true;
            }
        });

        var row = new DockPanel();
        var letterField = Field("DRIVE", letter);
        letterField.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(letterField, Dock.Left);
        row.Children.Add(letterField);
        row.Children.Add(Field("FOLDER", folder));

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Dialogs.Title("Map network drive"),
                row,
                new TextBlock { Text = @"For example \\nas\media or \\192.168.1.20\backups. Open Network in the sidebar to browse what's shared.", FontSize = 11.5, Foreground = Skin.Faint, TextWrapping = TextWrapping.Wrap },
                reconnect,
                other,
                creds,
                status,
                Dialogs.Buttons(Dialogs.Btn("Cancel", null, () => tcs.TrySetResult(null)), map),
            },
        };
    }, null, width: 520);

    private static StackPanel Field(string label, Control input) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, Classes = { "label" } }, input },
    };
}
