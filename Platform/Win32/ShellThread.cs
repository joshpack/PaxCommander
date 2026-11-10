using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

/// <summary>Dedicated STA threads for shell COM calls (icons, thumbnails, properties).</summary>
[SupportedOSPlatform("windows")]
public static class ShellThread
{
    private static readonly StaWorkerPool s_pool = new("pxdCommander shell", threadCount: 2, lifo: true);

    /// <summary>
    /// Runs <paramref name="work"/> on an STA worker. Newest requests run first (LIFO) so whatever the user just scrolled to
    /// is served before items that already scrolled away; a cancelled token drops a queued item without running it.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default) => s_pool.RunAsync(work, ct);

    /// <summary>True when the caller is running on one of the shell worker threads.</summary>
    public static bool IsShellThread => s_pool.IsWorkerThread;
}

/// <summary>Runs blocking shell calls (SHFileOperation, ShellExecuteEx…) on an STA thread when the caller is not on one.</summary>
[SupportedOSPlatform("windows")]
internal static class StaInvoker
{
    // A thread of its own, so a long Recycle never takes a ShellThread worker away from icons and thumbnails.
    private static readonly StaWorkerPool s_pool = new("pxdCommander shell operations", threadCount: 1, lifo: false);

    internal static T Invoke<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return work();
        return s_pool.RunAsync(work, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>A fixed set of STA threads sharing one LIFO stack or FIFO queue of work items.</summary>
[SupportedOSPlatform("windows")]
internal sealed class StaWorkerPool
{
    private readonly string _name;
    private readonly int _threadCount;
    private readonly bool _lifo;
    private readonly object _lock = new();
    private readonly Stack<WorkItem> _stack = new();
    private readonly Queue<WorkItem> _queue = new();
    // One count per queued item. A kernel semaphore (not SemaphoreSlim) because the workers wait on it together with
    // their message queue in MsgWaitForMultipleObjectsEx.
    private readonly Semaphore _pending = new(0, int.MaxValue);
    private volatile bool _started;

    [ThreadStatic] private static StaWorkerPool? t_owner;

    internal StaWorkerPool(string name, int threadCount, bool lifo)
    {
        _name = name;
        _threadCount = threadCount;
        _lifo = lifo;
    }

    internal bool IsWorkerThread => t_owner == this;

    internal Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T>(ct);

        var item = new WorkItem<T>(work, ct);
        EnsureStarted();
        lock (_lock)
        {
            if (_lifo)
                _stack.Push(item);
            else
                _queue.Enqueue(item);
        }
        _pending.Release();
        return item.Task;
    }

    private void EnsureStarted()
    {
        if (_started)
            return;
        lock (_lock)
        {
            if (_started)
                return;
            for (int i = 0; i < _threadCount; i++)
            {
                var thread = new Thread(WorkerMain)
                {
                    IsBackground = true,
                    Name = _threadCount > 1 ? $"{_name} #{i + 1}" : _name,
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            _started = true;
        }
    }

    private unsafe void WorkerMain()
    {
        t_owner = this;
        // SetApartmentState already made this an STA; OLE on top is what shell extensions written for Explorer's
        // threads expect (some touch the clipboard or drag/drop helpers).
        NativeMethods.OleInitialize(IntPtr.Zero);

        bool addedRef = false;
        _pending.SafeWaitHandle.DangerousAddRef(ref addedRef); // the pool lives as long as the process
        IntPtr handle = _pending.SafeWaitHandle.DangerousGetHandle();
        while (true)
        {
            // Wait for work while still dispatching messages: calls into COM objects living on this STA, and the
            // hidden windows some shell extensions create here, depend on this thread pumping.
            uint result = NativeMethods.MsgWaitForMultipleObjectsEx(1, &handle, NativeMethods.INFINITE,
                NativeMethods.QS_ALLINPUT, NativeMethods.MWMO_INPUTAVAILABLE);
            if (result == NativeMethods.WAIT_OBJECT_0)
                RunNext();
            else if (result != NativeMethods.WAIT_OBJECT_0 + 1)
                Thread.Sleep(1); // WAIT_FAILED should never happen; just don't spin on it
            PumpMessages();
        }
    }

    private void RunNext()
    {
        WorkItem item;
        lock (_lock)
            item = _lifo ? _stack.Pop() : _queue.Dequeue();
        item.Execute();
    }

    private static void PumpMessages()
    {
        while (NativeMethods.PeekMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(msg);
            NativeMethods.DispatchMessageW(msg);
        }
    }

    private abstract class WorkItem
    {
        internal abstract void Execute();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private const int Queued = 0, Running = 1, Cancelled = 2;

        // Continuations must not run inline on the STA worker (an awaiting caller would otherwise carry on here).
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _ct;
        private readonly CancellationTokenRegistration _registration;
        private Func<T>? _work;
        private int _state;

        internal WorkItem(Func<T> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;
            if (ct.CanBeCanceled)
                _registration = ct.Register(static s => ((WorkItem<T>)s!).Cancel(), this);
        }

        internal Task<T> Task => _tcs.Task;

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, Cancelled, Queued) != Queued)
                return; // already running: like Task.Run, a started item finishes
            _work = null; // the item stays on the stack until a worker pops it; don't keep its closure alive too
            _tcs.TrySetCanceled(_ct);
        }

        internal override void Execute()
        {
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
                return;
            _registration.Unregister();
            Func<T> work = _work!;
            _work = null;
            try
            {
                _tcs.TrySetResult(work());
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == _ct && _ct.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(_ct);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
