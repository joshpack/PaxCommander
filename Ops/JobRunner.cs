namespace PaxCommander.Ops;

/// <summary>
/// Shared machinery for every job kind: the scan → run lifecycle, the conflict and error prompts with their
/// "apply to all" memory, scanning and deleting trees without following links.
/// </summary>
internal abstract class JobRunner(FileJob job, IJobUi ui)
{
    protected readonly FileJob Job = job;
    private readonly IJobUi _ui = ui;
    // "Apply to all" is remembered separately for like-for-like clashes (file onto file) and mixed ones (file onto
    // folder): "Replace all" chosen for files must not silently wipe a whole folder that happens to share a name.
    private ConflictChoice? _sameKindChoice, _mixedKindChoice;
    private bool _skipAllErrors;

    /// <summary>Runs the job to its final state. Never throws.</summary>
    public void Run()
    {
        try
        {
            Job.MarkStarted();
            Job.Checkpoint(); // a job paused while queued waits here; one cancelled meanwhile stops here
            Job.SetState(JobState.Scanning);
            Validate();
            Scan();
            Job.SetCurrent(null);
            Job.Checkpoint();
            Job.BeginRunning();
            Execute();
            Cleanup(succeeded: true);
            Job.TryFinish(JobState.Completed, null);
        }
        catch (Exception) when (Job.IsCancelRequested)
        {
            // Whatever surfaced while cancelling (usually OperationCanceledException), the user asked to cancel.
            Cleanup(succeeded: false);
            Job.TryFinish(JobState.Cancelled, null);
        }
        catch (Exception ex)
        {
            Cleanup(succeeded: false);
            Job.TryFinish(JobState.Failed, ex.Message);
        }
    }

    /// <summary>Checks that must pass before anything is touched; throw JobFailedException to refuse.</summary>
    protected virtual void Validate() { }

    /// <summary>Counts TotalBytes/TotalItems (State = Scanning).</summary>
    protected abstract void Scan();

    /// <summary>Does the work (State = Running).</summary>
    protected abstract void Execute();

    /// <summary>Last chance to tidy up (e.g. remove a partial zip). Must not throw.</summary>
    protected virtual void Cleanup(bool succeeded) { }

    protected static void Fail(string message) => throw new JobFailedException(message);

    protected static void RequireFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) Fail($"“{path}” isn't a full path.");
    }

    // ---- prompts ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Settles a name clash, honouring a remembered "apply to all". ReplaceIfNewer comes back as Replace or Skip;
    /// Cancel cancels the job (throws OperationCanceledException).
    /// </summary>
    protected ConflictChoice ResolveConflict(ConflictInfo conflict)
    {
        var mixed = conflict.IsDirectory != conflict.DestinationIsDirectory;
        var choice = mixed ? _mixedKindChoice : _sameKindChoice;
        if (choice is null)
        {
            var answer = WaitForUser(_ui.ResolveConflictAsync(Job, conflict));
            choice = answer.Choice;
            if (answer.ApplyToAll && answer.Choice != ConflictChoice.Cancel)
            {
                if (mixed) _mixedKindChoice = answer.Choice;
                else _sameKindChoice = answer.Choice;
            }
        }
        return choice switch
        {
            ConflictChoice.Cancel => CancelJob<ConflictChoice>(),
            ConflictChoice.ReplaceIfNewer => conflict.SourceModifiedUtc > conflict.DestinationModifiedUtc ? ConflictChoice.Replace : ConflictChoice.Skip,
            _ => choice.Value,
        };
    }

    /// <summary>
    /// Runs <paramref name="action"/>, asking the user when it fails: true = done; false = skipped (recorded in
    /// Problems). Retry runs it again; Cancel cancels the job.
    /// </summary>
    protected bool Attempt(string path, Action action)
    {
        while (true)
        {
            Job.Checkpoint();
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (IsItemError(ex))
            {
                if (!AskRetry(path, ex)) return false;
            }
        }
    }

    /// <summary>The error prompt: true = retry; false = skip (recorded in Problems).</summary>
    protected bool AskRetry(string path, Exception error)
    {
        if (!_skipAllErrors)
        {
            switch (WaitForUser(_ui.ResolveErrorAsync(Job, path, error)))
            {
                case ErrorChoice.Retry:
                    return true;
                case ErrorChoice.SkipAll:
                    _skipAllErrors = true;
                    break;
                case ErrorChoice.Skip:
                    break;
                default:
                    return CancelJob<bool>();
            }
        }
        Job.AddProblem(path, OpsPath.Reason(error));
        return false;
    }

    /// <summary>Errors that belong to one item (and get the error prompt) rather than to the job as a whole.</summary>
    private static bool IsItemError(Exception ex) =>
        ex is not (OperationCanceledException or JobFailedException or DestinationExistsException or CrossVolumeException
            or OutOfMemoryException);

    private T WaitForUser<T>(Task<T> prompt)
    {
        try
        {
            // Stop waiting if the job is cancelled from the jobs list while a prompt is open.
            prompt.Wait(Job.Token);
            return prompt.Result;
        }
        catch (AggregateException) when (prompt.IsCanceled)
        {
            return CancelJob<T>(); // the app dismissed the prompt without an answer
        }
        catch (AggregateException ex)
        {
            throw new JobFailedException("The prompt failed: " + (ex.InnerException ?? ex).Message);
        }
        finally
        {
            Job.RestartSpeedWindow();
        }
    }

    private T CancelJob<T>()
    {
        Job.Cancel();
        throw new OperationCanceledException(Job.Token);
    }

    // ---- building blocks --------------------------------------------------------------------------------------

    /// <summary>Looks an item up, with the error prompt (e.g. it vanished). Null = skipped.</summary>
    protected FileSystemInfo? StatItem(string path)
    {
        FileSystemInfo? info = null;
        return Attempt(path, () => info = OpsPath.Stat(path)) ? info : null;
    }

    /// <summary>A folder's entries, with the error prompt. Null = skipped.</summary>
    protected FileSystemInfo[]? ListFolder(string folder)
    {
        FileSystemInfo[]? entries = null;
        return Attempt(folder, () => entries = new DirectoryInfo(folder).GetFileSystemInfos("*", OpsPath.Listing)) ? entries : null;
    }

    protected static ConflictInfo DescribeConflict(string sourcePath, bool sourceIsDirectory, long sourceSize, DateTime sourceModifiedUtc,
        string destinationPath, FileAttributes destinationAttributes)
    {
        var destinationIsDirectory = (destinationAttributes & FileAttributes.Directory) != 0;
        long destinationSize = 0;
        var destinationModified = DateTime.MinValue;
        try
        {
            FileSystemInfo existing = destinationIsDirectory ? new DirectoryInfo(destinationPath) : new FileInfo(destinationPath);
            destinationModified = existing.LastWriteTimeUtc;
            if (existing is FileInfo file) destinationSize = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new ConflictInfo(sourcePath, destinationPath, sourceIsDirectory, sourceIsDirectory ? 0 : sourceSize, sourceModifiedUtc,
            destinationSize, destinationModified) { DestinationIsDirectory = destinationIsDirectory };
    }

    protected static ConflictInfo DescribeConflict(FileSystemInfo source, string destinationPath, FileAttributes destinationAttributes) =>
        DescribeConflict(source.FullName, (source.Attributes & FileAttributes.Directory) != 0, source is FileInfo f ? f.Length : 0,
            source.LastWriteTimeUtc, destinationPath, destinationAttributes);

    /// <summary>Adds what's under <paramref name="path"/> to the job totals as it goes (State = Scanning).</summary>
    protected void ScanInto(string path)
    {
        if (OpsPath.GetAttributesQuiet(path) is not { } attributes) return; // gone: processing reports it
        Measure(path, attributes, live: true);
    }

    /// <summary>
    /// Bytes and items under <paramref name="path"/> (itself included). Links count as one item and are never entered.
    /// Unreadable folders count as what could be read; processing reports them. With <paramref name="live"/> the totals
    /// are added to the job while scanning so the UI sees them grow.
    /// </summary>
    protected (long Bytes, int Items) Measure(string path, FileAttributes attributes, bool live = false)
    {
        long bytes = 0;
        var items = 1;
        if ((attributes & FileAttributes.Directory) == 0)
        {
            try { bytes = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (live) Job.AddTotals(bytes, items);
            return (bytes, items);
        }
        if (live) Job.AddTotals(0, 1);
        if (OpsPath.IsLink(path, attributes)) return (0, 1);

        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            if (live) Job.SetCurrent(folder);
            Job.Checkpoint();
            long folderBytes = 0;
            var folderItems = 0;
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", OpsPath.Listing))
                {
                    folderItems++;
                    if (entry is FileInfo file) folderBytes += file.Length;
                    else if (!OpsPath.IsLink(entry.FullName, entry.Attributes)) pending.Push(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            bytes += folderBytes;
            items += folderItems;
            if (live) Job.AddTotals(folderBytes, folderItems);
        }
        return (bytes, items);
    }

    /// <summary>Marks a whole item as done without processing it (skipped), so progress still reaches 100%.</summary>
    protected void SkipProgress(FileSystemInfo item)
    {
        var (bytes, items) = Measure(item.FullName, item.Attributes);
        Job.AddDone(bytes, items);
    }

    /// <summary>
    /// Deletes a file, a link (just the link) or a folder tree, never following links. Returns false when anything was
    /// skipped — the item then still exists. With <paramref name="countProgress"/> each entry advances Done*.
    /// </summary>
    protected bool DeleteTree(FileSystemInfo item, bool countProgress)
    {
        var path = item.FullName;
        Job.SetCurrent(path);
        switch (OpsPath.Classify(item))
        {
            case OpsItemKind.File:
            {
                var size = ((FileInfo)item).Length;
                var ok = Attempt(path, () => OpsPath.DeleteFile(path));
                if (countProgress) Job.AddDone(size, 1);
                return ok;
            }
            case OpsItemKind.Link:
            {
                var ok = Attempt(path, () => OpsPath.DeleteLink(path));
                if (countProgress) Job.AddDone(0, 1);
                return ok;
            }
            default:
            {
                var children = ListFolder(path);
                var all = children is not null;
                foreach (var child in children ?? [])
                {
                    Job.Checkpoint();
                    all &= DeleteTree(child, countProgress);
                }
                // Bottom-up: the folder goes only once it's empty; a skipped child keeps its folder.
                if (all)
                {
                    Job.SetCurrent(path);
                    all = Attempt(path, () => OpsPath.DeleteEmptyFolder(path));
                }
                if (countProgress) Job.AddDone(0, 1);
                return all;
            }
        }
    }

    /// <summary>Clears the way for Replace when the existing item can't simply be overwritten (folder, link, file↔folder).</summary>
    protected bool RemoveExisting(string path)
    {
        FileSystemInfo existing;
        try { existing = OpsPath.Stat(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        return DeleteTree(existing, countProgress: false);
    }

    /// <summary>
    /// Creates a folder and any missing parents, recording the topmost new one in Created. False = skipped.
    /// </summary>
    protected bool EnsureFolder(string path, bool record)
    {
        if (Directory.Exists(path)) return true;
        var missing = new Stack<string>();
        for (var p = path; !string.IsNullOrEmpty(p) && !Directory.Exists(p); p = Path.GetDirectoryName(p)) missing.Push(p);
        while (missing.Count > 0)
        {
            var folder = missing.Pop();
            if (!Attempt(folder, () => Directory.CreateDirectory(folder))) return false;
            if (record) Job.RecordCreated(folder);
        }
        return true;
    }
}
