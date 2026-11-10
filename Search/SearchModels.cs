namespace pxdCommander.Search;

/// <summary>One search result. Size is 0 for folders; times are UTC.</summary>
public sealed record SearchHit(
    string Path, string Name, bool IsDirectory, long Size,
    DateTime ModifiedUtc, DateTime CreatedUtc, FileAttributes Attributes);

/// <summary>
/// Search progress. FoldersScanned counts folders whose listing was read, FilesScanned counts file entries examined,
/// Matches counts hits delivered through onHits. The last report of a run has Finished = true.
/// </summary>
public sealed record SearchProgress(long FoldersScanned, long FilesScanned, long Matches, string? CurrentFolder, bool Finished);

/// <summary>
/// IncludeHidden: also search hidden/system items and descend into hidden/system folders (a hidden: token in the query
/// overrides it). MaxResults: stop after this many hits (0 or less = unlimited). MaxParallelism: worker count
/// (0 = automatic: CPU count clamped to 4..12 locally, 16 on network drives and UNC paths).
/// </summary>
public sealed record SearchOptions(bool IncludeHidden = false, int MaxResults = 50_000, int MaxParallelism = 0);

/// <summary>
/// Folder totals. Bytes is the sum of file lengths (logical size, like Explorer's "Size"), Files/Folders count everything
/// below the folder (not the folder itself), Inaccessible counts folders whose listing could not be read.
/// Complete is false when the measurement was cancelled.
/// </summary>
public sealed record FolderSizeResult(long Bytes, long Files, long Folders, int Inaccessible, bool Complete);
