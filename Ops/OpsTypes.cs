namespace pxdCommander.Ops;

public enum JobKind { Copy, Move, Delete, Recycle, Compress, Extract }

public enum JobState { Queued, Scanning, Running, Paused, Completed, Failed, Cancelled }

/// <summary>How to settle a name clash. ReplaceIfNewer replaces only when the source is newer, otherwise skips.</summary>
public enum ConflictChoice { Replace, Skip, KeepBoth, ReplaceIfNewer, Cancel }

public enum ErrorChoice { Retry, Skip, SkipAll, Cancel }

/// <summary>
/// A name clash. <see cref="IsDirectory"/> describes the incoming item, <see cref="DestinationIsDirectory"/> the one
/// already there; they differ for file↔folder clashes (Replace then removes the existing item first). Sizes are 0 for folders.
/// </summary>
public sealed record ConflictInfo(
    string SourcePath, string DestinationPath, bool IsDirectory,
    long SourceSize, DateTime SourceModifiedUtc, long DestinationSize, DateTime DestinationModifiedUtc)
{
    public bool DestinationIsDirectory { get; init; }
}

public sealed record ConflictAnswer(ConflictChoice Choice, bool ApplyToAll);

/// <summary>Implemented by the app. Called on worker threads; implementations marshal to the UI themselves.</summary>
public interface IJobUi
{
    Task<ConflictAnswer> ResolveConflictAsync(FileJob job, ConflictInfo conflict);
    Task<ErrorChoice> ResolveErrorAsync(FileJob job, string path, Exception error);
}

/// <summary>Ends a job as Failed with a message meant for the user.</summary>
internal sealed class JobFailedException(string message) : Exception(message);

/// <summary>The destination appeared between the conflict check and the write; the caller re-runs the conflict check.</summary>
internal sealed class DestinationExistsException(string path) : IOException($"“{Path.GetFileName(path)}” already exists.");

/// <summary>A same-volume rename turned out to cross volumes (mounted folder); the caller falls back to copy + delete.</summary>
internal sealed class CrossVolumeException() : IOException("The item is on a different volume.");
