using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using pxdCommander.Core;

namespace pxdCommander.Views;

/// <summary>
/// The install / uninstall screen shown when the program runs as pxdcomm-setup.exe (or with --install / --uninstall).
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly bool _uninstall;

    public SetupWindow(bool uninstall)
    {
        _uninstall = uninstall;
        Title = uninstall ? "Uninstall pxd Commander" : "Install pxd Commander";
        Icon = AppIcon.CreateWindowIcon();
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#0E1117"));

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                AppIcon.Mark(72),
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = "pxd Commander", Classes = { "neon" }, FontSize = 24, FontWeight = FontWeight.Bold },
                        new TextBlock { Text = $"Version {Installer.Version}", Foreground = Skin.Muted, FontSize = 12.5 },
                        new TextBlock { Text = "Dual-pane file manager with FTP, SFTP and a built-in terminal", Foreground = Skin.Faint, FontSize = 12.5 },
                    },
                },
            },
        };
        Content = new Border
        {
            Padding = new Thickness(28, 24),
            Child = new StackPanel { Spacing = 22, Children = { header, _body } },
        };

        if (uninstall) ShowUninstall();
        else ShowInstall();
    }

    private static Button Primary(string text)
    {
        var b = new Button { Content = text, MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Classes.Add("primary");
        return b;
    }

    private static StackPanel Buttons(params Control[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        row.Children.AddRange(buttons);
        return row;
    }

    // ---- Install ---------------------------------------------------------------------------------------------------------

    private void ShowInstall()
    {
        var existing = Installer.InstalledFolder ?? Migration.FormerInstalledFolder;
        var folder = new TextBox { Text = Installer.SuggestedFolder, FontSize = 12.5 };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Install pxd Commander in…" });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p) folder.Text = Path.Combine(p, Installer.AppName);
        };
        var folderRow = new DockPanel { Children = { browse, folder } };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);

        var desktop = new CheckBox { Content = "Desktop shortcut", IsChecked = true };
        var menu = new CheckBox { Content = "“Open in pxd Commander” when you right-click a folder", IsChecked = true };
        var note = new TextBlock
        {
            Text = existing is null
                ? "Installs for your Windows account only — no administrator rights needed. Uninstall any time from Settings ▸ Apps."
                : $"Version {Installer.InstalledVersion ?? Migration.FormerInstalledVersion ?? "?"} is installed. Updating keeps your settings, connections and saved passwords.",
            Foreground = Skin.Faint,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var install = Primary(existing is null ? "Install" : "Update");
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        _body.Children.Add(new TextBlock { Text = "Install folder", Foreground = Skin.Muted, FontSize = 12 });
        _body.Children.Add(folderRow);
        _body.Children.Add(desktop);
        _body.Children.Add(menu);
        _body.Children.Add(note);
        _body.Children.Add(Buttons(cancel, install));

        install.Click += async (_, _) =>
        {
            var target = folder.Text?.Trim();
            if (string.IsNullOrEmpty(target)) return;
            var status = new TextBlock { Foreground = Skin.Muted, FontSize = 12.5 };
            _body.Children.Clear();
            _body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4 });
            _body.Children.Add(status);
            try
            {
                var exe = await Installer.InstallAsync(target, desktop.IsChecked == true, menu.IsChecked == true, s => status.Text = s);
                ShowInstalled(exe, existing is not null);
            }
            catch (Exception ex)
            {
                _body.Children.Clear();
                _body.Children.Add(new TextBlock { Text = "Installation didn't finish: " + ex.Message, Foreground = Skin.Bad, TextWrapping = TextWrapping.Wrap });
                var close = new Button { Content = "Close" };
                close.Click += (_, _) => Close();
                _body.Children.Add(Buttons(close));
            }
        };
    }

    private void ShowInstalled(string exe, bool updated)
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock
        {
            Text = updated ? "pxd Commander is up to date." : "pxd Commander is installed.",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        });
        _body.Children.Add(new TextBlock
        {
            Text = "Find it in the Start menu. Pin it to the taskbar from there or from its window.",
            Foreground = Skin.Muted,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        });
        var launch = new CheckBox { Content = "Open pxd Commander now", IsChecked = true };
        _body.Children.Add(launch);
        var finish = Primary("Finish");
        finish.Click += (_, _) =>
        {
            if (launch.IsChecked == true)
            {
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); }
                catch { }
            }
            Close();
        };
        _body.Children.Add(Buttons(finish));
    }

    // ---- Uninstall -------------------------------------------------------------------------------------------------------

    private void ShowUninstall()
    {
        var wipe = new CheckBox { Content = "Also remove my settings, saved connections and their passwords", IsChecked = false };
        var go = Primary("Uninstall");
        go.Classes.Add("danger");
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        _body.Children.Add(new TextBlock { Text = "Remove pxd Commander from this PC?", FontSize = 16, FontWeight = FontWeight.SemiBold });
        _body.Children.Add(new TextBlock
        {
            Text = "Shortcuts and the folder right-click entry are removed too. Your own files are never touched.",
            Foreground = Skin.Muted,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(wipe);
        _body.Children.Add(Buttons(cancel, go));
        go.Click += (_, _) =>
        {
            try
            {
                Installer.Uninstall(wipe.IsChecked == true);
                _body.Children.Clear();
                _body.Children.Add(new TextBlock { Text = "pxd Commander has been removed.", FontSize = 16, FontWeight = FontWeight.SemiBold });
                var close = Primary("Close");
                close.Click += (_, _) => Close();
                _body.Children.Add(Buttons(close));
            }
            catch (Exception ex)
            {
                _body.Children.Add(new TextBlock { Text = "Couldn't finish: " + ex.Message, Foreground = Skin.Bad, TextWrapping = TextWrapping.Wrap });
            }
        };
    }
}
