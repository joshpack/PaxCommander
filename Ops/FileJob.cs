namespace pxdCommander.Ops;

/// <summary>
/// One queued operation. Live properties are safe to read from any thread (the UI polls them a few times a second;
/// nothing here raises PropertyChanged).
/// </summary>
public sealed class FileJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public JobKind Kind { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    /// <summary>Target folder (copy, move, extract) or the .zip path (compress); null for deletes.</summary>
    public string? Destination { get; init; }
    /// <summary>Human title, e.g. "Copying 3 items to Downloads".</summary>
    public string Title { get; init; } = "";

    // Live state is written only by the job's worker thread and read by anyone. Each value is a single field written
    // atomically (64-bit ones through Interlocked so 32-bit readers can't see a torn value), so no locks are needed.
    private int _state;
    private volatile bool _pauseRequested;
    private long _totalBytes, _doneBytes;
    private int _totalItems, _doneItems;
    private volatile string? _currentItem;
    private volatile string? _errorMessage;
    private long _startedTicks = DateTime.UtcNow.Ticks;
    private long _finishedTicks;
    private int _finished;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);
    private readonly object _pauseLock = new();
    private readonly JobSpeedMeter _meter = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _resultsLock = new();
    private readonly List<string> _created = [], _deleted = [], _skipped = [];
    private readonly List<(string From, string To)> _moved = [];
    private readonly List<(string Path, string Reason)> _problems = [];
    // Destination paths already accounted for in Created/Moved. Only the highest new item is listed: a file inside a
    // folder this job created is covered by that folder, so undo deletes/moves back one item instead of thousands.
    private readonly HashSet<string> _covered = new(OpsPath.Comparer);

    /// <summary>Paused is reported as soon as Pause() is called; the worker stops at its next check (well under 200 ms).</summary>
    public JobState State
    {
        get
        {
            var state = (JobState)Volatile.Read(ref _state);
            return _pauseRequested && state is JobState.Queued or JobState.Scanning or JobState.Running ? JobState.Paused : state;
        }
    }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long DoneBytes => Interlocked.Read(ref _doneBytes);
    public int TotalItems => Volatile.Read(ref _totalItems);
    public int DoneItems => Volatile.Read(ref _doneItems);
    public string? CurrentItem => _currentItem;

    /// <summary>Throughput over roughly the last 3 seconds; 0 when not running or not yet measurable.</summary>
    public double BytesPerSecond => State == JobState.Running ? _meter.Rate(DoneBytes, out _) : 0;

    /// <summary>Estimated time left, from <see cref="BytesPerSecond"/>; null until there is at least a second of data.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (State != JobState.Running) return null;
            var done = DoneBytes;
            var rate = _meter.Rate(done, out var spanMs);
            if (rate <= 0 || spanMs < 1000) return null;
            var seconds = Math.Max(0, TotalBytes - done) / rate;
            return seconds > 1e7 ? null : TimeSpan.FromSeconds(Math.Ceiling(seconds));
        }
    }

    public string? ErrorMessage => _errorMessage;
    /// <summary>When the job left the queue (its creation time while it is still queued).</summary>
    public DateTime StartedUtc => new(Interlocked.Read(ref _startedTicks), DateTimeKind.Utc);
    public DateTime? FinishedUtc => Interlocked.Read(ref _finishedTicks) is var t and not 0 ? new DateTime(t, DateTimeKind.Utc) : null;
    /// <summary>Completes (never faults) once the job is final and JobFinished has been raised.</summary>
    public Task Completion => _completion.Task;
    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    // Results for undo. Safe to read at any time (each read is a snapshot); complete once Completion has finished.
    /// <summary>Items created at the destination: copies, the zip, the extracted root items (or the destination folder if Extract created it).</summary>
    public IReadOnlyList<string> Created { get { lock (_resultsLock) return _created.ToArray(); } }
    /// <summary>Items moved, as (old path, new path) — final names after keep-both renames.</summary>
    public IReadOnlyList<(string From, string To)> Moved { get { lock (_resultsLock) return _moved.ToArray(); } }
    /// <summary>Items deleted or recycled.</summary>
    public IReadOnlyList<string> Deleted { get { lock (_resultsLock) return _deleted.ToArray(); } }
    /// <summary>Items skipped or failed, with the reason.</summary>
    public IReadOnlyList<(string Path, string Reason)> Problems { get { lock (_resultsLock) return _problems.ToArray(); } }
    /// <summary>Items left alone because the user chose Skip (or ReplaceIfNewer found them older) in a name clash.</summary>
    public IReadOnlyList<string> Skipped { get { lock (_resultsLock) return _skipped.ToArray(); } }

    public void Pause()
    {
        lock (_pauseLock)
        {
            if (IsFinished) return;
            _pauseRequested = true;
            _resumeGate.Reset();
        }
    }

    public void Resume()
    {
        lock (_pauseLock)
        {
            _pauseRequested = false;
            _resumeGate.Set();
        }
    }

    public void Cancel()
    {
        if (IsFinished) return;
        _cts.Cancel();
    }

    // ---- worker side -------------------------------------------------------------------------------------------

    internal JobRunner? Runner { get; set; }
    internal CancellationToken Token => _cts.Token;
    internal bool IsCancelRequested => _cts.IsCancellationRequested;
    internal bool IsPauseRequested => _pauseRequested;

    /// <summary>Pause/cancel point: blocks while paused, throws OperationCanceledException once cancelled.</summary>
    internal void Checkpoint()
    {
        _cts.Token.ThrowIfCancellationRequested();
        if (_pauseRequested) WaitWhilePaused();
    }

    internal void WaitWhilePaused()
    {
        _resumeGate.Wait(_cts.Token);
        // Restart the speed window so the paused stretch doesn't drag the rate down after resuming.
        _meter.Reset(DoneBytes);
    }

    internal void MarkStarted() => Interlocked.Exchange(ref _startedTicks, DateTime.UtcNow.Ticks);
    internal void SetState(JobState state) => Volatile.Write(ref _state, (int)state);

    internal void BeginRunning()
    {
        _meter.Reset(DoneBytes);
        SetState(JobState.Running);
    }

    /// <summary>Called after the worker waited on the user: that wait mustn't count against the speed.</summary>
    internal void RestartSpeedWindow() => _meter.Reset(DoneBytes);

    internal void SetCurrent(string? path) => _currentItem = path;

    internal void AddTotals(long bytes, int items)
    {
        if (bytes != 0) Interlocked.Add(ref _totalBytes, bytes);
        if (items != 0) Interlocked.Add(ref _totalItems, items);
    }

    internal void SetDoneBytes(long value)
    {
        Interlocked.Exchange(ref _doneBytes, value);
        // Files can grow (or carry alternate streams) after the scan; never report more done than total.
        if (value > Interlocked.Read(ref _totalBytes)) Interlocked.Exchange(ref _totalBytes, value);
        _meter.Add(value);
    }

    internal void AddDone(long bytes, int items)
    {
        if (bytes != 0) SetDoneBytes(DoneBytes + bytes);
        if (items == 0) return;
        var done = Interlocked.Add(ref _doneItems, items);
        if (done > Volatile.Read(ref _totalItems)) Volatile.Write(ref _totalItems, done);
    }

    internal void AddProblem(string path, string reason) { lock (_resultsLock) _problems.Add((path, reason)); }
    internal void AddSkipped(string path) { lock (_resultsLock) _skipped.Add(path); }
    internal void AddDeleted(string path) { lock (_resultsLock) _deleted.Add(path); }

    internal void RecordCreated(string path)
    {
        lock (_resultsLock) if (Cover(path)) _created.Add(path);
    }

    internal void RecordMoved(string from, string to)
    {
        lock (_resultsLock) if (Cover(to)) _moved.Add((from, to));
    }

    /// <summary>Marks <paramref name="path"/> as accounted for; false when it (or a parent) already is.</summary>
    private bool Cover(string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (_covered.Contains(p)) return false;
        _covered.Add(path);
        return true;
    }

    /// <summary>Moves the job to its final state exactly once. The error message is set before the state flips.</summary>
    internal bool TryFinish(JobState final, string? error)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return false;
        _errorMessage = error;
        _currentItem = null;
        Interlocked.Exchange(ref _finishedTicks, DateTime.UtcNow.Ticks);
        lock (_pauseLock)
        {
            _pauseRequested = false;
            _resumeGate.Set();
        }
        SetState(final);
        return true;
    }

    internal void CompleteTask() => _completion.TrySetResult();
}

/// <summary>
/// Bytes per second over a sliding window of about three seconds, so a burst of tiny files or one slow patch doesn't
/// make the number jump. The worker pushes samples (at most every 100 ms); the rate is computed when someone asks,
/// against "now", so a stall shows as a falling rate instead of a frozen one.
/// </summary>
internal sealed class JobSpeedMeter
{
    private const long WindowMs = 3000, SampleEveryMs = 100, MinimumSpanMs = 500;
    private readonly List<(long Time, long Bytes)> _samples = [];
    private readonly object _lock = new();

    public void Reset(long bytes)
    {
        lock (_lock)
        {
            _samples.Clear();
            _samples.Add((Environment.TickCount64, bytes));
        }
    }

    public void Add(long bytes)
    {
        var now = Environment.TickCount64;
        lock (_lock)
        {
            if (_samples.Count > 0 && now - _samples[^1].Time < SampleEveryMs) return;
            _samples.Add((now, bytes));
            // Keep exactly one sample older than the window as the base, so the window stays ~3 s wide.
            while (_samples.Count > 2 && now - _samples[1].Time >= WindowMs) _samples.RemoveAt(0);
        }
    }

    public double Rate(long currentBytes, out long spanMs)
    {
        var now = Environment.TickCount64;
        lock (_lock)
        {
            spanMs = 0;
            if (_samples.Count == 0) return 0;
            var (time, bytes) = _samples[0];
            spanMs = now - time;
            return spanMs < MinimumSpanMs ? 0 : Math.Max(0, (currentBytes - bytes) * 1000.0 / spanMs);
        }
    }
}
