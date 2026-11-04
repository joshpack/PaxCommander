using Avalonia.Controls;
using PaxCommander.Core;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Views;

/// <summary>The local network: mapping and disconnecting network drives.</summary>
public sealed partial class MainWindow
{
    private async Task MapDriveAsync(string? share)
    {
        var root = await MapDriveDialog.ShowAsync(share);
        if (root is null) return;
        Toasts.Show($"Mapped {root.TrimEnd('\\')}.", ToastKind.Success);
        _sidebar.Rebuild();
        if (CurrentView is { } v) await v.NavigateAsync(root);
    }

    private async Task DisconnectDriveAsync(string root)
    {
        var letter = root.TrimEnd('\\');
        var remote = NetworkBrowse.RemoteOf(letter);
        if (!await Dialogs.ConfirmAsync($"Disconnect {letter}?", $"{remote} stays on the network; only the drive letter goes away.", "Disconnect"))
            return;
        try
        {
            await Task.Run(() => NetworkBrowse.Disconnect(letter));
            // Tabs showing that drive move to This PC.
            foreach (var v in Views().Where(v => !PathUtil.IsVirtual(v.CurrentPath) && PathUtil.IsSameOrInside(root, v.CurrentPath)).ToList())
                _ = v.NavigateAsync(PathUtil.ComputerPath);
            _sidebar.Rebuild();
            Toasts.Show($"Disconnected {letter}.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    /// <summary>A mapped network drive's root (e.g. "Z:\"), for the Disconnect menu entry.</summary>
    private static bool IsMappedDrive(string path) =>
        OperatingSystem.IsWindows() && path.Length is 2 or 3 && path[1] == ':' && NetworkBrowse.RemoteOf(path[..2]) is not null;

    /// <summary>Menu entries for network things: disconnect a mapped drive, map a shared folder.</summary>
    private IEnumerable<Control> NetworkMenuItems(string path)
    {
        if (IsMappedDrive(path))
            yield return Item("Disconnect network drive", "eject", () => _ = DisconnectDriveAsync(path));
        else if (PathUtil.Parent(path) is { } parent && PathUtil.IsServer(parent))
            yield return Item("Map network drive…", "drive", () => _ = MapDriveAsync(path));
    }
}
