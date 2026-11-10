using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO.Enumeration;
using Avalonia.Threading;
using pxdCommander.Search;

namespace pxdCommander.Core;

/// <summary>An observable list that can swap its whole contents with one Reset notification.</summary>
public sealed class ItemList : ObservableCollection<FileItem>
{
    public void ResetTo(IEnumerable<FileItem> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class SortSpec
{
    public SortKey Key { get; init; } = SortKey.Name;
    public bool Descending { get; init; }
    public bool FoldersFirst { get; init; } = true;

    public IComparer<FileItem> Comparer() => Comparer<FileItem>.Create(Compare);

    public int Compare(FileItem a, FileItem b)
    {
        if (FoldersFirst && a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;
        // Drives read best in letter order (C:, D:, …) rather than by label.
        if (a.IsDrive && b.IsDrive && Key is SortKey.Name or SortKey.Type or SortKey.Modified or SortKey.Created)
            return Descending ? string.CompareOrdinal(b.Path, a.Path) : string.CompareOrdinal(a.Path, b.Path);
        var c = Key switch
        {
            SortKey.Modified => a.Modified.CompareTo(b.Modified),
            SortKey.Created => a.Created.CompareTo(b.Created),
            SortKey.Size => a.EffectiveSize.CompareTo(b.EffectiveSize),
            SortKey.Type => string.Compare(a.IsDirectory ? "" : a.Extension, b.IsDirectory ? "" : b.Extension, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };
        if (c == 0) c = NaturalComparer.Instance.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Name, b.Name);
        return Descending ? -c : c;
    }
}

/// <summary>
/// The contents of one folder: loads in the background, follows changes on disk, and exposes a sorted, filtered
/// <see cref="Items"/> list for the views. All public members are for the UI thread.
/// </summary>
public sealed class FolderListing : IDisposable
{
    private readonly List<FileItem> _all = [];
    private readonly Dictionary<string, FileItem> _byName = new(PathUtil.Comparer);
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _pending = new(PathUtil.Comparer);
    private bool _pendingFull;
    private Timer? _debounce;
    private DispatcherTimer? _poll;
    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    public FolderListing(string path, SortSpec sort)
    {
        Path = path;
        Sort = sort;
    }

    public string Path { get; }
    public ItemList Items { get; } = new();
    public SortSpec Sort { get; private set; }
    public string Filter { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public bool IsLoaded { get; private set; }
    public string? Error { get; private set; }

    public int FileCount { get; private set; }
    public int FolderCount { get; private set; }
    /// <summary>Items hidden by the hidden-files setting or the filter.</summary>
    public int HiddenCount { get; private set; }
    public IReadOnlyList<FileItem> All => _all;

    /// <summary>Contents were replaced wholesale (load, sort, filter); views should restore selection/scroll.</summary>
    public event Action? Reset;
    /// <summary>Loading/error state or counts changed.</summary>
    public event Action? StateChanged;
    /// <summary>Items appeared or vanished through the watcher.</summary>
    public event Action<IReadOnlyList<FileItem>, IReadOnlyList<FileItem>>? Changed;

    public bool IsComputer => PathUtil.IsComputer(Path);
    /// <summary>This PC, Network or a computer's share list: not a real folder (no watcher, no sizes, no new files).</summary>
    public bool IsVirtual => PathUtil.IsVirtual(Path);

    public FileItem? Find(string name) => _byName.TryGetValue(name, out var i) ? i : null;

    // ---- Search results ------------------------------------------------------------------------------------------------

    /// <summary>A listing filled by a recursive search instead of a folder; nothing is watched.</summary>
    public bool IsSearch { get; private set; }

    public static FolderListing ForSearch(string root, SortSpec sort)
    {
        var l = new FolderListing(root, sort) { IsSearch = true };
        l.IsLoaded = true;
        l.IsLoading = true;
        return l;
    }

    /// <summary>Adds a batch of hits; they stream in unsorted (like any search tool) until the user sorts.</summary>
    public void AppendResults(IReadOnlyList<FileItem> hits)
    {
        if (_disposed || hits.Count == 0) return;
        var match = BuildFilter(Filter);
        _all.AddRange(hits);
        var visible = hits.Where(match).ToList();
        if (visible.Count > 48)
        {
            Items.ResetTo(Items.Concat(visible).ToList());
            Reset?.Invoke();
        }
        else
        {
            foreach (var v in visible) Items.Add(v);
        }
        foreach (var v in visible)
        {
            if (v.IsDirectory) FolderCount++;
            else FileCount++;
        }
        StateChanged?.Invoke();
    }

    public void SearchFinished(string? error = null)
    {
        IsLoading = false;
        Error = error;
        UpdateSizeRatios();
        StateChanged?.Invoke();
    }

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        StateChanged?.Invoke();

        try
        {
            var path = Path;
            var progressive = new List<FileItem>();
            var lastPost = Environment.TickCount64;
            var items = PathUtil.IsRemote(path) ? await ListRemoteAsync(path, cts.Token) : await Task.Run(() =>
            {
                if (PathUtil.IsComputer(path)) return ListDrives();
                if (PathUtil.IsNetworkRoot(path)) return ListNetwork(cts.Token);
                if (PathUtil.IsServer(path)) return ListShares(path);
                return Enumerate(path, cts.Token, batch =>
                {
                    // Big or slow folders show what's been found so far instead of a blank pane.
                    if (Environment.TickCount64 - lastPost < 400) return;
                    lastPost = Environment.TickCount64;
                    var snapshot = batch.ToList();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cts.IsCancellationRequested || IsLoaded) return;
                        ReplaceAll(snapshot);
                    });
                });
            }, cts.Token);

            if (cts.IsCancellationRequested || _disposed) return;
            ReplaceAll(items);
            IsLoaded = true;
            StartWatching();
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            // The password prompt was cancelled.
            Error = "Not connected.";
            ReplaceAll([]);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Error = ex switch
            {
                UnauthorizedAccessException => "Access denied — you don't have permission to open this folder.",
                DirectoryNotFoundException => "This folder no longer exists.",
                IOException io => io.Message,
                _ => ex.Message,
            };
            ReplaceAll([]);
        }
        finally
        {
            if (_loadCts == cts)
            {
                IsLoading = false;
                StateChanged?.Invoke();
            }
        }
    }

    private static List<FileItem> Enumerate(string path, CancellationToken ct, Action<List<FileItem>> progress)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var list = new List<FileItem>();
        var en = new FileSystemEnumerable<FileItem>(path, (ref FileSystemEntry e) =>
            new FileItem(e.ToFullPath(), e.FileName.ToString(), e.IsDirectory ? ItemKind.Folder : ItemKind.File,
                e.IsDirectory ? 0 : e.Length, ToLocal(e.LastWriteTimeUtc), ToLocal(e.CreationTimeUtc), e.Attributes), options);
        foreach (var item in en)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(item);
            if ((list.Count & 1023) == 0) progress(list);
        }
        return list;
    }

    /// <summary>A folder on an SFTP/FTP server. Dot-files count as hidden, as they would on the server.</summary>
    private static async Task<List<FileItem>> ListRemoteAsync(string path, CancellationToken ct)
    {
        if (!Remote.RemotePath.TryParse(path, out var conn, out var remote) || conn is null)
            throw new IOException("This connection no longer exists.");
        var fs = await Remote.RemoteSessions.GetAsync(conn, ct);
        var entries = await fs.ListAsync(remote, ct);
        return entries.Select(e => new FileItem(Remote.RemotePath.Combine(path, e.Name), e.Name, e.IsDirectory ? ItemKind.Folder : ItemKind.File,
            e.Size, e.ModifiedUtc == DateTime.MinValue ? DateTime.MinValue : e.ModifiedUtc.ToLocalTime(), DateTime.MinValue,
            (e.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal) | (e.Name.StartsWith('.') ? FileAttributes.Hidden : 0))).ToList();
    }

    private static DateTime ToLocal(DateTimeOffset utc)
    {
        try { return utc.LocalDateTime; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>"Network": the computers Explorer finds on the local network, each opening to its shared folders.</summary>
    private static List<FileItem> ListNetwork(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var computers = Platform.Win32.ShellThread.RunAsync(Platform.Win32.NetworkBrowse.ListComputers, ct).GetAwaiter().GetResult();
        return computers.Select(c => new FileItem(c.Path, c.Name, ItemKind.Folder, 0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory)
            { TypeName = "Computer" }).ToList();
    }

    /// <summary>\\SERVER: the folders that computer shares.</summary>
    private static List<FileItem> ListShares(string server)
    {
        if (!OperatingSystem.IsWindows()) return [];
        return Platform.Win32.NetworkBrowse.ListShares(server).Select(s =>
            new FileItem(s.Path, s.Name, ItemKind.Folder, 0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory)
                { TypeName = s.Remark.Length > 0 ? $"Shared folder · {s.Remark}" : "Shared folder" }).ToList();
    }

    /// <summary>"This PC": every drive with its capacity. Unresponsive network drives can't stall the listing.</summary>
    private static List<FileItem> ListDrives()
    {
        var tasks = DriveInfo.GetDrives().Select(d => Task.Run(() => DriveItem(d))).ToArray();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
        var list = new List<FileItem>();
        for (var i = 0; i < tasks.Length; i++)
        {
            if (tasks[i].IsCompletedSuccessfully && tasks[i].Result is { } item)
                list.Add(item);
        }
        return list;
    }

    public static FileItem? DriveItem(DriveInfo d)
    {
        try
        {
            var root = d.RootDirectory.FullName;
            var letter = root.TrimEnd('\\', '/');
            // Mounted ISOs look like DVD drives (and mounted VHDs like local disks); ask what's really behind them.
            var image = OperatingSystem.IsWindows() && d.DriveType is DriveType.CDRom or DriveType.Fixed or DriveType.Removable
                ? Platform.Win32.VirtualDrives.GetMountedImage(root)
                : null;
            var desc = image is not null ? $"Mounted {image.Kind}" : d.DriveType switch
            {
                DriveType.Fixed => "Local Disk",
                DriveType.Network => "Network Drive",
                DriveType.Removable => "Removable Disk",
                DriveType.CDRom => "CD/DVD Drive",
                DriveType.Ram => "RAM Disk",
                _ => "Drive",
            };
            var isDisc = d.DriveType == DriveType.CDRom;
            if (!d.IsReady)
                return new FileItem(root, $"{desc} ({letter})", ItemKind.Drive, 0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory)
                    { DriveDescription = desc + (isDisc ? " (empty)" : " (not ready)"), Image = image, IsDisc = isDisc };
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? desc : d.VolumeLabel;
            var detail = image is not null ? System.IO.Path.GetFileName(image.DisplayPath) : d.DriveFormat;
            return new FileItem(root, OperatingSystem.IsWindows() ? $"{label} ({letter})" : root, ItemKind.Drive, d.TotalSize,
                DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory)
            {
                DriveFree = d.AvailableFreeSpace,
                DriveDescription = desc + (string.IsNullOrEmpty(detail) ? "" : " · " + detail),
                Image = image,
                IsDisc = isDisc,
            };
        }
        catch
        {
            return null;
        }
    }

    private void ReplaceAll(List<FileItem> items)
    {
        // Keep existing instances (icons, sizes, selection) for names that are still there.
        var fresh = new List<FileItem>(items.Count);
        var seen = new Dictionary<string, FileItem>(PathUtil.Comparer);
        foreach (var n in items)
        {
            if (_byName.TryGetValue(n.Name, out var old) && old.IsDirectory == n.IsDirectory)
            {
                old.UpdateFrom(n);
                fresh.Add(old);
                seen[n.Name] = old;
            }
            else
            {
                fresh.Add(n);
                seen[n.Name] = n;
            }
        }
        _all.Clear();
        _all.AddRange(fresh);
        _byName.Clear();
        foreach (var (k, v) in seen) _byName[k] = v;
        foreach (var item in _all)
            if (item.Label == 0) item.Label = Labels.Get(item.Path);
        ApplyView();
    }

    public void SetSort(SortSpec sort)
    {
        Sort = sort;
        ApplyView();
    }

    public void SetFilter(string filter)
    {
        filter = filter.Trim();
        if (filter == Filter) return;
        Filter = filter;
        ApplyView();
    }

    /// <summary>Re-applies hidden-file settings, filter and sort to the full list.</summary>
    public void ApplyView()
    {
        var match = BuildFilter(Filter);
        var visible = new List<FileItem>(_all.Count);
        int files = 0, folders = 0, hidden = 0;
        foreach (var i in _all)
        {
            if (!IsVisible(i) || !match(i))
            {
                hidden++;
                if (i.IsSelected) i.IsSelected = false;
                continue;
            }
            visible.Add(i);
            if (i.IsDirectory) folders++; else files++;
        }
        visible.Sort(Sort.Compare);
        FileCount = files;
        FolderCount = folders;
        HiddenCount = hidden;
        Items.ResetTo(visible);
        UpdateSizeRatios();
        Reset?.Invoke();
        StateChanged?.Invoke();
    }

    public static bool IsVisible(FileItem i)
    {
        var s = AppSettings.Current;
        if (i.IsHidden && i.IsSystem) return s.ShowSystemFiles;
        if (i.IsHidden) return s.ShowHidden;
        return true;
    }

    private static Func<FileItem, bool> BuildFilter(string filter)
    {
        if (filter.Length == 0) return _ => true;
        if (filter.Contains('*') || filter.Contains('?'))
        {
            var pattern = filter;
            return i => FileSystemName.MatchesSimpleExpression(pattern, i.Name, ignoreCase: true);
        }
        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Recomputes the size-bar proportions after sizes change.</summary>
    public void UpdateSizeRatios()
    {
        long max = 0;
        foreach (var i in Items)
            max = Math.Max(max, i.Kind == ItemKind.Drive ? i.Size : i.EffectiveSize);
        foreach (var i in Items)
        {
            i.SizeRatio = i.Kind == ItemKind.Drive
                ? (i.Size > 0 ? (double)(i.Size - i.DriveFree) / i.Size : 0)
                : max > 0 && i.EffectiveSize > 0 ? (double)i.EffectiveSize / max : 0;
        }
    }

    /// <summary>Folder sizes changed: keep size sorting honest without disturbing other sorts.</summary>
    public void SizesChanged()
    {
        if (Sort.Key == SortKey.Size)
        {
            var sorted = Items.OrderBy(i => i, Sort.Comparer()).ToList();
            if (!sorted.SequenceEqual(Items))
            {
                Items.ResetTo(sorted);
                Reset?.Invoke();
            }
        }
        UpdateSizeRatios();
    }

    // ---- Watching ------------------------------------------------------------------------------------------------------

    private void StartWatching()
    {
        if (_watcher is not null || _poll is not null || IsVirtual || PathUtil.IsRemote(Path)) return;
        try
        {
            var w = new FileSystemWatcher(Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Queue(e.Name);
            w.Deleted += (_, e) => Queue(e.Name);
            w.Changed += (_, e) => Queue(e.Name);
            w.Renamed += (_, e) =>
            {
                Queue(e.OldName);
                Queue(e.Name);
            };
            w.Error += (_, _) => QueueFull();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception)
        {
            // Some network shares and odd file systems refuse change notifications; check now and then instead.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _poll.Tick += (_, _) =>
            {
                if (!IsLoading) _ = RefreshAsync();
            };
            _poll.Start();
        }
    }

    private void Queue(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_pending)
        {
            _pending.Add(name);
            ArmDebounce();
        }
    }

    private void QueueFull()
    {
        lock (_pending)
        {
            _pendingFull = true;
            ArmDebounce();
        }
    }

    private void ArmDebounce()
    {
        _debounce ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(150, Timeout.Infinite);
    }

    private void Flush()
    {
        string[] names;
        bool full;
        lock (_pending)
        {
            names = _pending.ToArray();
            full = _pendingFull || names.Length > 400;
            _pending.Clear();
            _pendingFull = false;
        }
        if (_disposed) return;
        if (full)
        {
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
            return;
        }
        // Stat off the UI thread: network shares can take a while to answer.
        var stats = names.Select(n => (Name: n, Item: FileItem.TryStat(System.IO.Path.Combine(Path, n)))).ToList();
        Dispatcher.UIThread.Post(() => Apply(stats));
    }

    private void Apply(List<(string Name, FileItem? Item)> stats)
    {
        if (_disposed || !IsLoaded) return;
        var added = new List<FileItem>();
        var removed = new List<FileItem>();
        var cmp = Sort.Comparer();
        var filter = BuildFilter(Filter);

        foreach (var (name, fresh) in stats)
        {
            _byName.TryGetValue(name, out var existing);
            if (fresh is null || (existing is not null && existing.IsDirectory != fresh.IsDirectory))
            {
                if (existing is null) continue;
                _all.Remove(existing);
                _byName.Remove(name);
                if (Items.Remove(existing)) removed.Add(existing);
                existing.IsSelected = false;
                if (fresh is null) continue;
                existing = null;
            }

            if (existing is null)
            {
                fresh.Label = Labels.Get(fresh.Path);
                _all.Add(fresh);
                _byName[name] = fresh;
                if (IsVisible(fresh) && filter(fresh))
                {
                    Insert(fresh, cmp);
                    added.Add(fresh);
                }
            }
            else
            {
                var moved = Sort.Key is SortKey.Modified or SortKey.Size && (existing.Modified != fresh.Modified || existing.Size != fresh.Size);
                existing.UpdateFrom(fresh);
                if (moved && Items.Remove(existing))
                    Insert(existing, cmp);
            }
        }

        if (added.Count > 0 || removed.Count > 0)
        {
            FileCount = Items.Count(i => !i.IsDirectory);
            FolderCount = Items.Count - FileCount;
            UpdateSizeRatios();
            Changed?.Invoke(added, removed);
            StateChanged?.Invoke();
        }
    }

    private void Insert(FileItem item, IComparer<FileItem> cmp)
    {
        int lo = 0, hi = Items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (cmp.Compare(Items[mid], item) <= 0) lo = mid + 1;
            else hi = mid;
        }
        Items.Insert(lo, item);
    }

    /// <summary>Re-reads the folder, keeping unchanged items (and their selection) in place.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed || IsSearch) return;
        if (IsVirtual || !IsLoaded)
        {
            await LoadAsync();
            return;
        }
        try
        {
            var path = Path;
            var items = PathUtil.IsRemote(path) ? await ListRemoteAsync(path, CancellationToken.None) : await Task.Run(() => Enumerate(path, CancellationToken.None, _ => { }));
            if (_disposed) return;
            var freshNames = new HashSet<string>(items.Select(i => i.Name), PathUtil.Comparer);
            var changes = items.Select(i => (i.Name, (FileItem?)i))
                .Concat(_all.Where(i => !freshNames.Contains(i.Name)).Select(i => (i.Name, (FileItem?)null)))
                .Where(c => c.Item2 is null || !_byName.TryGetValue(c.Name, out var e) || Differs(e, c.Item2))
                .ToList();
            if (changes.Count > 300)
                ReplaceAll(items);
            else if (changes.Count > 0)
                Apply(changes);
            if (Error is not null)
            {
                Error = null;
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Error = ex is DirectoryNotFoundException ? "This folder no longer exists." : ex.Message;
            StateChanged?.Invoke();
        }
    }

    private static bool Differs(FileItem a, FileItem b) =>
        a.Size != b.Size || a.Modified != b.Modified || a.Attributes != b.Attributes || a.IsDirectory != b.IsDirectory;

    public void Dispose()
    {
        _disposed = true;
        _loadCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _poll?.Stop();
    }
}

/// <summary>
/// Explorer-style selection over an <see cref="ItemList"/>: a set of selected items plus a caret (keyboard focus) and an
/// anchor for Shift ranges. The selection lives on the items' IsSelected flags so every view draws it the same way.
/// </summary>
public sealed class SelectionModel
{
    private readonly ItemList _items;
    private readonly HashSet<FileItem> _selected = [];

    public SelectionModel(ItemList items) => _items = items;

    public event Action? Changed;

    public FileItem? Caret { get; private set; }
    public FileItem? Anchor { get; set; }
    public int Count => _selected.Count;
    public IReadOnlyCollection<FileItem> Set => _selected;

    /// <summary>Selected items in list order.</summary>
    public List<FileItem> Ordered()
    {
        if (_selected.Count == 0) return [];
        if (_selected.Count == 1) return [.. _selected];
        return _items.Where(_selected.Contains).ToList();
    }

    public void SetCaret(FileItem? item)
    {
        if (Caret == item) return;
        if (Caret is not null) Caret.IsCaret = false;
        Caret = item;
        if (item is not null) item.IsCaret = true;
    }

    public void Clear(bool notify = true)
    {
        foreach (var i in _selected) i.IsSelected = false;
        _selected.Clear();
        if (notify) Changed?.Invoke();
    }

    public void SelectOnly(FileItem? item)
    {
        Clear(notify: false);
        if (item is not null)
        {
            _selected.Add(item);
            item.IsSelected = true;
        }
        Anchor = item;
        SetCaret(item);
        Changed?.Invoke();
    }

    public void Toggle(FileItem item)
    {
        if (_selected.Remove(item)) item.IsSelected = false;
        else
        {
            _selected.Add(item);
            item.IsSelected = true;
        }
        Anchor = item;
        SetCaret(item);
        Changed?.Invoke();
    }

    public void Add(IEnumerable<FileItem> items)
    {
        foreach (var i in items)
            if (_selected.Add(i)) i.IsSelected = true;
        Changed?.Invoke();
    }

    /// <summary>Anchor-to-<paramref name="to"/> range; <paramref name="additive"/> keeps what was already selected (Ctrl+Shift).</summary>
    public void SelectRange(FileItem to, bool additive)
    {
        var anchor = Anchor ?? Caret ?? to;
        var a = _items.IndexOf(anchor);
        var b = _items.IndexOf(to);
        if (a < 0) a = b;
        if (!additive) Clear(notify: false);
        for (var i = Math.Min(a, b); i <= Math.Max(a, b) && i >= 0; i++)
        {
            var item = _items[i];
            if (_selected.Add(item)) item.IsSelected = true;
        }
        SetCaret(to);
        Changed?.Invoke();
    }

    /// <summary>Selects exactly the items between two list indexes (rubber band).</summary>
    public void SelectIndexRange(int from, int to, IReadOnlyCollection<FileItem>? keep)
    {
        Clear(notify: false);
        if (keep is not null)
            foreach (var k in keep)
                if (_selected.Add(k)) k.IsSelected = true;
        for (var i = Math.Max(0, Math.Min(from, to)); i <= Math.Min(_items.Count - 1, Math.Max(from, to)); i++)
        {
            var item = _items[i];
            if (_selected.Add(item)) item.IsSelected = true;
        }
        Changed?.Invoke();
    }

    public void SelectAll()
    {
        foreach (var i in _items)
            if (_selected.Add(i)) i.IsSelected = true;
        Changed?.Invoke();
    }

    public void Invert()
    {
        foreach (var i in _items)
        {
            if (_selected.Remove(i)) i.IsSelected = false;
            else
            {
                _selected.Add(i);
                i.IsSelected = true;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Drops items that left the list (deleted, filtered out).</summary>
    public void Prune()
    {
        var present = new HashSet<FileItem>(_items);
        var gone = _selected.Where(i => !present.Contains(i)).ToList();
        foreach (var g in gone)
        {
            _selected.Remove(g);
            g.IsSelected = false;
        }
        if (Caret is not null && !present.Contains(Caret)) SetCaret(null);
        if (Anchor is not null && !present.Contains(Anchor)) Anchor = null;
        if (gone.Count > 0) Changed?.Invoke();
    }

    /// <summary>Selects items by name after a reload or an operation (e.g. the new folder, pasted files).</summary>
    public void SelectNames(IEnumerable<string> names, bool caretOnFirst = true)
    {
        var set = new HashSet<string>(names, PathUtil.Comparer);
        Clear(notify: false);
        FileItem? first = null;
        foreach (var i in _items)
        {
            if (!set.Contains(i.Name)) continue;
            _selected.Add(i);
            i.IsSelected = true;
            first ??= i;
        }
        if (caretOnFirst && first is not null)
        {
            SetCaret(first);
            Anchor = first;
        }
        Changed?.Invoke();
    }
}
