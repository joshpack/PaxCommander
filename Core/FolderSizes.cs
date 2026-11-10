using System.Collections.Concurrent;
using Avalonia.Threading;
using pxdCommander.Search;

namespace pxdCommander.Core;

/// <summary>
/// Adds up folder sizes in the background so the Size column (and its bars) work for folders too — the thing Explorer
/// never shows. Results are cached for a few minutes; partial totals appear while a big folder is still being counted.
/// </summary>
public static class FolderSizes
{
    private sealed record Entry(long Bytes, DateTime At);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(PathUtil.Comparer);
    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    public static bool ShouldAutoMeasure(string folder) => AppSettings.Current.FolderSizes switch
    {
        FolderSizeMode.Everywhere => !PathUtil.IsVirtual(folder) && !PathUtil.IsRemote(folder),
        FolderSizeMode.LocalDrives => PathUtil.IsLocalFixed(folder),
        _ => false,
    };

    /// <summary>Fills in sizes for every folder in the listing; cancel when the listing goes away.</summary>
    public static void MeasureAll(FolderListing listing, CancellationToken ct)
    {
        var folders = listing.Items.Where(i => i.Kind == ItemKind.Folder && !i.IsLink).ToList();
        if (folders.Count == 0) return;
        var pending = folders.Count;
        var lastResort = Environment.TickCount64;

        foreach (var item in folders)
        {
            if (Cache.TryGetValue(item.Path, out var hit) && DateTime.UtcNow - hit.At < Fresh)
            {
                item.FolderBytes = hit.Bytes;
                item.FolderSizePartial = false;
                if (Interlocked.Decrement(ref pending) == 0) listing.SizesChanged();
                continue;
            }
            _ = MeasureOne(item, ct, () =>
            {
                // Re-sorting on every folder that finishes would make the list jump around; batch it.
                var done = Interlocked.Decrement(ref pending) == 0;
                if (done || Environment.TickCount64 - lastResort > 1500)
                {
                    lastResort = Environment.TickCount64;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ct.IsCancellationRequested) listing.SizesChanged();
                    });
                }
            });
        }
        listing.SizesChanged();
    }

    /// <summary>Measures one folder (used by "Calculate size" and the preview pane).</summary>
    public static async Task<FolderSizeResult?> MeasureOne(FileItem item, CancellationToken ct, Action? finished = null)
    {
        try
        {
            await Gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            Dispatcher.UIThread.Post(() => item.FolderSizePartial = true);
            var result = await Task.Run(() => FolderSizer.MeasureAsync(item.Path, ct, partial =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ct.IsCancellationRequested) item.FolderBytes = partial.Bytes;
                })), ct);
            Dispatcher.UIThread.Post(() =>
            {
                item.FolderBytes = result.Bytes;
                item.FolderSizePartial = !result.Complete;
            });
            if (result.Complete)
                Cache[item.Path] = new Entry(result.Bytes, DateTime.UtcNow);
            return result;
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => item.FolderSizePartial = false);
            return null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("folder size " + item.Path, ex);
            return null;
        }
        finally
        {
            Gate.Release();
            finished?.Invoke();
        }
    }

    /// <summary>Something changed under <paramref name="path"/>: its cached size and every ancestor's are stale.</summary>
    public static void Invalidate(string path)
    {
        foreach (var key in Cache.Keys)
            if (PathUtil.IsSameOrInside(key, path))
                Cache.TryRemove(key, out _);
    }
}
