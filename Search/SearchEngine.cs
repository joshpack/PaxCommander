using System.Diagnostics;
using System.IO.Enumeration;

namespace pxdCommander.Search;

public static class SearchEngine
{
    private const int BatchSize = 256;      // hits that trigger an early flush
    private const int TickMs = 100;         // flush and progress cadence
    private const int CancelGraceMs = 100;  // how long a cancel waits for workers before returning anyway
    private const int PublishEvery = 4096;  // entries between counter publications inside one huge folder

    /// <summary>
    /// Recursive parallel search under <paramref name="root"/>. Results arrive in batches through <paramref name="onHits"/>
    /// (called from worker threads, never concurrently with itself); progress roughly every 100 ms. Junctions/symlinks are
    /// not followed. Returns when finished, cancelled (no exception) or MaxResults is reached.
    /// </summary>
    /// <remarks>
    /// onHits and onProgress are both called from one reporting loop on a thread-pool thread, so they never overlap
    /// each other either. After cancellation no further batches are delivered; the final progress report
    /// (Finished = true) is still made. An exception thrown by a callback stops the search and propagates.
    /// </remarks>
    public static async Task RunAsync(string root, SearchQuery query, SearchOptions options,
        Action<IReadOnlyList<SearchHit>> onHits, Action<SearchProgress>? onProgress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(onHits);
        options ??= new SearchOptions();

        string? full = TreeWalker.NormalizeRoot(root);
        if (full is null || ct.IsCancellationRequested)
        {
            onProgress?.Invoke(new SearchProgress(0, 0, 0, null, true));
            return;
        }

        bool includeHidden = query.IncludeHidden ?? options.IncludeHidden;
        bool network = TreeWalker.IsNetworkPath(full);
        int workers = TreeWalker.ChooseParallelism(full, options.MaxParallelism);

        // "stop" ends the walk for either reason: the caller cancelled, or MaxResults was reached.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new Run(query, options.MaxResults <= 0 ? int.MaxValue : options.MaxResults,
            TreeWalker.ListingOptions(skipHidden: !includeHidden, network), stop);

        Task<bool> walk = TreeWalker.Run(full, workers, () => new Worker(run), stop.Token);
        _ = walk.ContinueWith(static (_, s) => ((Run)s!).Wake(), run, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        using var wakeOnCancel = ct.Register(static s => ((Run)s!).Wake(), run);

        long delivered = 0;
        var clock = Stopwatch.StartNew();
        long lastProgress = 0;
        try
        {
            while (true)
            {
                await run.Signal.WaitAsync(TickMs).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                bool done = walk.IsCompleted; // read before draining: once true, nothing more can be added
                var batch = run.TakeBatch();
                if (batch is not null)
                {
                    delivered += batch.Count;
                    onHits(batch);
                }
                if (done) break;
                long now = clock.ElapsedMilliseconds;
                if (onProgress is not null && now - lastProgress >= TickMs - 10)
                {
                    lastProgress = now;
                    onProgress(run.Snapshot(delivered, finished: false));
                }
            }
        }
        finally
        {
            stop.Cancel(); // no-op after a natural finish; otherwise tells the workers to stop
            // Workers notice within one directory entry or one 64 KB content chunk; a worker stuck in a slow network call
            // is left to finish on its own rather than holding up the caller.
            if (!walk.IsCompleted) await Task.WhenAny(walk, Task.Delay(CancelGraceMs)).ConfigureAwait(false);
        }
        onProgress?.Invoke(run.Snapshot(delivered, finished: true));
    }

    /// <summary>State shared by the workers and the reporting loop of one search.</summary>
    private sealed class Run(SearchQuery query, int maxResults, EnumerationOptions listing, CancellationTokenSource stop)
    {
        public readonly SearchQuery Query = query;
        public readonly EnumerationOptions Listing = listing;
        public readonly CancellationToken Token = stop.Token;
        public readonly SemaphoreSlim Signal = new(0);
        public long Folders, Files;
        public volatile string? CurrentFolder;

        private readonly object _lock = new();
        private List<SearchHit> _pending = new(BatchSize);
        private int _accepted;

        public void Add(SearchHit hit)
        {
            bool wake, full;
            lock (_lock)
            {
                // Also rejects stragglers from workers that outlived a cancelled run.
                if (Token.IsCancellationRequested || _accepted >= maxResults) return;
                _accepted++;
                _pending.Add(hit);
                full = _accepted >= maxResults;
                wake = full || _pending.Count == BatchSize;
            }
            if (wake) Wake();
            if (full) stop.Cancel();
        }

        public List<SearchHit>? TakeBatch()
        {
            lock (_lock)
            {
                if (_pending.Count == 0) return null;
                var batch = _pending;
                _pending = new List<SearchHit>(BatchSize);
                return batch;
            }
        }

        public void Wake() => Signal.Release();

        public SearchProgress Snapshot(long delivered, bool finished) =>
            new(Interlocked.Read(ref Folders), Interlocked.Read(ref Files), delivered, CurrentFolder, finished);
    }

    private sealed class Worker(Run run) : WalkWorker
    {
        private readonly ContentScanner? _scanner = run.Query.NeedsContent ? new ContentScanner(run.Query.ContentTerms) : null;
        // With only -content: terms a folder passes (it contains none of the excluded text); with any content: term it can't match.
        private readonly bool _foldersPassContent = Array.TrueForAll(run.Query.ContentTerms, t => t.Negate);
        internal long Files, SincePublish;

        public override void ProcessDirectory(string path, bool isRoot, List<string> subdirectories)
        {
            run.CurrentFolder = path;
            using (var listing = new Listing(path, run, this, subdirectories))
            {
                while (listing.MoveNext())
                {
                    if (run.Token.IsCancellationRequested) break;
                    var hit = listing.Current;
                    if (hit is null) continue;
                    if (_scanner is not null && (hit.IsDirectory
                            ? !_foldersPassContent
                            : !_scanner.Matches(hit.Path, hit.Name, hit.Size, hit.Attributes, run.Token)))
                        continue;
                    run.Add(hit);
                }
            }
            Interlocked.Increment(ref run.Folders);
            Publish();
        }

        internal void Publish()
        {
            if (Files != 0) Interlocked.Add(ref run.Files, Files);
            Files = 0;
            SincePublish = 0;
        }

        public override void Finish() => Publish();
    }

    /// <summary>
    /// Lists one folder. Every entry passes through ShouldIncludeEntry, which queues subfolders and runs the
    /// span-based query test; only matches are turned into SearchHit objects.
    /// </summary>
    private sealed class Listing : FileSystemEnumerator<SearchHit?>
    {
        private readonly Run _run;
        private readonly Worker _worker;
        private readonly List<string> _subdirectories;
        private string? _folderPath;
        private bool _stopping;

        public Listing(string path, Run run, Worker worker, List<string> subdirectories) : base(path, run.Listing)
        {
            _run = run;
            _worker = worker;
            _subdirectories = subdirectories;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (_run.Token.IsCancellationRequested)
            {
                // Yield a null so the caller's loop regains control and stops, instead of finishing a huge folder.
                _stopping = true;
                return true;
            }
            _folderPath = null;
            if (entry.IsDirectory)
            {
                // Reparse points (junctions, symlinks, mount points) are listed but never entered: no loops.
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    _folderPath = entry.ToFullPath();
                    _subdirectories.Add(_folderPath);
                }
            }
            else
            {
                _worker.Files++;
            }
            if (++_worker.SincePublish >= PublishEvery) _worker.Publish();
            return _run.Query.MatchesEntry(ref entry);
        }

        protected override SearchHit? TransformEntry(ref FileSystemEntry entry)
        {
            if (_stopping) return null;
            bool isDirectory = entry.IsDirectory;
            return new SearchHit(_folderPath ?? entry.ToFullPath(), entry.FileName.ToString(), isDirectory,
                isDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.UtcDateTime, entry.CreationTimeUtc.UtcDateTime,
                entry.Attributes);
        }

        // Unreadable folders are skipped, like IgnoreInaccessible but for every error (network drops included).
        protected override bool ContinueOnError(int error) => true;
    }
}
