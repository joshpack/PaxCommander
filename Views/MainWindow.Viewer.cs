using pxdCommander.Core;
using pxdImageViewer;

namespace pxdCommander.Views;

/// <summary>
/// Images open in pxd Image Viewer (the same viewer as the standalone app, in its own window): deep zoom, stepping
/// through the folder in this tab's order, and slideshows. The tab follows along, selecting the image on screen.
/// </summary>
public sealed partial class MainWindow
{
    private static bool _viewerReady;
    private static readonly Dictionary<ViewerWindow, FolderView> ViewerSources = [];

    public static bool IsViewableImage(FileItem item) =>
        item.Kind == ItemKind.File && !item.IsRemote && ImageLoader.IsImage(item.Path);

    private static void EnsureViewer()
    {
        if (_viewerReady) return;
        _viewerReady = true;
        // Tests run against a throwaway data folder; normally the viewer shares its preferences with the standalone app.
        if (Environment.GetEnvironmentVariable("PXDCMD_DATA") is { Length: > 0 })
            ViewerSettings.FilePath = AppPaths.File("imageviewer.json");
        ViewerTheme.SetAccent(AccentTheme.Accent);
    }

    /// <summary>
    /// Shows <paramref name="start"/> in the viewer. One image: every image in the tab (in its sort order) comes along;
    /// several selected: just those.
    /// </summary>
    private void ViewImages(FolderView view, IReadOnlyList<FileItem> picked, bool slideshow = false)
    {
        if (picked.Count == 0) return;
        EnsureViewer();
        var start = picked[0];
        List<string> paths = picked.Count > 1
            ? picked.Select(i => i.Path).ToList()
            : view.ActiveListing?.Items.Where(IsViewableImage).Select(i => i.Path).ToList() ?? [];
        if (!paths.Any(p => PathUtil.Same(p, start.Path))) paths = [start.Path];

        try
        {
            var w = ViewerWindow.Show(start.Path, paths, slideshow);
            w.Icon ??= Icon;
            if (!ViewerSources.ContainsKey(w))
            {
                w.Viewer.CurrentChanged += p => FollowViewer(w, p);
                w.Closed += (_, _) => ViewerSources.Remove(w);
            }
            ViewerSources[w] = view;
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't open the viewer: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>A slideshow of every image in a folder.</summary>
    private void SlideshowFolder(string folder)
    {
        EnsureViewer();
        var files = ImageFolder.List(folder);
        if (files.Count == 0)
        {
            Toasts.Show("There are no images in " + Path.GetFileName(folder.TrimEnd('\\')) + ".");
            return;
        }
        var w = ViewerWindow.Show(files[0], files, slideshow: true);
        w.Icon ??= Icon;
    }

    /// <summary>The viewer moved on: select that image in the tab it came from, if the tab still shows it.</summary>
    private static void FollowViewer(ViewerWindow w, string? path)
    {
        if (path is null || !ViewerSources.TryGetValue(w, out var view)) return;
        if (view.ActiveListing is not { } listing || view.ActiveSelection is not { } sel) return;
        var item = listing.Items.FirstOrDefault(i => PathUtil.Same(i.Path, path));
        if (item is null) return;
        sel.SelectOnly(item);
        view.ActiveView?.BringIntoView(item);
    }
}
