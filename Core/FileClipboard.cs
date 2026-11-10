using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using pxdCommander.Platform.Win32;

namespace pxdCommander.Core;

/// <summary>
/// Cut/copy/paste of files through the real system clipboard, so it interoperates with Explorer both ways.
/// On Windows that's CF_HDROP + "Preferred DropEffect" (what Explorer uses); elsewhere Avalonia's file clipboard.
/// </summary>
public static class FileClipboard
{
    private static readonly List<FileItem> CutMarked = [];
    private static string[] _lastPaths = [];
    private static bool _lastCut;
    private static bool _remoteOnly;
    private static uint _remoteSequence;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public static async Task SetAsync(TopLevel top, IReadOnlyList<FileItem> items, bool cut)
    {
        ClearCutMarks();
        var paths = items.Select(i => i.Path).ToArray();
        var ok = false;
        // Server items have no local file for Windows' clipboard; they stay inside the app.
        _remoteOnly = items.Any(i => i.IsRemote);
        _remoteSequence = OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;
        if (_remoteOnly) ok = true;
        if (!ok && OperatingSystem.IsWindows())
            ok = NativeClipboard.SetFiles(paths, cut);
        if (!ok && top.Clipboard is { } cb)
        {
            var storage = new List<IStorageItem>();
            foreach (var i in items)
            {
                IStorageItem? s = i.IsDirectory
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(i.Path)
                    : await top.StorageProvider.TryGetFileFromPathAsync(i.Path);
                if (s is not null) storage.Add(s);
            }
            await cb.SetFilesAsync(storage);
        }
        _lastPaths = paths;
        _lastCut = cut;
        if (cut)
        {
            foreach (var i in items)
            {
                i.IsCut = true;
                CutMarked.Add(i);
            }
        }
    }

    /// <summary>Files on the clipboard and whether they were cut, or null when there are none.</summary>
    public static async Task<(IReadOnlyList<string> Paths, bool Cut)?> GetAsync(TopLevel top)
    {
        // A server copy wins until something else lands on the Windows clipboard.
        if (_remoteOnly && _lastPaths.Length > 0 && (!OperatingSystem.IsWindows() || GetClipboardSequenceNumber() == _remoteSequence))
            return (_lastPaths, _lastCut);
        if (OperatingSystem.IsWindows())
        {
            var files = NativeClipboard.GetFiles(out var cut);
            if (files is { Count: > 0 }) return (files, cut);
        }
        if (top.Clipboard is { } cb)
        {
            var items = await cb.TryGetFilesAsync();
            var paths = items?.Select(i => i.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
            if (paths is { Count: > 0 })
            {
                // Avalonia's clipboard has no cut flag; remember our own last cut.
                var cut = _lastCut && paths.SequenceEqual(_lastPaths, PathUtil.Comparer);
                return (paths, cut);
            }
        }
        return null;
    }

    public static bool MaybeHasFiles()
    {
        if (OperatingSystem.IsWindows()) return NativeClipboard.ContainsFiles();
        return _lastPaths.Length > 0;
    }

    /// <summary>After a cut is pasted the source items are gone; Explorer empties the clipboard too.</summary>
    public static async Task ConsumeCutAsync(TopLevel top)
    {
        ClearCutMarks();
        _lastCut = false;
        _lastPaths = [];
        if (top.Clipboard is { } cb) await cb.ClearAsync();
    }

    public static void ClearCutMarks()
    {
        foreach (var i in CutMarked) i.IsCut = false;
        CutMarked.Clear();
    }

    public static bool IsCutPath(string path) => _lastCut && _lastPaths.Contains(path, PathUtil.Comparer);
}
