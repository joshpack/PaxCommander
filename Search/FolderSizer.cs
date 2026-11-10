using System.IO.Enumeration;

namespace pxdCommander.Search;

public static class FolderSizer
{
    private const int TickMs = 200;
    private const int CancelGraceMs = 100;
    private const int PublishEvery = 8192;

    /// <summary>
    /// Total size of everything under <paramref name="folder"/> (parallel, junctions/symlinks not followed). Partial totals go
    /// to <paramref name="progress"/> about every 200 ms. Cancelling returns the partial total with Complete = false.
    /// </summary>
    /// <remarks>
    /// Hidden and system files are counted. Reparse-point folders count as folders but are not entered. A folder whose
    /// listing fails (access denied, network error) adds 1 to Inaccessible; one that vanished mid-walk does not.
    /// progress is called from a thread-pool thread, never concurrently with itself. Never throws.
    /// </remarks>
    public static Task<FolderSizeResult> MeasureAsync(string folder, CancellationToken ct, Action<FolderSizeResult>? progress = null) =>
        MeasureAsync(folder, ct, progress, maxParallelism: 0);

    /// <summary>As above, with an explicit worker count (0 = automatic: CPU count clamped to 4..12, 16 on network paths).</summary>
    public static async Task<FolderSizeResult> MeasureAsync(string folder, CancellationToken ct, Action<FolderSizeResult>? progress,
        int maxParallelism)
    {
        var totals = new Totals();
        string? full = TreeWalker.NormalizeRoot(folder);
        if (full is null) return new FolderSizeResult(0, 0, 0, 1, true);
        if (ct.IsCancellationRequested) return totals.Snapshot(complete: false);

        var listing = TreeWalker.ListingOptions(skipHidden: false, TreeWalker.IsNetworkPath(full));
        int workers = TreeWalker.ChooseParallelism(full, maxParallelism);
        Task<bool> walk = TreeWalker.Run(full, workers, () => new Worker(totals, listing, ct), ct);

        while (!walk.IsCompleted)
        {
            // Task.Delay with the token completes early on cancel; WhenAny never throws.
            await Task.WhenAny(walk, Task.Delay(TickMs, ct)).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;
            if (!walk.IsCompleted) progress?.Invoke(totals.Snapshot(complete: false));
        }
        if (!walk.IsCompleted)
        {
            // Cancelled: give the workers a moment to publish their last counts, but don't wait on a stuck network call.
            await Task.WhenAny(walk, Task.Delay(CancelGraceMs)).ConfigureAwait(false);
            return totals.Snapshot(complete: false);
        }
        return totals.Snapshot(complete: walk.Result);
    }

    private sealed class Totals
    {
        public long Bytes, Files, Folders;
        public int Inaccessible;

        public FolderSizeResult Snapshot(bool complete) => new(Interlocked.Read(ref Bytes), Interlocked.Read(ref Files),
            Interlocked.Read(ref Folders), Volatile.Read(ref Inaccessible), complete);
    }

    private sealed class Worker(Totals totals, EnumerationOptions listingOptions, CancellationToken ct) : WalkWorker
    {
        internal long Bytes, Files, Folders, SincePublish;

        public override void ProcessDirectory(string path, bool isRoot, List<string> subdirectories)
        {
            int error;
            using (var listing = new Listing(path, listingOptions, this, subdirectories, ct))
            {
                // One MoveNext reads the whole folder: the listing only counts and never yields, except one dummy item
                // when cancelled mid-folder.
                listing.MoveNext();
                error = listing.Error;
            }
            // A subfolder that vanished between being listed and being opened simply has nothing to count.
            if (error != 0 && (isRoot || !TreeWalker.IsNotFound(error))) Interlocked.Increment(ref totals.Inaccessible);
            Publish();
        }

        public override void OnDirectoryFailed(string path, bool isRoot, Exception error) =>
            Interlocked.Increment(ref totals.Inaccessible);

        public override void Finish() => Publish();

        internal void Publish()
        {
            if (Bytes != 0) Interlocked.Add(ref totals.Bytes, Bytes);
            if (Files != 0) Interlocked.Add(ref totals.Files, Files);
            if (Folders != 0) Interlocked.Add(ref totals.Folders, Folders);
            Bytes = Files = Folders = SincePublish = 0;
        }
    }

    private sealed class Listing : FileSystemEnumerator<byte>
    {
        private readonly Worker _worker;
        private readonly List<string> _subdirectories;
        private readonly CancellationToken _ct;

        /// <summary>The first error opening or reading this folder (Win32 error or errno), 0 when it listed cleanly.</summary>
        public int Error; // set from ContinueOnError, which can run inside the base constructor

        public Listing(string path, EnumerationOptions options, Worker worker, List<string> subdirectories, CancellationToken ct)
            : base(path, options)
        {
            _worker = worker;
            _subdirectories = subdirectories;
            _ct = ct;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (_ct.IsCancellationRequested) return true; // surfaces one item so MoveNext returns and the worker stops
            if (entry.IsDirectory)
            {
                _worker.Folders++;
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) _subdirectories.Add(entry.ToFullPath());
            }
            else
            {
                _worker.Files++;
                _worker.Bytes += entry.Length;
            }
            if (++_worker.SincePublish >= PublishEvery) _worker.Publish();
            return false;
        }

        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;

        protected override bool ContinueOnError(int error)
        {
            if (Error == 0) Error = error;
            return true;
        }
    }
}
