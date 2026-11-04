using System.Diagnostics;

namespace PaxCommander.Ops;

/// <summary>
/// Creates and runs file jobs. Each job runs on the thread pool; at most <see cref="MaxConcurrentJobs"/> run at once and
/// the rest wait their turn in the order they were queued (State = Queued).
/// </summary>
public sealed class JobQueue
{
    private readonly IJobUi _ui;
    private readonly Action<IReadOnlyList<string>>? _recycle;
    private readonly object _lock = new();
    private readonly List<FileJob> _jobs = [];
    // A FIFO gate rather than a SemaphoreSlim: jobs start strictly in queue order and a new limit applies at once.
    private readonly List<FileJob> _waiting = [];
    private int _running;
    private int _maxConcurrent;

    /// <param name="ui">Conflict/error prompts.</param>
    /// <param name="recycle">Platform Recycle Bin (throws on failure); null where there is none, and Recycle jobs then fail.</param>
    public JobQueue(IJobUi ui, Action<IReadOnlyList<string>>? recycle)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _recycle = recycle;
    }

    /// <summary>How many jobs run at once; 0 = no limit. A paused job keeps its slot.</summary>
    public int MaxConcurrentJobs
    {
        get { lock (_lock) return _maxConcurrent; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_lock) _maxConcurrent = value;
            StartWaitingJobs();
        }
    }

    /// <summary>Test hook: treat every move as cross-volume (copy + delete) so that path can be exercised on one disk.</summary>
    internal bool ForceCrossVolumeMoves { get; set; }

    /// <summary>Snapshot of all jobs (running and finished), oldest first.</summary>
    public IReadOnlyList<FileJob> Jobs
    {
        get { lock (_lock) return _jobs.ToArray(); }
    }

    /// <summary>Raised on a worker thread when a job reaches a final state.</summary>
    public event Action<FileJob>? JobFinished;

    public FileJob Copy(IReadOnlyList<string> sources, string destinationFolder)
    {
        var items = NormalizeAll(sources);
        var destination = OpsPath.Normalize(destinationFolder);
        var job = new FileJob
        {
            Kind = JobKind.Copy, Sources = items, Destination = destination,
            Title = $"Copying {DescribeItems(items)} to {OpsPath.DisplayName(destination)}",
        };
        return Enqueue(job, new CopyMoveRunner(job, _ui, move: false, forceCrossVolume: false));
    }

    public FileJob Move(IReadOnlyList<string> sources, string destinationFolder)
    {
        var items = NormalizeAll(sources);
        var destination = OpsPath.Normalize(destinationFolder);
        var job = new FileJob
        {
            Kind = JobKind.Move, Sources = items, Destination = destination,
            Title = $"Moving {DescribeItems(items)} to {OpsPath.DisplayName(destination)}",
        };
        return Enqueue(job, new CopyMoveRunner(job, _ui, move: true, ForceCrossVolumeMoves));
    }

    /// <summary>permanent = false → Recycle Bin.</summary>
    public FileJob Delete(IReadOnlyList<string> paths, bool permanent)
    {
        var items = NormalizeAll(paths);
        var job = new FileJob
        {
            Kind = permanent ? JobKind.Delete : JobKind.Recycle, Sources = items,
            Title = permanent ? $"Deleting {DescribeItems(items)}" : $"Moving {DescribeItems(items)} to the Recycle Bin",
        };
        return Enqueue(job, permanent ? new DeleteRunner(job, _ui) : new RecycleRunner(job, _ui, _recycle));
    }

    public FileJob Compress(IReadOnlyList<string> sources, string zipPath)
    {
        var items = NormalizeAll(sources);
        var zip = OpsPath.Normalize(zipPath);
        var job = new FileJob
        {
            Kind = JobKind.Compress, Sources = items, Destination = zip,
            Title = $"Compressing {DescribeItems(items)} to {Path.GetFileName(zip)}",
        };
        return Enqueue(job, new CompressRunner(job, _ui));
    }

    public FileJob Extract(string zipPath, string destinationFolder)
    {
        var zip = OpsPath.Normalize(zipPath);
        var job = new FileJob
        {
            Kind = JobKind.Extract, Sources = [zip], Destination = OpsPath.Normalize(destinationFolder),
            Title = $"Extracting {Path.GetFileName(zip)}",
        };
        return Enqueue(job, new ExtractRunner(job, _ui));
    }

    /// <summary>Forgets finished jobs.</summary>
    public void ClearFinished()
    {
        lock (_lock) _jobs.RemoveAll(j => j.IsFinished);
    }

    private static string[] NormalizeAll(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Select(OpsPath.Normalize).ToArray();
    }

    private static string DescribeItems(IReadOnlyList<string> items) =>
        items.Count == 1 ? $"“{OpsPath.DisplayName(items[0])}”" : $"{items.Count} items";

    private FileJob Enqueue(FileJob job, JobRunner runner)
    {
        job.Runner = runner;
        lock (_lock)
        {
            _jobs.Add(job);
            _waiting.Add(job);
        }
        // Cancelling a job that is still waiting for a slot finishes it right away instead of when its turn comes.
        job.Token.Register(() => CancelWaiting(job));
        StartWaitingJobs();
        return job;
    }

    private void StartWaitingJobs()
    {
        List<FileJob>? toStart = null;
        lock (_lock)
        {
            while (_waiting.Count > 0 && (_maxConcurrent == 0 || _running < _maxConcurrent))
            {
                (toStart ??= []).Add(_waiting[0]);
                _waiting.RemoveAt(0);
                _running++;
            }
        }
        foreach (var job in toStart ?? []) Task.Run(() => RunJob(job));
    }

    private void RunJob(FileJob job)
    {
        try
        {
            job.Runner!.Run();
        }
        catch (Exception ex)
        {
            job.TryFinish(JobState.Failed, ex.Message); // Run() doesn't throw; this keeps the slot accounting safe regardless
        }
        finally
        {
            lock (_lock) _running--;
            StartWaitingJobs();
            Announce(job);
        }
    }

    private void CancelWaiting(FileJob job)
    {
        bool removed;
        lock (_lock) removed = _waiting.Remove(job);
        if (!removed) return;
        // Cancel() is usually called on the UI thread; JobFinished is promised on a worker thread.
        Task.Run(() =>
        {
            job.TryFinish(JobState.Cancelled, null);
            Announce(job);
        });
    }

    private void Announce(FileJob job)
    {
        try
        {
            JobFinished?.Invoke(job);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JobFinished handler failed: {ex}"); // a faulty handler must not stop Completion
        }
        job.CompleteTask();
    }
}
