using System.Collections.Concurrent;
using Avalonia.Threading;
using pxdCommander.Search;

namespace pxdCommander.Core;

/// <summary>
/// Adds up folder sizes in the background so the Size column (and its bars) work for folders too — the thing Explorer
/// never shows. Totals are remembered on disk (folder-sizes.tsv), so a folder counted before shows its last total at
/// once — faded — while a fresh count runs quietly and replaces it. Recent totals (5 min local, 30 min network) aren't
/// recounted at all. Partial totals appear while a folder that was never counted is still being added up.
/// </summary>
public static class FolderSizes
{
    private sealed record Entry(long Bytes, DateTime At);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(PathUtil.Comparer);
    private static readonly SemaphoreSlim Gate = new(3);
    private const int MaxRemembered = 20_000;
    private static readonly object LoadLock = new();
    private static bool _loaded;
    private static Timer? _saveTimer;

    private static string StoreFile => AppPaths.File("folder-sizes.tsv");

    /// <summary>How long a count is trusted without recounting: longer on the network, where counting is slow.</summary>
    private static TimeSpan FreshFor(string path) => PathUtil.IsLocalFixed(path) ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30);

    /// <summary>The remembered total for a folder, if any (for tests and the preview).</summary>
    public static long? Remembered(string path)
    {
        EnsureLoaded();
        return Cache.TryGetValue(path, out var e) ? e.Bytes : null;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (LoadLock)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(StoreFile))
                    foreach (var line in File.ReadLines(StoreFile))
                    {
                        // bytes <tab> UTC ticks <tab> path
                        var parts = line.Split('\t', 3);
                        if (parts.Length == 3 && long.TryParse(parts[0], out var bytes) && long.TryParse(parts[1], out var ticks) && parts[2].Length > 0)
                            Cache.TryAdd(parts[2], new Entry(bytes, new DateTime(ticks, DateTimeKind.Utc)));
                    }
            }
            catch (Exception ex)
            {
                CrashLog.Write("folder sizes load", ex);
            }
            _loaded = true;
        }
    }

    private static void SaveSoon()
    {
        _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes the remembered totals (newest 20,000) to disk.</summary>
    public static void SaveNow()
    {
        if (!_loaded) return;
        try
        {
            var lines = Cache.ToArray().OrderByDescending(kv => kv.Value.At).Take(MaxRemembered)
                .Select(kv => $"{kv.Value.Bytes}\t{kv.Value.At.Ticks}\t{kv.Key}");
            Directory.CreateDirectory(AppPaths.Data);
            var tmp = StoreFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, StoreFile, overwrite: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("folder sizes save", ex);
        }
    }

    public static bool ShouldAutoMeasure(string folder) => AppSettings.Current.FolderSizes switch
    {
        // A server's share list (\\nas) counts: each share is a real folder to add up.
        FolderSizeMode.Everywhere => !PathUtil.IsComputer(folder) && !PathUtil.IsNetworkRoot(folder) && !PathUtil.IsRemote(folder),
        FolderSizeMode.LocalDrives => PathUtil.IsLocalFixed(folder),
        _ => false,
    };

    /// <summary>Fills in sizes for every folder in the listing; cancel when the listing goes away.</summary>
    public static void MeasureAll(FolderListing listing, CancellationToken ct)
    {
        var folders = listing.Items.Where(i => i.Kind == ItemKind.Folder && !i.IsLink).ToList();
        if (folders.Count == 0) return;
        EnsureLoaded();
        var pending = folders.Count;
        var lastResort = Environment.TickCount64;

        foreach (var item in folders)
        {
            if (Cache.TryGetValue(item.Path, out var hit))
            {
                item.FolderBytes = hit.Bytes;
                item.FolderSizePartial = false;
                if (DateTime.UtcNow - hit.At < FreshFor(item.Path))
                {
                    item.SizeIsStale = false;
                    if (Interlocked.Decrement(ref pending) == 0) listing.SizesChanged();
                    continue;
                }
                // Older count: show it now (faded) and recount quietly below.
                item.SizeIsStale = true;
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
            // A remembered total stays on screen while it's rechecked: no "…", no shrinking partial numbers.
            var quiet = item.SizeIsStale && item.FolderBytes >= 0;
            if (!quiet) Dispatcher.UIThread.Post(() => item.FolderSizePartial = true);
            var result = await Task.Run(() => FolderSizer.MeasureAsync(item.Path, ct, partial =>
            {
                if (!quiet)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ct.IsCancellationRequested) item.FolderBytes = partial.Bytes;
                    });
            }), ct);
            Dispatcher.UIThread.Post(() =>
            {
                // Switched off (or left the folder) meanwhile: don't put a number back.
                if (ct.IsCancellationRequested) return;
                item.FolderBytes = result.Bytes;
                item.FolderSizePartial = !result.Complete;
                item.SizeIsStale = false;
            });
            if (result.Complete)
            {
                EnsureLoaded();
                Cache[item.Path] = new Entry(result.Bytes, DateTime.UtcNow);
                SaveSoon();
            }
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

    /// <summary>
    /// Something changed under <paramref name="path"/>: its total and every ancestor's must be recounted. They're kept
    /// (marked old) so the last total still shows, faded, until the recount finishes.
    /// </summary>
    public static void Invalidate(string path, bool includeInside = false)
    {
        EnsureLoaded();
        var changed = false;
        foreach (var (key, entry) in Cache.ToArray())
            if ((PathUtil.IsSameOrInside(key, path) || includeInside && PathUtil.IsSameOrInside(path, key)) && entry.At != DateTime.MinValue)
            {
                Cache[key] = entry with { At = DateTime.MinValue };
                changed = true;
            }
        if (changed) SaveSoon();
    }
}
