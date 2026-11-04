using System.IO.Enumeration;

namespace PaxCommander.Search;

/// <summary>
/// One worker's view of the walk. Each worker thread gets its own instance, so per-worker buffers and counters
/// need no locking. ProcessDirectory lists one folder and appends the subfolders to descend into.
/// </summary>
internal abstract class WalkWorker
{
    public abstract void ProcessDirectory(string path, bool isRoot, List<string> subdirectories);

    /// <summary>ProcessDirectory threw (should not happen; the enumerators swallow IO errors).</summary>
    public virtual void OnDirectoryFailed(string path, bool isRoot, Exception error) { }

    /// <summary>Called once on the worker thread after its last directory.</summary>
    public virtual void Finish() { }
}

/// <summary>
/// Parallel breadth-first directory walk shared by search and folder sizing. A FIFO queue of folders is drained by
/// N dedicated threads (blocking IO would starve the thread pool); a folder's subfolders are queued when its listing
/// is done. Reparse points are never descended into, so junction/symlink loops cannot happen.
/// </summary>
internal static class TreeWalker
{
    // Network walks are latency-bound, so more threads than cores pay off. On H: (SMB) 8..32 workers all ran at the
    // server's limit (~2x a single thread); 16 costs nothing there and helps on slower links.
    internal const int NetworkParallelism = 16;

    /// <summary>
    /// Starts the walk. The task completes when every worker has exited; its result is true when the tree was walked to
    /// the end, false when the token stopped it first. It never faults.
    /// </summary>
    public static Task<bool> Run(string root, int workerCount, Func<WalkWorker> createWorker, CancellationToken ct)
    {
        var queue = new WorkQueue(root);
        var registration = ct.Register(static q => ((WorkQueue)q!).Close(), queue);
        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            var worker = createWorker();
            workers[i] = Task.Factory.StartNew(() => WorkerLoop(queue, worker, root, ct), CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        return Task.WhenAll(workers).ContinueWith(_ =>
        {
            registration.Dispose();
            return !ct.IsCancellationRequested;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void WorkerLoop(WorkQueue queue, WalkWorker worker, string root, CancellationToken ct)
    {
        Thread.CurrentThread.Name ??= "PaxCommander walk";
        var subdirectories = new List<string>();
        try
        {
            while (queue.TryTake(out var dir))
            {
                subdirectories.Clear();
                bool isRoot = ReferenceEquals(dir, root);
                try
                {
                    if (!ct.IsCancellationRequested) worker.ProcessDirectory(dir, isRoot, subdirectories);
                }
                catch (Exception e)
                {
                    // One odd folder must not kill the worker: the queue's busy count would never drop and the walk would hang.
                    worker.OnDirectoryFailed(dir, isRoot, e);
                }
                finally
                {
                    queue.Complete(ct.IsCancellationRequested ? null : subdirectories);
                }
            }
        }
        finally
        {
            worker.Finish();
        }
    }

    /// <summary>Worker count for a root: explicit request, else more threads for latency-bound network paths.</summary>
    public static int ChooseParallelism(string fullPath, int requested)
    {
        if (requested > 0) return Math.Clamp(requested, 1, 64);
        return IsNetworkPath(fullPath) ? NetworkParallelism : Math.Clamp(Environment.ProcessorCount, 4, 12);
    }

    public static bool IsNetworkPath(string fullPath)
    {
        try
        {
            if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
                fullPath = fullPath[4..];
            else if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return true; // \\server\share
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Full path for a walk root, or null when it is not a usable path. "C:" means the drive root.</summary>
    public static string? NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (OperatingSystem.IsWindows() && path.Length == 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) path += '\\';
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The enumeration options for one folder listing; errors are seen through ContinueOnError instead.</summary>
    public static EnumerationOptions ListingOptions(bool skipHidden, bool network) => new()
    {
        RecurseSubdirectories = false,
        // Not IgnoreInaccessible: our enumerators override ContinueOnError, which sees every error (so folder sizing
        // can count access-denied folders) and always continues, which ignores them just the same.
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = skipHidden ? FileAttributes.Hidden | FileAttributes.System : 0,
        // A bigger listing buffer means fewer directory-query round trips for big folders over SMB (no difference for
        // typical small folders, measured on H:).
        BufferSize = network ? 64 * 1024 : 16 * 1024,
    };

    /// <summary>Directory went away between being listed and being opened: not an access problem.</summary>
    public static bool IsNotFound(int error) => OperatingSystem.IsWindows()
        ? error is 2 or 3 or 267 // FILE_NOT_FOUND, PATH_NOT_FOUND, DIRECTORY (replaced by a file)
        : error is 2 or 20;      // ENOENT, ENOTDIR

    /// <summary>
    /// FIFO folder queue with termination detection: the walk is over when the queue is empty and no worker is busy
    /// (a busy worker may still add subfolders).
    /// </summary>
    private sealed class WorkQueue
    {
        private readonly Queue<string> _items = new();
        private int _busy, _waiting;
        private bool _closed;

        public WorkQueue(string root) => _items.Enqueue(root);

        public bool TryTake(out string dir)
        {
            lock (_items)
            {
                while (true)
                {
                    if (!_closed)
                    {
                        if (_items.TryDequeue(out dir!))
                        {
                            _busy++;
                            return true;
                        }
                        if (_busy == 0)
                        {
                            _closed = true;
                            Monitor.PulseAll(_items);
                        }
                    }
                    if (_closed)
                    {
                        dir = null!;
                        return false;
                    }
                    _waiting++;
                    Monitor.Wait(_items);
                    _waiting--;
                }
            }
        }

        public void Complete(List<string>? subdirectories)
        {
            lock (_items)
            {
                _busy--;
                if (_closed) return;
                if (subdirectories is { Count: > 0 })
                {
                    foreach (var d in subdirectories) _items.Enqueue(d);
                    // Waking everyone is simplest and cheap at these thread counts; losers just wait again.
                    if (_waiting > 0) Monitor.PulseAll(_items);
                }
                else if (_busy == 0 && _items.Count == 0)
                {
                    _closed = true;
                    Monitor.PulseAll(_items);
                }
            }
        }

        public void Close()
        {
            lock (_items)
            {
                _closed = true;
                _items.Clear();
                Monitor.PulseAll(_items);
            }
        }
    }
}
