namespace pxdCommander.Ops;

/// <summary>
/// Copy, and Move. A move within one volume renames (instant; a folder meeting an existing folder of the same name
/// merges into it by moving its children). Across volumes it copies, deleting each source item only after its copy
/// succeeded, and removes source folders only when they end up empty.
/// </summary>
internal sealed class CopyMoveRunner(FileJob job, IJobUi ui, bool move, bool forceCrossVolume) : JobRunner(job, ui)
{
    private readonly FileCopier _copier = new(job);
    private string _destination = "", _realDestination = "";
    private bool[] _byRename = []; // per source: same-volume move done by renaming

    private string Verb => move ? "moved" : "copied";

    protected override void Validate()
    {
        _destination = Job.Destination ?? "";
        RequireFullPath(_destination);
        var attributes = OpsPath.GetAttributesQuiet(_destination)
            ?? throw new JobFailedException($"The folder “{OpsPath.DisplayName(_destination)}” doesn't exist.");
        if ((attributes & FileAttributes.Directory) == 0) Fail($"“{OpsPath.DisplayName(_destination)}” is a file, not a folder.");

        _realDestination = OpsPath.RealPath(_destination);
        foreach (var source in Job.Sources)
        {
            RequireFullPath(source);
            if (OpsPath.IsRoot(source)) Fail($"“{source}” is a drive and can't be {Verb}.");
            // A folder copied into itself would copy its own copy, forever. Real paths are compared as well, so a
            // destination reached through a junction that leads back into the source is caught too.
            if (OpsPath.IsSameOrInside(_destination, source)
                || (Directory.Exists(source) && OpsPath.IsSameOrInside(_realDestination, OpsPath.RealPath(source))))
                Fail($"“{Path.GetFileName(source)}” can't be {Verb} into itself or one of its own folders.");
        }
        _byRename = Job.Sources.Select(s => move && !forceCrossVolume && OpsPath.SameVolume(s, _destination)).ToArray();
    }

    protected override void Scan()
    {
        for (var i = 0; i < Job.Sources.Count; i++)
        {
            var source = Job.Sources[i];
            if (!_byRename[i])
            {
                ScanInto(source);
                continue;
            }
            // A rename never touches the contents, so walking them would only cost time.
            if (OpsPath.GetAttributesQuiet(source) is not { } attributes) continue;
            long size = 0;
            if ((attributes & FileAttributes.Directory) == 0)
                try { size = new FileInfo(source).Length; } catch (IOException) { }
            Job.AddTotals(size, 1);
        }
    }

    protected override void Execute()
    {
        for (var i = 0; i < Job.Sources.Count; i++)
        {
            Job.Checkpoint();
            var source = Job.Sources[i];
            Job.SetCurrent(source);
            if (StatItem(source) is not { } item)
            {
                Job.AddDone(0, 1);
                continue;
            }
            // Same folder, also when the destination is the source folder reached through a junction: a copy then gets
            // "- Copy" naming and a move is a no-op, instead of the item being written over itself.
            var parent = Path.GetDirectoryName(source);
            var inPlace = OpsPath.PathEquals(parent, _destination)
                || (parent is not null && OpsPath.PathEquals(OpsPath.RealPath(parent), _realDestination));
            if (_byRename[i])
            {
                if (!inPlace) MoveByRename(item, Path.Combine(_destination, item.Name));
                Job.AddDone(item is FileInfo file ? file.Length : 0, 1);
            }
            else if (move && inPlace)
            {
                SkipProgress(item); // moving an item onto itself: nothing to do
            }
            else
            {
                // Copying into the item's own folder gets Explorer's "name - Copy" naming instead of a clash.
                var target = inPlace
                    ? OpsPath.CopyName(_destination, item.Name, item is DirectoryInfo)
                    : Path.Combine(_destination, item.Name);
                Transfer(item, target);
            }
        }
    }

    // ---- same volume: rename -----------------------------------------------------------------------------------

    /// <summary>Same-volume move. True when the whole item moved.</summary>
    private bool MoveByRename(FileSystemInfo item, string target)
    {
        var kind = OpsPath.Classify(item);
        while (true)
        {
            Job.SetCurrent(item.FullName);
            FileAttributes? existing = null;
            if (!Attempt(target, () => existing = OpsPath.GetAttributes(target))) return false;

            if (existing is not { } destinationAttributes)
            {
                try
                {
                    if (!Attempt(item.FullName, () => Rename(item.FullName, target, kind))) return false;
                }
                catch (CrossVolumeException)
                {
                    return Transfer(item, target); // a folder mounted from another disk under the same drive letter
                }
                Job.RecordMoved(item.FullName, target);
                return true;
            }

            var destinationKind = OpsPath.Classify(target, destinationAttributes);
            if (kind == OpsItemKind.Directory && destinationKind == OpsItemKind.Directory)
                return MergeByRename((DirectoryInfo)item, target);

            switch (ResolveConflict(DescribeConflict(item, target, destinationAttributes)))
            {
                case ConflictChoice.Skip:
                    Job.AddSkipped(item.FullName);
                    return false;
                case ConflictChoice.KeepBoth:
                    target = OpsPath.NumberedName(target, kind != OpsItemKind.File);
                    continue;
                default: // Replace
                    if (kind == OpsItemKind.File && destinationKind == OpsItemKind.File)
                    {
                        var to = target;
                        if (!Attempt(item.FullName, () =>
                            {
                                OpsPath.MakeWritable(to);
                                File.Move(item.FullName, to, overwrite: true);
                            }))
                            return false;
                        Job.RecordMoved(item.FullName, target);
                        return true;
                    }
                    if (!RemoveExisting(target)) return false;
                    continue;
            }
        }
    }

    /// <summary>Moves a folder's children into the existing folder of the same name, then removes it if emptied.</summary>
    private bool MergeByRename(DirectoryInfo folder, string target)
    {
        var children = ListFolder(folder.FullName);
        if (children is null) return false;
        var all = true;
        foreach (var child in children)
        {
            Job.Checkpoint();
            all &= MoveByRename(child, Path.Combine(target, child.Name));
        }
        // A folder that still holds skipped children stays where it is.
        if (all) all = Attempt(folder.FullName, () => OpsPath.DeleteEmptyFolder(folder.FullName));
        return all;
    }

    private static void Rename(string source, string target, OpsItemKind kind)
    {
        try
        {
            // Directory.Move on a junction/symlink renames the link itself; its target is never entered.
            if (kind == OpsItemKind.File) File.Move(source, target);
            else Directory.Move(source, target);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows()
            ? (ex.HResult & 0xFFFF) == OpsNative.ERROR_NOT_SAME_DEVICE
            : ex.HResult == 18 /* EXDEV */)
        {
            throw new CrossVolumeException();
        }
    }

    // ---- copy (and cross-volume move) -------------------------------------------------------------------------

    /// <summary>
    /// Copies an item to <paramref name="target"/> (its full new path); when moving, also removes the source once its
    /// copy is complete. True when the whole item was transferred.
    /// </summary>
    private bool Transfer(FileSystemInfo item, string target)
    {
        Job.SetCurrent(item.FullName);
        switch (OpsPath.Classify(item))
        {
            case OpsItemKind.Link:
                // Never followed: a junction to elsewhere would drag in (or, moving, delete) things outside the
                // selection, and one pointing back up the tree would loop forever.
                Job.AddProblem(item.FullName, "Skipped link");
                Job.AddDone(0, 1);
                return false;
            case OpsItemKind.Directory:
                return TransferFolder((DirectoryInfo)item, target);
            default:
                return TransferFile((FileInfo)item, target);
        }
    }

    private bool TransferFile(FileInfo file, string target)
    {
        var baseBytes = Job.DoneBytes;
        while (true)
        {
            FileAttributes? existing = null;
            if (!Attempt(target, () => existing = OpsPath.GetAttributes(target))) return SkipFile(file, baseBytes);

            var overwrite = false;
            if (existing is { } destinationAttributes)
            {
                var choice = ResolveConflict(DescribeConflict(file, target, destinationAttributes));
                if (choice == ConflictChoice.Skip)
                {
                    Job.AddSkipped(file.FullName);
                    return SkipFile(file, baseBytes);
                }
                if (choice == ConflictChoice.KeepBoth) target = OpsPath.NumberedName(target, isDirectory: false);
                else if ((destinationAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    // Replacing a folder with a file: the folder goes first. A link goes too rather than being
                    // overwritten, which would write through it into whatever it points at.
                    if (!RemoveExisting(target)) return SkipFile(file, baseBytes);
                }
                else overwrite = true;
            }

            var to = target;
            try
            {
                if (!Attempt(file.FullName, () =>
                    {
                        if (overwrite) OpsPath.MakeWritable(to); // replacing a read-only file
                        _copier.Copy(file, to, overwrite, baseBytes);
                    }))
                    return SkipFile(file, baseBytes);
            }
            catch (DestinationExistsException)
            {
                continue; // something appeared at the destination meanwhile: settle the clash first
            }
            break;
        }

        Job.AddDone(0, 1);
        if (!move)
        {
            Job.RecordCreated(target);
            return true;
        }
        // Moving across volumes: the original goes only now that its copy is complete.
        if (Attempt(file.FullName, () => OpsPath.DeleteFile(file.FullName)))
        {
            Job.RecordMoved(file.FullName, target);
            return true;
        }
        Job.RecordCreated(target); // copied, but the original stayed: record what actually happened
        return false;
    }

    private bool SkipFile(FileInfo file, long baseBytes)
    {
        Job.SetDoneBytes(baseBytes + file.Length);
        Job.AddDone(0, 1);
        return false;
    }

    private bool TransferFolder(DirectoryInfo folder, string target)
    {
        bool merge;
        while (true)
        {
            FileAttributes? existing = null;
            if (!Attempt(target, () => existing = OpsPath.GetAttributes(target)))
            {
                SkipProgress(folder);
                return false;
            }
            if (existing is not { } destinationAttributes)
            {
                merge = false;
                break;
            }
            // Folder onto folder merges silently; clashes inside are asked per file.
            if (OpsPath.Classify(target, destinationAttributes) == OpsItemKind.Directory)
            {
                merge = true;
                break;
            }
            // Folder onto a file, or onto a link (never written through): ask.
            var choice = ResolveConflict(DescribeConflict(folder, target, destinationAttributes));
            if (choice == ConflictChoice.Skip)
            {
                Job.AddSkipped(folder.FullName);
                SkipProgress(folder);
                return false;
            }
            if (choice == ConflictChoice.KeepBoth) target = OpsPath.NumberedName(target, isDirectory: true);
            else if (!RemoveExisting(target))
            {
                SkipProgress(folder);
                return false;
            }
        }

        // List first: if the source can't be read there's no point leaving an empty folder at the destination.
        var children = ListFolder(folder.FullName);
        if (children is null)
        {
            Job.AddDone(0, 1);
            return false;
        }
        if (!merge)
        {
            var to = target;
            if (!Attempt(target, () => Directory.CreateDirectory(to)))
            {
                SkipProgress(folder);
                return false;
            }
            if (move) Job.RecordMoved(folder.FullName, target);
            else Job.RecordCreated(target);
        }

        var all = true;
        foreach (var child in children)
        {
            Job.Checkpoint();
            all &= Transfer(child, Path.Combine(target, child.Name));
        }
        // Folder times last: adding the contents would have bumped them. A merged folder keeps its own.
        if (!merge) ApplyFolderMetadata(folder, target);
        Job.AddDone(0, 1);
        // Moving: the source folder goes only if everything in it went (skipped children keep their folder).
        if (move && all) all = Attempt(folder.FullName, () => OpsPath.DeleteEmptyFolder(folder.FullName));
        return all;
    }

    private static void ApplyFolderMetadata(DirectoryInfo source, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Directory.SetCreationTimeUtc(target, source.CreationTimeUtc);
            Directory.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
            var attributes = source.Attributes & OpsPath.SettableAttributes;
            if (attributes != 0) File.SetAttributes(target, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }
}
