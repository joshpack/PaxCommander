using pxdCommander.Core;
using pxdCommander.Platform.Win32;

namespace pxdCommander.Views;

/// <summary>Mounting and unmounting disc images (ISO) and virtual disks.</summary>
public sealed partial class MainWindow
{
    public static bool IsMountableImage(FileItem item) =>
        OperatingSystem.IsWindows() && item.Kind == ItemKind.File && item.Extension is ".iso" or ".vhd" or ".vhdx";

    private async Task UnmountAsync(string driveRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = await Task.Run(() => VirtualDrives.GetMountedImage(driveRoot));
        var letter = driveRoot.TrimEnd('\\');
        if (image is null)
        {
            Toasts.Show($"{letter} isn't a mounted image.", ToastKind.Error);
            return;
        }

        // Our own tabs watch folders on the drive and would keep it busy: move them off first.
        foreach (var v in Views().Where(v => !PathUtil.IsVirtual(v.CurrentPath) && PathUtil.IsSameOrInside(driveRoot, v.CurrentPath)).ToList())
            await v.NavigateAsync(PathUtil.ComputerPath);
        _preview.Show(null);

        Toasts.Show($"Unmounting {letter}…", seconds: 1.5);
        var error = await Task.Run(() => VirtualDrives.Unmount(image));
        if (error is not null)
        {
            Toasts.Show($"Couldn't unmount {letter}: {error}", ToastKind.Error, seconds: 8);
            return;
        }
        var file = Path.GetFileName(image.DisplayPath);
        Toasts.Show($"Unmounted {letter} ({file})", ToastKind.Success, "Mount again", () => _ = MountAsync(image.DisplayPath), seconds: 5);
        DrivesChanged();
    }

    private async Task MountAsync(string imagePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (await Task.Run(() => VirtualDrives.FindMount(imagePath)) is { } already)
        {
            Toasts.Show($"Already mounted as {already.DriveRoot.TrimEnd('\\')}");
            OpenInNewTab(already.DriveRoot, false);
            return;
        }
        Toasts.Show($"Mounting {Path.GetFileName(imagePath)}…", seconds: 1.5);
        var (root, error) = await Task.Run(() => VirtualDrives.Mount(imagePath));
        if (root is null)
        {
            Toasts.Show("Couldn't mount it: " + error, ToastKind.Error, seconds: 8);
            return;
        }
        Toasts.Show($"Mounted as {root.TrimEnd('\\')}", ToastKind.Success, seconds: 3);
        DrivesChanged();
        OpenInNewTab(root, false);
    }

    /// <summary>Opens the folder holding a mounted drive's image file, with the file selected.</summary>
    private void ShowImageFile(MountedImage image)
    {
        if (CurrentView is not { } v) return;
        _ = v.NavigateAsync(image.DisplayPath);
        v.FocusList();
    }

    private void UnmountPicker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var images = VirtualDrives.All();
        if (images.Count == 0)
        {
            Toasts.Show("No disc images are mounted.");
            return;
        }
        if (images.Count == 1)
        {
            _ = UnmountAsync(images[0].DriveRoot);
            return;
        }
        ShowGoAnywhere("", images.Select(m => new PaletteItem($"Unmount {m.DriveRoot.TrimEnd('\\')}", m.DisplayPath, "eject", null,
            () => _ = UnmountAsync(m.DriveRoot), Skin.Accent)).ToList(), "Unmount which image?");
    }

    /// <summary>A new accent colour: brushes recolour themselves; custom-drawn rows and the window icon need a repaint.</summary>
    private void OnAccentChanged()
    {
        Icon = AppIcon.CreateWindowIcon();
        pxdImageViewer.ViewerTheme.SetAccent(AccentTheme.Accent);
        foreach (var p in _panes)
            foreach (var t in p.Tabs) t.InvalidateRows();
        _sidebar.Rebuild();
    }

    /// <summary>A drive came or went: refresh the sidebar and any "This PC" tabs.</summary>
    private void DrivesChanged()
    {
        _sidebar.Rebuild();
        foreach (var v in Views().Where(v => PathUtil.IsComputer(v.CurrentPath)))
            _ = v.RefreshAsync();
    }
}
