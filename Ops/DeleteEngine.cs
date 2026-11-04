namespace PaxCommander.Ops;

/// <summary>
/// Permanent delete: files (read-only cleared) and folders bottom-up. Junctions and symlinks are removed as links —
/// never entered — so a link to another folder can never get that folder's contents deleted.
/// </summary>
internal sealed class DeleteRunner(FileJob job, IJobUi ui) : JobRunner(job, ui)
{
    protected override void Validate()
    {
        foreach (var path in Job.Sources)
        {
            RequireFullPath(path);
            if (OpsPath.IsRoot(path)) Fail($"“{path}” is a drive and can't be deleted.");
        }
    }

    protected override void Scan()
    {
        foreach (var path in Job.Sources) ScanInto(path);
    }

    protected override void Execute()
    {
        foreach (var path in Job.Sources)
        {
            Job.Checkpoint();
            Job.SetCurrent(path);
            if (StatItem(path) is not { } item)
            {
                Job.AddDone(0, 1);
                continue;
            }
            if (DeleteTree(item, countProgress: true)) Job.AddDeleted(path);
        }
    }
}

/// <summary>
/// Recycle Bin delete through the platform action the app injects (it throws when anything wasn't recycled).
/// Progress is by top-level items; TotalBytes stays 0 — recycling is a rename, so walking folder contents would only
/// make it slower.
/// </summary>
internal sealed class RecycleRunner(FileJob job, IJobUi ui, Action<IReadOnlyList<string>>? recycle) : JobRunner(job, ui)
{
    // The shell is much faster with many items per call; batches keep pause/cancel and progress responsive.
    private const int BatchSize = 64;
    private readonly List<string> _present = [];

    protected override void Validate()
    {
        if (recycle is null) Fail("The Recycle Bin isn't available here");
        foreach (var path in Job.Sources)
        {
            RequireFullPath(path);
            if (OpsPath.IsRoot(path)) Fail($"“{path}” is a drive and can't be deleted.");
        }
    }

    protected override void Scan()
    {
        foreach (var path in Job.Sources)
        {
            Job.Checkpoint();
            Job.AddTotals(0, 1);
            // Something that's already gone can't be recycled; say so instead of letting a whole batch fail on it.
            if (OpsPath.GetAttributesQuiet(path) is null) Job.AddProblem(path, "It no longer exists.");
            else _present.Add(path);
        }
        Job.AddDone(0, Job.Sources.Count - _present.Count);
    }

    protected override void Execute()
    {
        for (var start = 0; start < _present.Count; start += BatchSize)
        {
            Job.Checkpoint();
            var batch = _present.GetRange(start, Math.Min(BatchSize, _present.Count - start));
            Job.SetCurrent(batch[0]);
            try
            {
                recycle!(batch);
                foreach (var path in batch) Job.AddDeleted(path);
                Job.AddDone(0, batch.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Something in the batch didn't go: settle the leftovers one by one, with the error prompt.
                foreach (var path in batch)
                {
                    Job.Checkpoint();
                    Job.SetCurrent(path);
                    if (OpsPath.GetAttributesQuiet(path) is null || Attempt(path, () => recycle!([path])))
                        Job.AddDeleted(path);
                    Job.AddDone(0, 1);
                }
            }
        }
    }
}
