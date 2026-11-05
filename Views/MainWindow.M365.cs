using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>Saved Microsoft 365 admin PowerShell connections: connect in the docked terminal, edit, delete.</summary>
public sealed partial class MainWindow
{
    private void ShowNewConnectionMenu(Control anchor)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("SSH server (SFTP + terminal)…", "terminal", () => _ = EditConnectionAsync(null, new Core.Remote.Connection { Protocol = Core.Remote.RemoteProtocol.Sftp })));
        menu.Items.Add(Item("FTP / FTPS server…", "network", () => _ = EditConnectionAsync(null, new Core.Remote.Connection { Protocol = Core.Remote.RemoteProtocol.Ftps })));
        menu.Items.Add(Item("Microsoft 365 PowerShell…", "shield", () => _ = EditM365Async(null)));
        menu.Open(anchor);
    }

    private async Task EditM365Async(M365Profile? existing)
    {
        var saved = await M365Dialog.ShowAsync(existing);
        if (saved is null) return;
        _sidebar.Rebuild();
        if (existing is null) OpenM365(saved);
    }

    /// <summary>
    /// Connects in the PowerShell terminal of the current tab (or a new tab at Home when the current one is on a
    /// server or This PC). The terminal header gets a "Sign-in code" button when the connection has a code link.
    /// </summary>
    private void OpenM365(M365Profile p)
    {
        if (CurrentView is not { CanShowTerminal: true } view || PathUtil.IsRemote(view.CurrentPath))
        {
            OpenInNewTab(PathUtil.Home, false);
            view = CurrentView;
        }
        if (view is null) return;

        var link = M365.GetOtpLink(p);
        var who = p.Auth == M365Auth.Certificate ? $"app {p.AppId} in {p.Tenant}" : p.Upn.Length > 0 ? p.Upn : "your account";
        var note = $"Microsoft 365 · {p.Display}: connecting {string.Join(", ", p.Services.Select(M365.ServiceName))} as {who}";
        if (!view.RunInLocalTerminal(note, M365.ConnectSteps(p), link is null ? null : "Sign-in code", link is null ? null : () => OpenLink(link), "Open the link to your one-time code"))
        {
            // The tab was still loading its folder; try once more when it's ready.
            Dispatcher.UIThread.Post(() => view.RunInLocalTerminal(note, M365.ConnectSteps(p)), DispatcherPriority.Background);
        }
    }

    private static void OpenLink(string link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't open the link: " + ex.Message, ToastKind.Error);
        }
    }

    private void ShowM365Menu(M365Profile p, Control anchor)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        menu.Items.Add(Item("Connect", "shield", () => OpenM365(p), bold: true));
        if (M365.GetOtpLink(p) is { } link)
            menu.Items.Add(Item("Open sign-in code link", "link", () => OpenLink(link)));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("Edit…", "rename", () => _ = EditM365Async(p)));
        menu.Items.Add(Item(M365.HasPassword(p) ? "Forget saved password" : "No saved password", "shield", () =>
        {
            M365.SetPassword(p, null);
            Toasts.Show("Password forgotten.", ToastKind.Success);
        }, enabled: M365.HasPassword(p)));
        menu.Items.Add(Item("Delete connection", "trash", async () =>
        {
            if (!await Dialogs.ConfirmAsync($"Delete the connection “{p.Display}”?", "Its saved password and sign-in code link are removed from Windows Credential Manager too.", "Delete", danger: true))
                return;
            M365.Delete(p);
        }));
        menu.Open(anchor);
    }
}
