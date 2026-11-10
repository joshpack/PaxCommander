using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using pxdCommander.Core;
using pxdCommander.Core.Remote;

namespace pxdCommander.Views;

/// <summary>SFTP/FTP: saved connections, prompts, and file operations that involve a server.</summary>
public sealed partial class MainWindow
{
    private static bool _promptsWired;

    /// <summary>The questions a connection can ask (password, unknown host key, bad certificate), shown as dialogs.</summary>
    private static void WirePrompts()
    {
        if (_promptsWired) return;
        _promptsWired = true;
        RemotePrompts.AskSecret = (c, what) => Dispatcher.UIThread.InvokeAsync(() => AskSecretAsync(c, what));
        RemotePrompts.TrustHostKey = (c, fp, previous) => Dispatcher.UIThread.InvokeAsync(() => previous is null
            ? Dialogs.ConfirmAsync($"Trust {c.Host}?",
                $"First connection to this server. Its SSH key fingerprint is\n\n{fp}\n\nIf you're not sure this is right, check it with the server's owner. It's remembered from now on.", "Trust and connect", queue: false)
            : Dialogs.ConfirmAsync($"{c.Host}'s identity CHANGED",
                $"The server's SSH key is different from last time:\n\nwas  {previous}\nnow  {fp}\n\nThis happens after a server reinstall — or when someone is intercepting the connection. Only continue if you know why it changed.",
                "Trust the new key", danger: true, queue: false));
        RemotePrompts.TrustCertificate = (c, summary, errors) => Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(
            $"{c.Host}'s certificate isn't valid",
            $"{summary}\n\nProblem: {errors}\n\nSelf-signed certificates are common on home servers. Accepting remembers this exact certificate.",
            "Accept this certificate", danger: true, queue: false));
    }

    private static Task<(string Secret, bool Remember)?> AskSecretAsync(Connection c, string what) =>
        Dialogs.ShowAsync<(string, bool)?>(tcs =>
        {
            var box = new TextBox { PasswordChar = '●', RevealPassword = false };
            var remember = new CheckBox
            {
                Content = "Remember it (stored in Windows Credential Manager)",
                IsChecked = Connections.Find(c.Id) is not null,
                IsEnabled = Connections.Find(c.Id) is not null,
                Margin = new Thickness(0, 10, 0, 0),
            };
            void Ok() => tcs.TrySetResult((box.Text ?? "", remember.IsChecked == true));
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) Ok();
            };
            return new StackPanel
            {
                Children =
                {
                    Dialogs.Title($"{what} for {c.Display}"),
                    Dialogs.Body($"{c.User}@{c.Host}:{c.EffectivePort} ({c.Scheme.ToUpperInvariant()})"),
                    box,
                    remember,
                    Dialogs.Buttons(Dialogs.Btn("Cancel", null, () => tcs.TrySetResult(null)), Dialogs.Btn("Connect", "primary", Ok)),
                },
            };
        }, null, queue: false);

    // ---- Connections ---------------------------------------------------------------------------------------------------

    public void OpenConnection(Connection c, bool newTab = false)
    {
        // Saved start folder, else wherever the server puts us after login.
        _ = Task.Run(async () =>
        {
            string folder;
            if (c.StartPath.Length > 0) folder = c.StartPath;
            else
            {
                try
                {
                    folder = (await RemoteSessions.GetAsync(c)).HomePath;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Toasts.Show($"Couldn't connect to {c.Display}: {ex.Message}", ToastKind.Error, seconds: 8);
                    return;
                }
            }
            var path = RemotePath.Make(c, folder);
            Dispatcher.UIThread.Post(() =>
            {
                if (newTab) OpenInNewTab(path, false);
                else if (CurrentView is { } v)
                {
                    // An SSH server opens as one view: its files above, the terminal below. No second pane or columns.
                    if (c.Protocol == RemoteProtocol.Sftp)
                    {
                        if (AppSettings.Current.DualPane) ShowOnlyPane(_activePane);
                        if (v.Mode == ViewMode.Columns) v.SetMode(ViewMode.Details);
                    }
                    _ = v.NavigateAsync(path);
                    v.FocusList();
                }
            });
        });
    }

    private async Task EditConnectionAsync(Connection? existing, Connection? prefill = null)
    {
        var saved = await ConnectionDialog.ShowAsync(existing ?? prefill);
        if (saved is null) return;
        RemoteSessions.Forget(saved.Id);
        _sidebar.Rebuild();
        if (existing is null) OpenConnection(saved);
    }

    private async Task DeleteConnectionAsync(Connection c)
    {
        if (!await Dialogs.ConfirmAsync($"Delete the connection “{c.Display}”?", "Its saved password is removed from Windows Credential Manager too.", "Delete", danger: true))
            return;
        RemoteSessions.Forget(c.Id);
        Connections.Delete(c);
        _sidebar.Rebuild();
    }

    public void OpenExternalTerminal(Connection connection) => OpenSshTerminal(connection);

    /// <summary>An SSH shell in Windows Terminal (or PowerShell) using the system's OpenSSH client.</summary>
    private static void OpenSshTerminal(Connection c)
    {
        var args = new List<string> { "ssh" };
        if (c.Port > 0 && c.Port != 22) args.AddRange(["-p", c.Port.ToString()]);
        if (c.KeyFile.Length > 0) args.AddRange(["-i", c.KeyFile]);
        args.Add(c.User.Length > 0 ? $"{c.User}@{c.Host}" : c.Host);
        foreach (var exe in new[] { "wt.exe", "pwsh.exe", "powershell.exe" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
                if (exe != "wt.exe") psi.ArgumentList.Add("-NoExit");
                if (exe != "wt.exe") psi.ArgumentList.Add("-Command");
                if (exe == "wt.exe") foreach (var a in args) psi.ArgumentList.Add(a);
                else psi.ArgumentList.Add(string.Join(' ', args.Select(a => a.Contains(' ') ? $"'{a}'" : a)));
                Process.Start(psi);
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    // ---- File operations on servers ------------------------------------------------------------------------------------

    private void TrackRemote(RemoteJob job, FolderView? selectIn, string? destination)
    {
        job.Finished += j => Dispatcher.UIThread.Post(() =>
        {
            if (j.State == Ops.JobState.Failed)
                Toasts.Show($"{j.Title} failed: {j.ErrorMessage}", ToastKind.Error, seconds: 8);
            else if (j.ProblemCount > 0)
                Toasts.Show($"{j.Title}: {Formatting.Count(j.ProblemCount, "item")} had problems — {j.FirstProblem}", ToastKind.Error, seconds: 8);
            // Servers send no change notifications: refresh every tab looking at a remote folder.
            foreach (var v in Views().Where(v => PathUtil.IsRemote(v.ActiveFolder) || (destination is not null && PathUtil.Same(v.ActiveFolder, destination))))
                _ = v.ActiveListing?.RefreshAsync();
            if (selectIn is not null && j.Created.FirstOrDefault() is { } first)
                selectIn.SelectWhenItAppears(PathUtil.IsRemote(first) ? RemotePath.Name(first) : Path.GetFileName(first));
        });
        _ops.Add(job);
    }

    private bool StartRemoteTransfer(IReadOnlyList<string> paths, string destination, bool move, FolderView? selectIn)
    {
        if (!paths.Any(PathUtil.IsRemote) && !PathUtil.IsRemote(destination)) return false;
        if (paths.Count == 0) return true;
        if (paths.Select(p => PathUtil.IsRemote(p) ? RemotePath.Prefix(p) : "").Distinct().Count() > 1)
        {
            Toasts.Show("Copy from one place at a time.", ToastKind.Error);
            return true;
        }
        TrackRemote(RemoteTransfers.Transfer(paths, destination, move, new JobUi()), selectIn, destination);
        return true;
    }

    private async Task<bool> DeleteRemoteAsync(IReadOnlyList<FileItem> items)
    {
        if (!items.Any(i => i.IsRemote)) return false;
        var paths = items.Select(i => i.Path).ToList();
        var server = RemotePath.TryParse(paths[0], out var c, out _) && c is not null ? c.Display : "the server";
        var what = paths.Count == 1 ? $"“{items[0].Name}”" : Formatting.Count(paths.Count, "item");
        if (!await Dialogs.ConfirmAsync($"Delete {what} from {server}?", "Servers have no Recycle Bin, so this is permanent.", "Delete permanently", danger: true))
            return true;
        TrackRemote(RemoteTransfers.Delete(paths), null, null);
        return true;
    }

    private async Task<bool> RenameRemoteAsync(FolderView view, FileItem item, string newName)
    {
        try
        {
            await RemoteTransfers.RenameAsync(item.Path, newName);
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't rename: " + ex.Message, ToastKind.Error);
            return false;
        }
        await (view.ActiveListing?.RefreshAsync() ?? Task.CompletedTask);
        view.SelectWhenItAppears(newName);
        return true;
    }

    private async Task NewRemoteAsync(FolderView view, string folder, string baseName, string ext, bool isFolder)
    {
        try
        {
            var name = await RemoteTransfers.CreateAsync(folder, baseName, ext, isFolder);
            await (view.ActiveListing?.RefreshAsync() ?? Task.CompletedTask);
            view.SelectWhenItAppears(name, rename: true);
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't create it on the server: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Opens a server file: a copy is downloaded to a temp folder and opened with its usual app.</summary>
    private async Task OpenRemoteFilesAsync(IReadOnlyList<FileItem> files)
    {
        foreach (var f in files.Take(10))
        {
            try
            {
                Toasts.Show($"Downloading {f.Name}…", seconds: 1.5);
                var local = await RemoteSessions.DownloadToTempAsync(f.Path, CancellationToken.None);
                Shell.Open(local);
            }
            catch (Exception ex)
            {
                Toasts.Show($"Couldn't open {f.Name}: {ex.Message}", ToastKind.Error);
            }
        }
        Toasts.Show("Opened downloaded copies — edits aren't sent back to the server automatically.", seconds: 4);
    }

    private IEnumerable<Control> RemoteItemMenu(FolderView view, IReadOnlyList<FileItem> items)
    {
        var first = items[0];
        yield return Item("Open", "open", () => view.ItemsActivated(view.ActiveView!, items, false), "Enter", bold: true);
        if (items.All(i => i.IsDirectory))
            yield return Item("Open in new tab", "plus", () => { foreach (var f in items) OpenInNewTab(f.Path, false); }, "Ctrl+Enter");
        yield return Sep();
        yield return Item("Cut", "cut", () => _ = CopyToClipboardAsync(cut: true), "Ctrl+X");
        yield return Item("Copy", "copy", () => _ = CopyToClipboardAsync(cut: false), "Ctrl+C");
        if (AppSettings.Current.DualPane && OtherView is { } other)
        {
            yield return Item($"Download to “{PathUtil.DisplayName(other.ActiveFolder)}”", "download", () => CopyOrMoveToOtherPane(false), "Shift+F5");
            yield return Item($"Move to “{PathUtil.DisplayName(other.ActiveFolder)}”", "swap", () => CopyOrMoveToOtherPane(true), "Shift+F6");
        }
        yield return Item("Copy URL", "link", async () =>
        {
            if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, string.Join(Environment.NewLine, items.Select(i => RemotePath.ToUrl(i.Path))));
            Toasts.Show("URL copied", ToastKind.Success, seconds: 1.5);
        });
        yield return Sep();
        yield return Item(items.Count == 1 ? "Rename" : "Rename all…", "rename", () => _ = RenameSelectionAsync(), "F2");
        yield return Item("Delete", "trash", () => _ = DeleteSelectionAsync(false), "Delete");
        if (RemotePath.TryParse(first.Path, out var c, out _) && c is { Protocol: RemoteProtocol.Sftp })
        {
            yield return Sep();
            yield return Item("Open SSH terminal", "terminal", () => OpenSshTerminal(c));
        }
    }

    // ---- Sidebar menu for a saved connection ---------------------------------------------------------------------------

    private void ShowConnectionMenu(Connection c, Control anchor)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        menu.Items.Add(Item("Connect", "network", () => OpenConnection(c), bold: true));
        menu.Items.Add(Item("Connect in new tab", "plus", () => OpenConnection(c, newTab: true)));
        if (c.Protocol == RemoteProtocol.Sftp)
            menu.Items.Add(Item("Open SSH terminal", "terminal", () => OpenSshTerminal(c)));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("Edit…", "rename", () => _ = EditConnectionAsync(c)));
        menu.Items.Add(Item("Copy URL", "link", async () =>
        {
            if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, c.Url);
        }));
        menu.Items.Add(Item(Connections.GetSecret(c) is null ? "No saved password" : "Forget saved password", "shield", () =>
        {
            Connections.SetSecret(c, null);
            RemoteSessions.Forget(c.Id);
            Toasts.Show("Password forgotten.", ToastKind.Success);
        }, enabled: Connections.GetSecret(c) is not null));
        menu.Items.Add(Item("Delete connection", "trash", () => _ = DeleteConnectionAsync(c)));
        menu.Open(anchor);
    }
}
