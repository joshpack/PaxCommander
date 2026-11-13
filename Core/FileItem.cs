using Avalonia.Media;

namespace pxdCommander.Core;

public enum ItemKind { File, Folder, Drive }

/// <summary>
/// One entry in a listing. The file-system facts are fixed per instance except through <see cref="UpdateFrom"/>; the UI
/// state (icon, selection, computed folder size…) is observable so realized rows repaint when it changes.
/// </summary>
public sealed class FileItem : ObservableObject
{
    public FileItem(string path, string name, ItemKind kind, long size, DateTime modified, DateTime created, FileAttributes attributes)
    {
        Path = path;
        Name = name;
        Kind = kind;
        _size = size;
        _modified = modified;
        _created = created;
        _attributes = attributes;
        Extension = kind == ItemKind.File ? GetExtension(name) : "";
    }

    public static FileItem FromInfo(FileSystemInfo info)
    {
        var isDir = (info.Attributes & FileAttributes.Directory) != 0;
        return new FileItem(info.FullName, info.Name, isDir ? ItemKind.Folder : ItemKind.File,
            isDir ? 0 : ((FileInfo)info).Length, Safe(() => info.LastWriteTime), Safe(() => info.CreationTime), info.Attributes);
    }

    /// <summary>Fresh facts for a path, or null if it no longer exists.</summary>
    public static FileItem? TryStat(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (dir.Exists) return FromInfo(dir);
            var file = new FileInfo(path);
            if (file.Exists) return FromInfo(file);
        }
        catch
        {
            // Vanished or unreadable between the event and the stat.
        }
        return null;
    }

    private static DateTime Safe(Func<DateTime> get)
    {
        try { return get(); }
        catch { return DateTime.MinValue; }
    }

    public string Path { get; }
    public string Name { get; }
    public ItemKind Kind { get; }
    public string Extension { get; }

    public bool IsDirectory => Kind != ItemKind.File;
    public bool IsDrive => Kind == ItemKind.Drive;

    private long _size;
    /// <summary>File length in bytes; a drive's capacity. 0 for folders (see <see cref="FolderBytes"/>).</summary>
    public long Size { get => _size; private set => Set(ref _size, value); }

    private DateTime _modified;
    public DateTime Modified { get => _modified; private set => Set(ref _modified, value); }

    private DateTime _created;
    public DateTime Created { get => _created; private set => Set(ref _created, value); }

    private FileAttributes _attributes;
    public FileAttributes Attributes { get => _attributes; private set => Set(ref _attributes, value); }

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0 || (Name.StartsWith('.') && !OperatingSystem.IsWindows());
    public bool IsSystem => (Attributes & FileAttributes.System) != 0 && Kind != ItemKind.Drive;
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>On an SFTP/FTP server: no shell icons/thumbnails, transfers instead of local file operations.</summary>
    public bool IsRemote => PathUtil.IsRemote(Path);

    /// <summary>Copies the facts from a fresh stat of the same path (watcher refresh).</summary>
    public void UpdateFrom(FileItem fresh)
    {
        Size = fresh.Size;
        Modified = fresh.Modified;
        Created = fresh.Created;
        Attributes = fresh.Attributes;
    }

    // ---- Drive extras -------------------------------------------------------------------------------------------

    /// <summary>Free bytes on a drive item.</summary>
    public long DriveFree { get; init; }
    /// <summary>"Local Disk", "Network Drive"…</summary>
    public string? DriveDescription { get; init; }

    /// <summary>The disc image (ISO) or virtual disk this drive letter is mounted from, if it is one.</summary>
    public Platform.Win32.MountedImage? Image { get; init; }

    /// <summary>CD/DVD drives and mounted ISOs: read-only, so "0 bytes free" means nothing.</summary>
    public bool IsDisc { get; init; }

    // ---- Lazily filled / UI state -----------------------------------------------------------------------------------

    private string? _typeName;
    /// <summary>Explorer-style type name; filled lazily.</summary>
    public string? TypeName { get => _typeName; set => Set(ref _typeName, value); }

    /// <summary>Containing folder shown in search results.</summary>
    public string? Location { get; init; }

    private IImage? _icon;
    public IImage? Icon { get => _icon; set => Set(ref _icon, value); }
    internal bool IconRequested;
    internal double IconScale;

    private IImage? _thumbnail;
    public IImage? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    internal int ThumbnailRequestedSize;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isCaret;
    /// <summary>The keyboard focus item (dotted outline), separate from selection like in Explorer.</summary>
    public bool IsCaret { get => _isCaret; set => Set(ref _isCaret, value); }

    private bool _isCut;
    /// <summary>On the clipboard as "cut": drawn faded until pasted.</summary>
    public bool IsCut { get => _isCut; set => Set(ref _isCut, value); }

    private long _folderBytes = -1;
    /// <summary>Computed total size of a folder; -1 while unknown.</summary>
    public long FolderBytes { get => _folderBytes; set => Set(ref _folderBytes, value); }

    private bool _folderSizePartial;
    /// <summary>True while <see cref="FolderBytes"/> is still being added up.</summary>
    public bool FolderSizePartial { get => _folderSizePartial; set => Set(ref _folderSizePartial, value); }

    private bool _sizeIsStale;
    /// <summary>
    /// <see cref="FolderBytes"/> is the total remembered from an earlier count, shown at once (faded) while a fresh
    /// count runs quietly in the background.
    /// </summary>
    public bool SizeIsStale { get => _sizeIsStale; set => Set(ref _sizeIsStale, value); }

    private double _sizeRatio;
    /// <summary>Share of the largest item in the listing (0..1), drawn as a bar in the size column.</summary>
    public double SizeRatio { get => _sizeRatio; set => Set(ref _sizeRatio, value); }

    private int _label;
    /// <summary>Colour label index (0 = none), see <see cref="Labels"/>.</summary>
    public int Label { get => _label; set => Set(ref _label, value); }

    private char _git;
    /// <summary>Git status mark: 'M' modified, 'A' added, '?' untracked, 'D' deleted, 'R' renamed, '\0' clean.</summary>
    public char Git { get => _git; set => Set(ref _git, value); }

    /// <summary>Size used for sorting and bars: the file length, a folder's computed total, a drive's used space.</summary>
    public long EffectiveSize => Kind switch
    {
        ItemKind.File => Size,
        ItemKind.Drive => Size - DriveFree,
        _ => FolderBytes,
    };

    public static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..].ToLowerInvariant() : "";
    }

    public override string ToString() => Path;
}
