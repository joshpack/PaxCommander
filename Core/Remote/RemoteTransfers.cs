using PaxCommander.Ops;

namespace PaxCommander.Core.Remote;

/// <summary>What the operations panel shows for any job (local engine jobs and remote transfers alike).</summary>
public interface IJobView
{
    Guid Id { get; }
    string Glyph { get; }
    string Title { get; }
    JobState State { get; }
    DateTime? FinishedUtc { get; }
    int ProblemCount { get; }
    string? FirstProblem { get; }
    long TotalBytes { get; }
    long DoneBytes { get; }
    int TotalItems { get; }
    int DoneItems { get; }
    string? CurrentItem { get; }
    double BytesPerSecond { get; }
    TimeSpan? Remaining { get; }
    string? ErrorMessage { get; }
    bool CanPause { get; }
    void Pause();
    void Resume();
    void Cancel();
}

/// <summary>Copy, move or delete involving a server. Runs on its own session so browsing stays responsive.</summary>
public sealed class RemoteJob : IJobView
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _problems = [];
    private long _done;
    private long _speedMark;
    private long _speedBytes;

    public Guid Id { get; } = Guid.NewGuid();
    public string Glyph { get; init; } = "network";
    public string Title { get; init; } = "";
    public JobState State { get; internal set; } = JobState.Queued;
    public DateTime? FinishedUtc { get; private set; }
    public int ProblemCount { get { lock (_problems) return _problems.Count; } }
    public string? FirstProblem { get { lock (_problems) return _problems.FirstOrDefault(); } }
    public long TotalBytes { get; internal set; }
    public long DoneBytes => Interlocked.Read(ref _done);
    public int TotalItems { get; internal set; }
    public int DoneItems { get; internal set; }
    public string? CurrentItem { get; internal set; }
    public double BytesPerSecond { get; private set; }
    public TimeSpan? Remaining => BytesPerSecond > 1 && TotalBytes > DoneBytes ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / BytesPerSecond) : null;
    public string? ErrorMessage { get; private set; }
    public bool CanPause => false;
    public CancellationToken Token => _cts.Token;
    /// <summary>Top-level items that now exist at the destination (app paths).</summary>
    public List<string> Created { get; } = [];
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Action<RemoteJob>? Finished;

    public void Pause() { }
    public void Resume() { }
    public void Cancel() => _cts.Cancel();

    internal void AddBytes(long n)
    {
        Interlocked.Add(ref _done, n);
        var now = Environment.TickCount64;
        if (_speedMark == 0) _speedMark = now;
        _speedBytes += n;
        if (now - _speedMark >= 1000)
        {
            var rate = _speedBytes * 1000.0 / (now - _speedMark);
            BytesPerSecond = BytesPerSecond == 0 ? rate : BytesPerSecond * 0.6 + rate * 0.4;
            _speedMark = now;
            _speedBytes = 0;
        }
    }

    internal void Problem(string text)
    {
        lock (_problems) _problems.Add(text);
    }

    internal void Start(Func<RemoteJob, Task> work)
    {
        Completion = Task.Run(async () =>
        {
            State = JobState.Running;
            try
            {
                await work(this);
                State = _cts.IsCancellationRequested ? JobState.Cancelled : JobState.Completed;
            }
            catch (OperationCanceledException)
            {
                State = JobState.Cancelled;
            }
            catch (Exception ex)
            {
                ErrorMessage = _cts.IsCancellationRequested ? null : ex.Message;
                State = _cts.IsCancellationRequested ? JobState.Cancelled : JobState.Failed;
            }
            FinishedUtc = DateTime.UtcNow;
            CurrentItem = null;
            Finished?.Invoke(this);
        });
    }
}

/// <summary>
/// Moves data between this PC and servers (and between servers). Folders are walked first so progress is honest;
/// name clashes go through the same conflict prompt as local copies.
/// </summary>
public static class RemoteTransfers
{
    private sealed record Plan(string Source, string Target, bool IsDir, long Size);

    public static RemoteJob Transfer(IReadOnlyList<string> sources, string destinationFolder, bool move, IJobUi ui)
    {
        var what = sources.Count == 1 ? $"“{PathUtil.DisplayName(sources[0])}”" : Formatting.Count(sources.Count, "item");
        var upload = !PathUtil.IsRemote(sources[0]) && PathUtil.IsRemote(destinationFolder);
        var download = PathUtil.IsRemote(sources[0]) && !PathUtil.IsRemote(destinationFolder);
        var verb = move ? "Moving" : upload ? "Uploading" : download ? "Downloading" : "Copying";
        var job = new RemoteJob { Title = $"{verb} {what} to {PathUtil.DisplayName(destinationFolder)}", Glyph = upload ? "up" : download ? "download" : "swap" };
        job.Start(j => RunTransfer(j, sources, destinationFolder, move, ui));
        return job;
    }

    public static RemoteJob Delete(IReadOnlyList<string> paths)
    {
        var what = paths.Count == 1 ? $"“{PathUtil.DisplayName(paths[0])}”" : Formatting.Count(paths.Count, "item");
        var job = new RemoteJob { Title = $"Deleting {what}", Glyph = "trash" };
        job.Start(async j =>
        {
            foreach (var group in paths.GroupBy(RemotePath.Prefix))
            {
                var conn = Conn(group.First());
                using var fs = await RemoteSessions.OpenAsync(conn, j.Token);
                var plan = new List<(string Remote, bool IsDir)>();
                foreach (var p in group)
                    await WalkForDelete(fs, RemotePath.RemotePart(p), plan, j.Token);
                j.TotalItems += plan.Count;
                // Children before their folders.
                foreach (var (remote, isDir) in plan)
                {
                    j.Token.ThrowIfCancellationRequested();
                    j.CurrentItem = remote;
                    try
                    {
                        if (isDir) await fs.DeleteDirectoryAsync(remote, j.Token);
                        else await fs.DeleteFileAsync(remote, j.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        j.Problem($"{remote}: {ex.Message}");
                    }
                    j.DoneItems++;
                }
            }
        });
        return job;
    }

    private static async Task WalkForDelete(IRemoteFs fs, string remote, List<(string, bool)> plan, CancellationToken ct)
    {
        var stat = await fs.StatAsync(remote, ct);
        if (stat is null) return;
        if (stat.IsDirectory && !stat.IsLink)
        {
            foreach (var e in await fs.ListAsync(remote, ct))
                await WalkForDelete(fs, Join(remote, e.Name), plan, ct);
        }
        plan.Add((remote, stat.IsDirectory && !stat.IsLink));
    }

    private static Connection Conn(string path) =>
        RemotePath.TryParse(path, out var c, out _) && c is not null ? c : throw new IOException("That connection no longer exists.");

    private static string Join(string remoteFolder, string name) => remoteFolder == "/" ? "/" + name : remoteFolder.TrimEnd('/') + "/" + name;

    private static async Task RunTransfer(RemoteJob job, IReadOnlyList<string> sources, string destination, bool move, IJobUi ui)
    {
        var ct = job.Token;
        IRemoteFs? src = null, dst = null;
        try
        {
            if (PathUtil.IsRemote(sources[0])) src = await RemoteSessions.OpenAsync(Conn(sources[0]), ct);
            if (PathUtil.IsRemote(destination))
                dst = src is not null && RemotePath.Prefix(sources[0]) == RemotePath.Prefix(destination) ? src : await RemoteSessions.OpenAsync(Conn(destination), ct);

            // Moving within one server is a rename.
            if (move && src is not null && src == dst)
            {
                job.TotalItems = sources.Count;
                foreach (var s in sources)
                {
                    var target = RemotePath.Combine(destination, RemotePath.Name(s));
                    job.CurrentItem = s;
                    await src.RenameAsync(RemotePath.RemotePart(s), RemotePath.RemotePart(target), ct);
                    job.Created.Add(target);
                    job.DoneItems++;
                }
                return;
            }

            // Plan: every file with its size, folders first.
            var plan = new List<Plan>();
            foreach (var s in sources)
                await PlanItem(s, Combine(destination, PathUtil.IsRemote(s) ? RemotePath.Name(s) : Path.GetFileName(s.TrimEnd('\\'))), src, plan, ct);
            job.TotalItems = plan.Count(p => !p.IsDir);
            job.TotalBytes = plan.Sum(p => p.Size);

            ConflictChoice? always = null;
            var topLevel = sources.Select(s => Combine(destination, PathUtil.IsRemote(s) ? RemotePath.Name(s) : Path.GetFileName(s.TrimEnd('\\')))).ToHashSet();
            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                job.CurrentItem = item.Source;
                try
                {
                    if (item.IsDir)
                    {
                        await MakeDir(dst, item.Target, ct);
                    }
                    else
                    {
                        var target = item.Target;
                        if (await Exists(dst, target, ct))
                        {
                            var choice = always is { } a ? new ConflictAnswer(a, true) : await Ask(ui, job, item, dst, ct);
                            if (choice.ApplyToAll) always = choice.Choice;
                            switch (choice.Choice)
                            {
                                case ConflictChoice.Cancel:
                                    job.Cancel();
                                    return;
                                case ConflictChoice.Skip:
                                case ConflictChoice.ReplaceIfNewer:
                                    job.AddBytes(item.Size);
                                    job.DoneItems++;
                                    continue;
                                case ConflictChoice.KeepBoth:
                                    target = await FreeName(dst, target, ct);
                                    break;
                            }
                        }
                        await CopyFile(src, dst, item.Source, target, job, ct);
                        if (move) await DeleteSource(src, item.Source, ct);
                    }
                    if (topLevel.Contains(item.Target)) job.Created.Add(item.Target);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.Problem($"{Path.GetFileName(item.Source)}: {ex.Message}");
                }
                if (!item.IsDir) job.DoneItems++;
            }

            // A moved folder goes once its files have (deepest first, only if now empty).
            if (move)
            {
                foreach (var dir in plan.Where(p => p.IsDir).Select(p => p.Source).Reverse())
                {
                    try
                    {
                        if (src is not null) await src.DeleteDirectoryAsync(RemotePath.RemotePart(dir), ct);
                        else if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    }
                    catch
                    {
                        // Something was skipped inside: the folder stays.
                    }
                }
            }
        }
        finally
        {
            src?.Dispose();
            if (dst is not null && dst != src) dst.Dispose();
        }
    }

    private static string Combine(string folder, string name) =>
        PathUtil.IsRemote(folder) ? RemotePath.Combine(folder, name) : Path.Combine(folder, name);

    private static async Task PlanItem(string source, string target, IRemoteFs? src, List<Plan> plan, CancellationToken ct)
    {
        if (src is not null)
        {
            var remote = RemotePath.RemotePart(source);
            var stat = await src.StatAsync(remote, ct) ?? throw new FileNotFoundException("Not found on the server", remote);
            if (!stat.IsDirectory)
            {
                plan.Add(new Plan(source, target, false, stat.Size));
                return;
            }
            plan.Add(new Plan(source, target, true, 0));
            foreach (var e in await src.ListAsync(remote, ct))
            {
                if (e.IsLink && e.IsDirectory) continue;
                var child = RemotePath.Combine(source, e.Name);
                if (e.IsDirectory) await PlanItem(child, Combine(target, e.Name), src, plan, ct);
                else plan.Add(new Plan(child, Combine(target, e.Name), false, e.Size));
            }
            return;
        }
        if (File.Exists(source))
        {
            plan.Add(new Plan(source, target, false, new FileInfo(source).Length));
            return;
        }
        plan.Add(new Plan(source, target, true, 0));
        foreach (var e in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            await PlanItem(e.FullName, Combine(target, e.Name), null, plan, ct);
        }
    }

    private static async Task MakeDir(IRemoteFs? dst, string target, CancellationToken ct)
    {
        if (dst is null)
        {
            Directory.CreateDirectory(target);
            return;
        }
        var remote = RemotePath.RemotePart(target);
        if (await dst.StatAsync(remote, ct) is null) await dst.CreateDirectoryAsync(remote, ct);
    }

    private static async Task<bool> Exists(IRemoteFs? dst, string target, CancellationToken ct) =>
        dst is null ? File.Exists(target) || Directory.Exists(target) : await dst.StatAsync(RemotePath.RemotePart(target), ct) is not null;

    private static async Task<string> FreeName(IRemoteFs? dst, string target, CancellationToken ct)
    {
        var remote = PathUtil.IsRemote(target);
        var name = remote ? RemotePath.Name(target) : Path.GetFileName(target);
        var folder = remote ? RemotePath.Parent(target)! : Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = Combine(folder, $"{stem} ({n}){ext}");
            if (!await Exists(dst, candidate, ct)) return candidate;
        }
    }

    private static async Task<ConflictAnswer> Ask(IJobUi ui, RemoteJob job, Plan item, IRemoteFs? dst, CancellationToken ct)
    {
        long destSize = 0;
        if (dst is not null && await dst.StatAsync(RemotePath.RemotePart(item.Target), ct) is { } st) destSize = st.Size;
        else if (dst is null && File.Exists(item.Target)) destSize = new FileInfo(item.Target).Length;
        var info = new ConflictInfo(item.Source, item.Target, false, item.Size, DateTime.MinValue, destSize, DateTime.MinValue);
        return await ui.ResolveConflictAsync(null!, info);
    }

    private static async Task CopyFile(IRemoteFs? src, IRemoteFs? dst, string source, string target, RemoteJob job, CancellationToken ct)
    {
        long last = 0;
        void Progress(long done)
        {
            job.AddBytes(done - last);
            last = done;
        }

        if (src is not null && dst is null)
        {
            // Download into a .part file so a cancelled transfer never leaves a half file under the real name.
            var part = target + ".part";
            await using (var stream = File.Create(part))
                await src.DownloadAsync(RemotePath.RemotePart(source), stream, Progress, ct);
            File.Move(part, target, true);
            return;
        }
        if (src is null && dst is not null)
        {
            await using var stream = File.OpenRead(source);
            await dst.UploadAsync(stream, RemotePath.RemotePart(target), Progress, ct);
            return;
        }
        // Server to server: through a temporary file on this PC.
        var temp = Path.Combine(Path.GetTempPath(), "PaxCommander", "relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await using (var down = File.Create(temp))
                await src!.DownloadAsync(RemotePath.RemotePart(source), down, d => Progress(d / 2), ct);
            var half = last;
            await using var up = File.OpenRead(temp);
            await dst!.UploadAsync(up, RemotePath.RemotePart(target), d => Progress(half + d / 2), ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static async Task DeleteSource(IRemoteFs? src, string source, CancellationToken ct)
    {
        if (src is not null) await src.DeleteFileAsync(RemotePath.RemotePart(source), ct);
        else File.Delete(source);
    }

    /// <summary>A new empty folder or file on a server, with a free name; returns its name.</summary>
    public static async Task<string> CreateAsync(string folder, string baseName, string extension, bool isFolder)
    {
        var conn = Conn(folder);
        var fs = await RemoteSessions.GetAsync(conn);
        var remoteFolder = RemotePath.RemotePart(folder);
        var names = (await fs.ListAsync(remoteFolder, CancellationToken.None)).Select(e => e.Name).ToHashSet();
        var name = baseName + extension;
        for (var n = 2; names.Contains(name); n++) name = $"{baseName} ({n}){extension}";
        var remote = Join(remoteFolder, name);
        if (isFolder) await fs.CreateDirectoryAsync(remote, CancellationToken.None);
        else await fs.UploadAsync(new MemoryStream(), remote, _ => { }, CancellationToken.None);
        return name;
    }

    public static async Task RenameAsync(string path, string newName)
    {
        var fs = await RemoteSessions.GetAsync(Conn(path));
        var remote = RemotePath.RemotePart(path);
        var parent = remote[..Math.Max(0, remote.LastIndexOf('/'))];
        await fs.RenameAsync(remote, Join(parent.Length == 0 ? "/" : parent, newName), CancellationToken.None);
    }
}
