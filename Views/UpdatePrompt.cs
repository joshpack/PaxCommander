using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdCommander.Core;

namespace pxdCommander.Views;

/// <summary>
/// "A new version is out" card for "Check for updates" (on demand): shows what's new and installs when the user clicks
/// Update now. Start-up no longer prompts: a newer release installs before the app opens (Updater.BeforeStart).
/// </summary>
public static class UpdatePrompt
{
    private static bool _busy;

    /// <summary>For tests: the last update the card offered.</summary>
    public static UpdateInfo? LastOffered { get; private set; }

    /// <summary>
    /// After start-up: removes what the last update left behind. Updates themselves install before the app opens
    /// (Updater.BeforeStart, no prompt), so nothing is offered here.
    /// </summary>
    public static Task CheckOnStartAsync(Window window)
    {
        // Test runs (their own data folder) leave the real temp folders alone.
        if (Environment.GetEnvironmentVariable("PXDCMD_DATA") is { Length: > 0 }) return Task.CompletedTask;
        return Task.Run(() =>
        {
            Updater.CleanUp();
            Updater.CleanUpOldExe();
        });
    }

    public static async Task CheckNowAsync(Window window)
    {
        if (_busy) return;
        Toasts.Show("Checking for updates…", seconds: 2);
        UpdateInfo? info;
        try
        {
            info = await Updater.CheckAsync();
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't check for updates: " + ex.Message, ToastKind.Error);
            return;
        }
        AppSettings.Current.LastUpdateCheck = DateTime.UtcNow;
        AppSettings.SaveSoon();
        if (info is null) Toasts.Show($"You're on the latest version ({Installer.Version}).", ToastKind.Success);
        else await ShowAsync(window, info);
    }

    public static async Task ShowAsync(Window window, UpdateInfo info)
    {
        if (_busy) return;
        _busy = true;
        LastOffered = info;
        try
        {
            var result = await Dialogs.ShowAsync<string>(tcs => Build(tcs, info), "later", width: 520, focusInput: false);
            if (result == "skip")
            {
                AppSettings.Current.SkippedUpdate = info.Version;
                AppSettings.SaveSoon();
            }
            else if (result == "close")
            {
                // The installer is waiting for us to close so it can replace the program, then reopens it.
                Dispatcher.UIThread.Post(window.Close, DispatcherPriority.Background);
            }
            else if (result == "opened")
                Toasts.Show("The pxd Commander installer is open.", ToastKind.Info);
        }
        finally
        {
            _busy = false;
        }
    }

    private static Control Build(TaskCompletionSource<string> tcs, UpdateInfo info)
    {
        var notes = new Border
        {
            Background = Skin.CodeBg,
            BorderBrush = Skin.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            IsVisible = info.Notes.Length > 0,
            Child = new ScrollViewer
            {
                MaxHeight = 220,
                Content = new TextBlock { Text = info.Notes, TextWrapping = TextWrapping.Wrap, Foreground = Skin.Text, LineHeight = 19 },
            },
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, IsVisible = false, Margin = new Thickness(0, 0, 0, 8) };
        var status = new TextBlock { Foreground = Skin.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 6) };
        var mb = info.Size > 0 ? $" ({info.Size / 1048576.0:0.#} MB)" : "";
        var cts = new CancellationTokenSource();
        var downloading = false;

        var skip = Dialogs.Btn("Skip this version", null, () => tcs.TrySetResult("skip"));
        var later = Dialogs.Btn("Later", null, () =>
        {
            if (downloading) cts.Cancel();
            tcs.TrySetResult("later");
        });
        Button update = null!;
        update = Dialogs.Btn("Update now", "primary", async () =>
        {
            if (downloading) return;
            downloading = true;
            update.IsEnabled = skip.IsEnabled = false;
            later.Content = "Cancel";
            bar.IsVisible = status.IsVisible = true;
            status.Text = "Downloading" + mb + "…";
            try
            {
                var progress = new Progress<double>(v =>
                {
                    bar.Value = v;
                    status.Text = $"Downloading{mb}… {v:P0}";
                });
                var setup = await Updater.DownloadAsync(info, progress, cts.Token);
                status.Text = "Checksum OK. Installing — pxd Commander will close and reopen in a moment.";
                later.IsEnabled = false;
                await Task.Delay(500);
                tcs.TrySetResult(Updater.Launch(setup) ? "close" : "opened");
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult("later");
            }
            catch (Exception ex)
            {
                downloading = false;
                status.Text = "Update failed: " + ex.Message;
                status.Foreground = Skin.Bad;
                bar.IsVisible = false;
                later.Content = "Close";
                update.IsEnabled = true;
                update.Content = "Try again";
            }
        });

        var page = new TextBlock
        {
            Text = "Release page",
            Foreground = Skin.Accent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        page.PointerPressed += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); } catch { }
        };
        var buttons = Dialogs.Buttons(skip, later, update);
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Margin = new Thickness(0);
        footer.Children.Add(buttons);
        footer.Children.Add(page);

        return new StackPanel
        {
            Children =
            {
                Dialogs.Title($"pxd Commander {info.Version} is available"),
                Dialogs.Body($"You have {Installer.Version}. Here's what's new:"),
                notes,
                bar,
                status,
                footer,
            },
        };
    }
}
