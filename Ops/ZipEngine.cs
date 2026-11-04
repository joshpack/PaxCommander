using System.IO.Compression;

namespace PaxCommander.Ops;

/// <summary>
/// Compress to a .zip: top-level items become entry roots, folders are recursed (empty ones get a directory entry),
/// links are skipped. Already-compressed formats are stored as-is. A cancelled or failed run leaves no partial zip.
/// </summary>
internal sealed class CompressRunner(FileJob job, IJobUi ui) : JobRunner(job, ui)
{
    // Deflating these again costs time and gains nothing.
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp4", ".mkv", ".mov", ".avi", ".webm", ".mp3", ".aac", ".m4a",
        ".flac", ".ogg", ".opus", ".zip", ".7z", ".rar", ".gz", ".xz", ".bz2", ".zst", ".docx", ".xlsx", ".pptx", ".pdf",
    };

    private readonly byte[] _buffer = new byte[1 << 20];
    private string _target = "";
    private string? _writePath; // the file being written; set only once this job created it, so cleanup never hits someone else's

    protected override void Validate()
    {
        _target = Job.Destination ?? "";
        RequireFullPath(_target);
        foreach (var source in Job.Sources)
        {
            RequireFullPath(source);
            if (OpsPath.IsRoot(source)) Fail($"“{source}” is a drive and can't be compressed.");
            if (OpsPath.PathEquals(source, _target)) Fail($"“{Path.GetFileName(_target)}” can't be compressed into itself.");
        }
        var folder = Path.GetDirectoryName(_target);
        if (folder is null || !Directory.Exists(folder)) Fail($"The folder for “{Path.GetFileName(_target)}” doesn't exist.");
    }

    protected override void Scan()
    {
        foreach (var source in Job.Sources) ScanInto(source);
    }

    protected override void Execute()
    {
        var replace = false;
        while (OpsPath.GetAttributes(_target) is { } existing)
        {
            var conflict = DescribeConflict(_target, false, Job.TotalBytes, DateTime.UtcNow, _target, existing);
            var choice = ResolveConflict(conflict);
            if (choice == ConflictChoice.Skip)
            {
                Job.AddSkipped(_target);
                Job.AddDone(Job.TotalBytes - Job.DoneBytes, Job.TotalItems - Job.DoneItems);
                return;
            }
            if (choice == ConflictChoice.KeepBoth)
            {
                _target = OpsPath.NumberedName(_target, isDirectory: false);
                continue;
            }
            if ((existing & FileAttributes.Directory) == 0)
            {
                replace = true;
                break;
            }
            if (!RemoveExisting(_target))
            {
                // A folder in the way that couldn't be removed (the Problem is recorded): nothing to write.
                Job.AddDone(Job.TotalBytes - Job.DoneBytes, Job.TotalItems - Job.DoneItems);
                return;
            }
        }

        // Replacing: write beside the old zip and swap at the end, so cancelling leaves the old one untouched.
        var writePath = replace
            ? Path.Combine(Path.GetDirectoryName(_target)!, $".{Path.GetFileName(_target)}.{Guid.NewGuid().ToString("N")[..8]}.tmp")
            : _target;
        FileStream? stream = null;
        // Buffered: the archive writes many small header and deflate chunks.
        if (!Attempt(_target, () => stream = new FileStream(writePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20)))
        {
            Job.AddDone(Job.TotalBytes - Job.DoneBytes, Job.TotalItems - Job.DoneItems);
            return;
        }
        _writePath = writePath;

        try
        {
            // Not disposed on failure: Dispose writes the central directory, pointless for a file about to be deleted.
            var archive = new ZipArchive(stream!, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var source in Job.Sources)
            {
                Job.Checkpoint();
                Job.SetCurrent(source);
                if (StatItem(source) is not { } item)
                {
                    Job.AddDone(0, 1);
                    continue;
                }
                AddItem(archive, item, item.Name);
            }
            Job.SetCurrent(_target);
            archive.Dispose();
        }
        finally
        {
            stream!.Dispose();
        }

        if (replace)
        {
            OpsPath.MakeWritable(_target);
            File.Move(writePath, _target, overwrite: true);
        }
        _writePath = null; // finished: nothing left to clean up
        Job.RecordCreated(_target);
    }

    protected override void Cleanup(bool succeeded)
    {
        if (_writePath is not null) OpsPath.DeletePartial(_writePath);
    }

    /// <summary>Adds an item (recursively for folders). True when it produced at least one entry.</summary>
    private bool AddItem(ZipArchive archive, FileSystemInfo item, string entryName)
    {
        Job.SetCurrent(item.FullName);
        switch (OpsPath.Classify(item))
        {
            case OpsItemKind.Link:
                Job.AddProblem(item.FullName, "Skipped link");
                Job.AddDone(0, 1);
                return false;
            case OpsItemKind.File:
                return AddFile(archive, (FileInfo)item, entryName);
        }

        var children = ListFolder(item.FullName);
        var added = false;
        foreach (var child in children ?? [])
        {
            Job.Checkpoint();
            added |= AddItem(archive, child, entryName + "/" + child.Name);
        }
        // A folder with nothing in it needs its own entry, or it would vanish from the zip.
        if (!added && children is not null)
        {
            var entry = archive.CreateEntry(entryName + "/");
            entry.LastWriteTime = ZipTime(item.LastWriteTime);
            added = true;
        }
        Job.AddDone(0, 1);
        return added;
    }

    private bool AddFile(ZipArchive archive, FileInfo file, string entryName)
    {
        // The zip itself can sit inside a folder being compressed.
        if (OpsPath.PathEquals(file.FullName, _writePath) || OpsPath.PathEquals(file.FullName, _target))
        {
            Job.AddDone(file.Length, 1);
            return false;
        }
        var baseBytes = Job.DoneBytes;
        FileStream? input = null;
        // Open the source before creating the entry: an entry can't be taken back once started.
        if (!Attempt(file.FullName, () => input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan)))
        {
            Job.SetDoneBytes(baseBytes + file.Length);
            Job.AddDone(0, 1);
            return false;
        }

        using (input)
        {
            var level = StoredExtensions.Contains(file.Extension) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = archive.CreateEntry(entryName, level);
            entry.LastWriteTime = ZipTime(file.LastWriteTime);
            // Read/write failures from here on can't be retried (the entry is half written): they fail the job.
            var output = entry.Open();
            long done = 0;
            int read;
            while ((read = input!.Read(_buffer, 0, _buffer.Length)) > 0)
            {
                Job.Checkpoint();
                output.Write(_buffer, 0, read);
                done += read;
                Job.SetDoneBytes(baseBytes + done);
            }
            output.Dispose();
        }
        Job.AddDone(0, 1);
        return true;
    }

    /// <summary>Zip stores DOS times, which only cover 1980–2107.</summary>
    private static DateTimeOffset ZipTime(DateTime local)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        return new DateTimeOffset(local < min ? min : local > max ? max : local);
    }
}

/// <summary>
/// Extract a .zip into a folder (created if needed). Entries that would land outside it ("Zip Slip") are refused.
/// Clashes are asked per file; folders merge.
/// </summary>
internal sealed class ExtractRunner(FileJob job, IJobUi ui) : JobRunner(job, ui)
{
    private readonly byte[] _buffer = new byte[1 << 20];
    private ZipArchive? _archive;
    private string _zipPath = "", _destination = "", _destinationPrefix = "";

    protected override void Validate()
    {
        _zipPath = Job.Sources.Count > 0 ? Job.Sources[0] : "";
        _destination = Job.Destination ?? "";
        RequireFullPath(_zipPath);
        RequireFullPath(_destination);
        if (OpsPath.GetAttributesQuiet(_destination) is { } attributes && (attributes & FileAttributes.Directory) == 0)
            Fail($"“{OpsPath.DisplayName(_destination)}” is a file, not a folder.");
        _destinationPrefix = Path.EndsInDirectorySeparator(_destination) ? _destination : _destination + Path.DirectorySeparatorChar;
    }

    protected override void Scan()
    {
        Job.SetCurrent(_zipPath);
        try
        {
            _archive = ZipFile.OpenRead(_zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Fail($"“{Path.GetFileName(_zipPath)}” can't be opened: {ex.Message}");
        }
        foreach (var entry in _archive!.Entries) Job.AddTotals(entry.Length, 1);
    }

    protected override void Execute()
    {
        // A destination folder this job creates is itself what Created lists (undo removes just that folder).
        if (!EnsureFolder(_destination, record: true)) Fail($"The folder “{OpsPath.DisplayName(_destination)}” couldn't be created.");

        foreach (var entry in _archive!.Entries)
        {
            Job.Checkpoint();
            var name = entry.FullName;
            Job.SetCurrent(name);
            var relative = name.Replace('\\', '/');
            var isFolder = relative.EndsWith('/');
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(_destination, relative.TrimEnd('/')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Job.AddProblem(name, ex.Message);
                Job.AddDone(entry.Length, 1);
                continue;
            }
            // Zip Slip: "../../x" or an absolute path must never write outside the destination. The check runs on the
            // fully normalised path — the same one the file APIs get.
            if (!target.StartsWith(_destinationPrefix, OpsPath.Comparison))
            {
                if (!OpsPath.PathEquals(target, _destination))
                    Job.AddProblem(name, "Blocked: it would be extracted outside the destination folder.");
                Job.AddDone(entry.Length, 1);
                continue;
            }
            if (isFolder)
            {
                EnsureFolder(target, record: true);
                Job.AddDone(0, 1);
                continue;
            }
            ExtractFile(entry, target);
        }
    }

    protected override void Cleanup(bool succeeded) => _archive?.Dispose();

    private void ExtractFile(ZipArchiveEntry entry, string target)
    {
        var baseBytes = Job.DoneBytes;
        if (!EnsureFolder(Path.GetDirectoryName(target)!, record: true))
        {
            SkipEntry(entry, baseBytes);
            return;
        }
        while (true)
        {
            FileAttributes? existing = null;
            if (!Attempt(target, () => existing = OpsPath.GetAttributes(target)))
            {
                SkipEntry(entry, baseBytes);
                return;
            }
            var overwrite = false;
            if (existing is { } destinationAttributes)
            {
                var source = Path.Combine(_zipPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var conflict = DescribeConflict(source, false, entry.Length, entry.LastWriteTime.UtcDateTime, target, destinationAttributes);
                var choice = ResolveConflict(conflict);
                if (choice == ConflictChoice.Skip)
                {
                    Job.AddSkipped(entry.FullName);
                    SkipEntry(entry, baseBytes);
                    return;
                }
                if (choice == ConflictChoice.KeepBoth) target = OpsPath.NumberedName(target, isDirectory: false);
                else if ((destinationAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    // A folder (or a link, never written through) in the way goes first.
                    if (!RemoveExisting(target))
                    {
                        SkipEntry(entry, baseBytes);
                        return;
                    }
                }
                else overwrite = true;
            }

            var to = target;
            try
            {
                if (!Attempt(entry.FullName, () =>
                    {
                        if (overwrite) OpsPath.MakeWritable(to);
                        WriteEntry(entry, to, overwrite, baseBytes);
                    }))
                {
                    SkipEntry(entry, baseBytes);
                    return;
                }
            }
            catch (DestinationExistsException)
            {
                continue;
            }
            break;
        }
        Job.RecordCreated(target);
        Job.AddDone(0, 1);
    }

    private void WriteEntry(ZipArchiveEntry entry, string target, bool overwrite, long baseBytes)
    {
        // Open the entry first: an unreadable entry (encrypted, unsupported method) then leaves no empty file behind.
        using var input = entry.Open();
        FileStream output;
        try
        {
            output = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
        }
        catch (IOException) when (!overwrite && OpsPath.Exists(target))
        {
            throw new DestinationExistsException(target);
        }

        var completed = false;
        try
        {
            using (output)
            {
                long done = 0;
                int read;
                while ((read = input.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    Job.Checkpoint();
                    output.Write(_buffer, 0, read);
                    done += read;
                    Job.SetDoneBytes(baseBytes + done);
                }
            }
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                OpsPath.DeletePartial(target);
                Job.SetDoneBytes(baseBytes);
            }
        }
        try { File.SetLastWriteTimeUtc(target, entry.LastWriteTime.UtcDateTime); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SkipEntry(ZipArchiveEntry entry, long baseBytes)
    {
        Job.SetDoneBytes(baseBytes + entry.Length);
        Job.AddDone(0, 1);
    }
}
