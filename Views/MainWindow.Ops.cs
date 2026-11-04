using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using PaxCommander.Core;
using PaxCommander.Ops;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Views;

/// <summary>File operations: clipboard, transfers, delete, rename, new items, archives — all undoable where possible.</summary>
public sealed partial class MainWindow
{
    private sealed class JobUi : IJobUi
    {
        public Task<ConflictAnswer> ResolveConflictAsync(FileJob job, ConflictInfo conflict) => Dialogs.ConflictAsync(conflict, job is null ? default : WhenDone(job));
        public Task<ErrorChoice> ResolveErrorAsync(FileJob job, string path, Exception error) => Dialogs.ErrorAsync(path, error, WhenDone(job));

        /// <summary>The engine stops waiting when a job is cancelled mid-prompt; the prompt should go away too.</summary>
        private static CancellationToken WhenDone(FileJob job)
        {
            var cts = new CancellationTokenSource();
            job.Completion.ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);
            return cts.Token;
        }
    }

    private sealed record JobOrigin(FolderView? SelectIn, bool ConsumeCut, FileItem? CaretAfter);

    private readonly Dictionary<Guid, JobOrigin> _jobOrigins = [];

    private void Track(FileJob job, FolderView? selectIn, bool consumeCut = false, FileItem? caretAfter = null)
    {
        _jobOrigins[job.Id] = new JobOrigin(selectIn, consumeCut, caretAfter);
        _ops.Poke();
    }

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"“{Path.GetFileName(PathUtil.Trim(paths[0]))}”" : Formatting.Count(paths.Count, "item");

    // ---- Transfers -----------------------------------------------------------------------------------------------------

    private FileJob? StartTransfer(IReadOnlyList<string> paths, string destination, bool move, FolderView? selectIn = null, bool fromCut = false)
    {
        if (paths.Count == 0 || PathUtil.IsVirtual(destination)) return null;
        if (StartRemoteTransfer(paths, destination, move, selectIn)) return null;
        var job = move ? _queue.Move(paths, destination) : _queue.Copy(paths, destination);
        Track(job, selectIn ?? Views().FirstOrDefault(v => PathUtil.Same(v.ActiveFolder, destination)), fromCut);
        return job;
    }

    public Task DropAsync(FolderView view, string target, IReadOnlyList<string> paths, DragDropEffects effect)
    {
        StartTransfer(paths, target, effect == DragDropEffects.Move, PathUtil.Same(view.ActiveFolder, target) ? view : null);
        return Task.CompletedTask;
    }

    private void CopyOrMoveToOtherPane(bool move)
    {
        if (CurrentView is not { } view || OtherView is not { } other)
        {
            Toasts.Show("Turn on two panes (Ctrl+Alt+D) to copy straight to the other side.");
            return;
        }
        var items = view.SelectedItems.Where(i => !i.IsDrive).Select(i => i.Path).ToList();
        if (items.Count == 0) return;
        StartTransfer(items, other.ActiveFolder, move, other);
    }

    private async Task CopyToClipboardAsync(bool cut)
    {
        if (CurrentView is not { } view) return;
        var items = view.SelectedItems.Where(i => !i.IsDrive).ToList();
        if (items.Count == 0) return;
        await FileClipboard.SetAsync(this, items, cut);
        Toasts.Show($"{(cut ? "Cut" : "Copied")} {Describe(items.Select(i => i.Path).ToList())} — paste with Ctrl+V", ToastKind.Success, seconds: 1.8);
    }

    private async Task PasteAsync(string? into = null)
    {
        if (CurrentView is not { } view) return;
        var dest = into ?? view.ActiveFolder;
        if (PathUtil.IsVirtual(dest) || view.IsSearch && into is null)
        {
            Toasts.Show("Open a folder to paste into.");
            return;
        }
        if (await FileClipboard.GetAsync(this) is { } clip)
        {
            StartTransfer(clip.Paths, dest, clip.Cut, PathUtil.Same(dest, view.ActiveFolder) ? view : null, fromCut: clip.Cut);
            return;
        }

        if (PathUtil.IsRemote(dest))
        {
            Toasts.Show("There are no files on the clipboard to upload.");
            return;
        }
        // No files on the clipboard: a screenshot or some text becomes a new file — Explorer can't do this.
        if (Clipboard is { } cb)
        {
            try
            {
                if (await cb.TryGetBitmapAsync() is { } bmp)
                {
                    var name = PathUtil.UniqueName(dest, $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}", ".png");
                    var path = Path.Combine(dest, name);
                    await Task.Run(() => bmp.Save(path));
                    PushCreated(path, "pasted image");
                    view.SelectWhenItAppears(name);
                    return;
                }
                if (await cb.TryGetTextAsync() is { Length: > 0 } text)
                {
                    var name = PathUtil.UniqueName(dest, $"Pasted text {DateTime.Now:yyyy-MM-dd HHmmss}", ".txt");
                    var path = Path.Combine(dest, name);
                    await File.WriteAllTextAsync(path, text);
                    PushCreated(path, "pasted text");
                    view.SelectWhenItAppears(name);
                    return;
                }
            }
            catch (Exception ex)
            {
                Toasts.Show("Couldn't paste: " + ex.Message, ToastKind.Error);
                return;
            }
        }
        Toasts.Show("There's nothing on the clipboard to paste.");
    }

    // ---- Delete --------------------------------------------------------------------------------------------------------

    private async Task DeleteSelectionAsync(bool permanent)
    {
        if (CurrentView is not { } view) return;
        var items = view.SelectedItems.Where(i => !i.IsDrive).ToList();
        if (items.Count == 0) return;
        if (await DeleteRemoteAsync(items)) return;
        var paths = items.Select(i => i.Path).ToList();
        var what = Describe(paths);

        var canRecycle = OperatingSystem.IsWindows() && paths.All(RecycleBin.IsSupported);
        if (!permanent && !canRecycle)
        {
            // Network and removable drives have no Recycle Bin: say so plainly instead of deleting for good silently.
            var ok = await Dialogs.ConfirmAsync($"Permanently delete {what}?",
                "This drive has no Recycle Bin (network or removable), so they'll be gone for good. This can't be undone.",
                "Delete permanently", danger: true);
            if (!ok) return;
            permanent = true;
        }
        else if (permanent)
        {
            var ok = await Dialogs.ConfirmAsync($"Permanently delete {what}?", "They won't go to the Recycle Bin. This can't be undone.", "Delete permanently", danger: true);
            if (!ok) return;
        }
        else if (AppSettings.Current.ConfirmRecycle)
        {
            var ok = await Dialogs.ConfirmAsync($"Move {what} to the Recycle Bin?", "You can undo this with Ctrl+Z.", "Move to Recycle Bin");
            if (!ok) return;
        }

        // Like Explorer, the caret lands on the next item that survives.
        FileItem? next = null;
        if (view.ActiveListing is { } listing)
        {
            var set = items.ToHashSet();
            var last = items.Select(i => listing.Items.IndexOf(i)).DefaultIfEmpty(-1).Max();
            next = listing.Items.Skip(last + 1).FirstOrDefault(i => !set.Contains(i))
                   ?? listing.Items.Take(Math.Max(0, last)).LastOrDefault(i => !set.Contains(i));
        }
        var job = _queue.Delete(paths, permanent);
        Track(job, view, caretAfter: next);
    }

    // ---- Rename --------------------------------------------------------------------------------------------------------

    public async Task<bool> RenameAsync(FolderView view, FileItem item, string newName)
    {
        newName = newName.Trim();
        if (item.IsRemote)
        {
            if (newName.Contains('/') || newName.Length == 0)
            {
                Toasts.Show("That isn't a valid name.", ToastKind.Error);
                return false;
            }
            return await RenameRemoteAsync(view, item, newName);
        }
        if (PathUtil.ValidateName(newName) is { } err)
        {
            Toasts.Show(err, ToastKind.Error);
            return false;
        }
        var dir = Path.GetDirectoryName(item.Path) ?? "";
        var target = Path.Combine(dir, newName);
        var caseOnly = string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && PathUtil.Exists(target))
        {
            Toasts.Show($"“{newName}” already exists here.", ToastKind.Error);
            return false;
        }
        var old = item.Path;
        var isDir = item.IsDirectory;
        try
        {
            await Task.Run(() => MovePath(old, target, isDir));
        }
        catch (Exception ex)
        {
            Toasts.Show($"Couldn't rename: {ex.Message}", ToastKind.Error);
            return false;
        }
        Labels.Moved(old, target);
        UndoStack.Push(new UndoEntry($"rename to “{newName}”",
            async () =>
            {
                await Task.Run(() => MovePath(target, old, isDir));
                Labels.Moved(target, old);
            },
            async () =>
            {
                await Task.Run(() => MovePath(old, target, isDir));
                Labels.Moved(old, target);
            }));
        view.SelectWhenItAppears(newName);
        return true;
    }

    /// <summary>Rename/move on one volume; a case-only rename goes through a temporary name (Windows ignores it otherwise).</summary>
    private static void MovePath(string from, string to, bool isDir)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && from != to)
        {
            var temp = from + ".pax-" + Guid.NewGuid().ToString("N")[..6];
            if (isDir) Directory.Move(from, temp);
            else File.Move(from, temp);
            from = temp;
        }
        if (isDir) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private async Task RenameSelectionAsync()
    {
        if (CurrentView is not { } view) return;
        var items = view.SelectedItems.Where(i => !i.IsDrive).ToList();
        if (items.Count == 0 && view.ActiveSelection?.Caret is { IsDrive: false } caret) items = [caret];
        if (items.Count == 0) return;
        if (items.Count == 1)
        {
            view.BeginRename(items[0]);
            return;
        }
        if (items.Any(i => i.IsRemote))
        {
            Toasts.Show("Rename server items one at a time (F2 on a single item).");
            return;
        }
        if (await BatchRename.ShowAsync(items) is not { Count: > 0 } plan) return;
        await ApplyBatchRename(view, plan.Select(p => (p.Item.Path, Path.Combine(Path.GetDirectoryName(p.Item.Path) ?? "", p.NewName), p.Item.IsDirectory)).ToList(), pushUndo: true);
    }

    /// <summary>
    /// Renames in two phases (everything to a temporary name first) so swaps like a→b, b→a work.
    /// </summary>
    private async Task ApplyBatchRename(FolderView? view, List<(string From, string To, bool IsDir)> plan, bool pushUndo)
    {
        var done = new List<(string From, string To, bool IsDir)>();
        string? error = null;
        await Task.Run(() =>
        {
            var temps = new List<(string Temp, string To, string From, bool IsDir)>();
            foreach (var (from, to, isDir) in plan)
            {
                try
                {
                    var temp = from + ".pax-" + Guid.NewGuid().ToString("N")[..8];
                    MovePath(from, temp, isDir);
                    temps.Add((temp, to, from, isDir));
                }
                catch (Exception ex)
                {
                    error ??= $"{Path.GetFileName(from)}: {ex.Message}";
                }
            }
            foreach (var (temp, to, from, isDir) in temps)
            {
                try
                {
                    MovePath(temp, to, isDir);
                    done.Add((from, to, isDir));
                }
                catch (Exception ex)
                {
                    // Put it back rather than leaving a temporary name behind.
                    try { MovePath(temp, from, isDir); } catch { }
                    error ??= $"{Path.GetFileName(from)}: {ex.Message}";
                }
            }
        });
        foreach (var (from, to, _) in done) Labels.Moved(from, to);
        if (error is not null) Toasts.Show("Some items weren't renamed — " + error, ToastKind.Error);
        else if (done.Count > 0) Toasts.Show($"Renamed {Formatting.Count(done.Count, "item")}", ToastKind.Success, seconds: 2);
        if (pushUndo && done.Count > 0)
        {
            UndoStack.Push(new UndoEntry($"rename of {Formatting.Count(done.Count, "item")}",
                () => ApplyBatchRename(null, done.Select(d => (d.To, d.From, d.IsDir)).ToList(), pushUndo: false),
                () => ApplyBatchRename(null, done, pushUndo: false)));
        }
        if (view is not null && done.Count > 0) view.SelectWhenItAppears(Path.GetFileName(done[0].To));
    }

    // ---- New items -----------------------------------------------------------------------------------------------------

    private string? WritableFolder(FolderView view)
    {
        var folder = view.ActiveFolder;
        if (PathUtil.IsVirtual(folder) || view.IsSearch)
        {
            Toasts.Show("Open a folder first.");
            return null;
        }
        return folder;
    }

    private async Task NewFolderAsync()
    {
        if (CurrentView is not { } view || WritableFolder(view) is not { } folder) return;
        if (PathUtil.IsRemote(folder))
        {
            await NewRemoteAsync(view, folder, "New folder", "", isFolder: true);
            return;
        }
        var name = PathUtil.UniqueName(folder, "New folder");
        var path = Path.Combine(folder, name);
        try
        {
            await Task.Run(() => Directory.CreateDirectory(path));
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't create the folder: " + ex.Message, ToastKind.Error);
            return;
        }
        PushCreated(path, "new folder");
        view.SelectWhenItAppears(name, rename: true);
    }

    private async Task NewFileAsync(string baseName, string ext)
    {
        if (CurrentView is not { } view || WritableFolder(view) is not { } folder) return;
        if (PathUtil.IsRemote(folder))
        {
            await NewRemoteAsync(view, folder, baseName, ext, isFolder: false);
            return;
        }
        var name = PathUtil.UniqueName(folder, baseName, ext);
        var path = Path.Combine(folder, name);
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, []));
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't create the file: " + ex.Message, ToastKind.Error);
            return;
        }
        PushCreated(path, "new file");
        view.SelectWhenItAppears(name, rename: true);
    }

    /// <summary>Undo for something the app just created: it goes to the Recycle Bin (or is removed if still empty).</summary>
    private void PushCreated(string path, string what)
    {
        UndoStack.Push(new UndoEntry($"{what} “{Path.GetFileName(path)}”", () => RemoveCreatedAsync([path])));
    }

    private async Task RemoveCreatedAsync(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(PathUtil.Exists).ToList();
        if (existing.Count == 0) return;
        var recyclable = OperatingSystem.IsWindows() && existing.All(RecycleBin.IsSupported);
        if (!recyclable)
        {
            var ok = await Dialogs.ConfirmAsync($"Undo by deleting {Describe(existing)}?",
                "There's no Recycle Bin on this drive, so undoing deletes them permanently.", "Delete permanently", danger: true);
            if (!ok) throw new OperationCanceledException();
        }
        var job = _queue.Delete(existing, permanent: !recyclable);
        _jobOrigins[job.Id] = new JobOrigin(null, false, null) { };
        _undoing.Add(job.Id);
        _ops.Poke();
        await job.Completion;
    }

    private readonly HashSet<Guid> _undoing = [];

    // ---- Archives ------------------------------------------------------------------------------------------------------

    private void CompressSelection()
    {
        if (CurrentView is not { } view) return;
        var items = view.SelectedItems.Where(i => !i.IsDrive).ToList();
        if (items.Count == 0) return;
        var folder = Path.GetDirectoryName(items[0].Path) ?? view.ActiveFolder;
        var stem = items.Count == 1
            ? (items[0].IsDirectory ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name))
            : PathUtil.DisplayName(folder);
        var zip = Path.Combine(folder, PathUtil.UniqueName(folder, stem, ".zip"));
        var job = _queue.Compress(items.Select(i => i.Path).ToList(), zip);
        Track(job, view);
    }

    private void Extract(FileItem zip, bool intoOwnFolder)
    {
        var parent = Path.GetDirectoryName(zip.Path) ?? "";
        var dest = intoOwnFolder ? Path.Combine(parent, PathUtil.UniqueName(parent, Path.GetFileNameWithoutExtension(zip.Name))) : parent;
        var job = _queue.Extract(zip.Path, dest);
        Track(job, CurrentView);
    }

    // ---- Opening -------------------------------------------------------------------------------------------------------

    public void OpenItems(FolderView view, IReadOnlyList<FileItem> items)
    {
        var remote = items.Where(i => i.IsRemote && !i.IsDirectory).ToList();
        if (remote.Count > 0)
        {
            _ = OpenRemoteFilesAsync(remote);
            items = items.Except(remote).ToList();
        }
        foreach (var item in items.Take(25))
        {
            try
            {
                Shell.Open(item.Path);
            }
            catch (Exception ex)
            {
                Toasts.Show($"Couldn't open “{item.Name}”: {ex.Message}", ToastKind.Error);
            }
        }
        if (items.Count > 25) Toasts.Show("Opened the first 25 items only.");
    }

    private async Task CopyPathsAsync(bool network = false)
    {
        if (CurrentView is not { } view || Clipboard is not { } cb) return;
        var paths = view.SelectedItems.Select(i => i.Path).ToList();
        if (paths.Count == 0 && !PathUtil.IsVirtual(view.ActiveFolder)) paths = [view.ActiveFolder];
        if (paths.Count == 0) return;
        if (network && OperatingSystem.IsWindows())
            paths = paths.Select(p => ShellInfo.GetUniversalPath(p) ?? p).ToList();
        await cb.SetTextAsync(string.Join(Environment.NewLine, paths));
        Toasts.Show(paths.Count == 1 ? $"Copied {paths[0]}" : $"Copied {paths.Count} paths", ToastKind.Success, seconds: 1.8);
    }

    // ---- Job results ---------------------------------------------------------------------------------------------------

    private void OnJobFinished(FileJob job)
    {
        _jobOrigins.Remove(job.Id, out var origin);
        var undoing = _undoing.Remove(job.Id);

        if (job.State == JobState.Failed)
            Toasts.Show($"{job.Title} failed: {job.ErrorMessage}", ToastKind.Error, seconds: 8);
        else if (job.Problems.Count > 0)
            Toasts.Show($"{job.Title}: {Formatting.Count(job.Problems.Count, "item")} skipped — {job.Problems[0].Reason}", ToastKind.Error, seconds: 8);

        if (!undoing) RecordUndo(job);

        if (job.Kind == JobKind.Move)
            foreach (var (from, to) in job.Moved) Labels.Moved(from, to);

        if (origin?.ConsumeCut == true && job.State == JobState.Completed)
            _ = FileClipboard.ConsumeCutAsync(this);

        // Select what arrived, once it's listed.
        var arrived = job.Kind switch
        {
            JobKind.Copy or JobKind.Compress or JobKind.Extract => job.Created,
            JobKind.Move => job.Moved.Select(m => m.To).ToList(),
            _ => [],
        };
        if (origin?.SelectIn is { } view && arrived.Count > 0)
        {
            var here = arrived.Where(p => PathUtil.Same(Path.GetDirectoryName(p), view.ActiveFolder)).ToList();
            if (here.Count > 0) view.SelectWhenItAppears(Path.GetFileName(here[0]));
        }
        if (origin?.CaretAfter is { } next && origin.SelectIn is { } v && v.ActiveListing?.Items.Contains(next) == true)
        {
            v.ActiveSelection?.SelectOnly(next);
            v.ActiveView?.BringIntoView(next);
        }

        // Network shares often don't report changes; refresh any tab looking at an affected folder.
        var touched = new HashSet<string>(PathUtil.Comparer);
        if (job.Destination is { } d) touched.Add(job.Kind == JobKind.Compress ? Path.GetDirectoryName(d) ?? d : d);
        foreach (var s in job.Sources) touched.Add(Path.GetDirectoryName(s) ?? s);
        foreach (var t in touched) FolderSizes.Invalidate(t);
        foreach (var view2 in Views())
        {
            if (touched.Contains(view2.ActiveFolder) && PathUtil.IsNetwork(view2.ActiveFolder))
                _ = view2.ActiveListing?.RefreshAsync();
        }
        _preview.Reload();
    }

    private void RecordUndo(FileJob job)
    {
        if (job.State is not (JobState.Completed or JobState.Cancelled or JobState.Failed)) return;
        switch (job.Kind)
        {
            case JobKind.Copy when job.Created.Count > 0:
            {
                var created = job.Created;
                var sources = job.Sources;
                var dest = job.Destination!;
                UndoStack.Push(new UndoEntry($"copy of {Describe(created)}", () => RemoveCreatedAsync(created),
                    () => WaitFor(_queue.Copy(sources, dest))));
                break;
            }
            case JobKind.Compress or JobKind.Extract when job.Created.Count > 0:
            {
                var created = job.Created;
                UndoStack.Push(new UndoEntry(job.Kind == JobKind.Compress ? $"zip {Describe(created)}" : $"extract of {Describe(created)}", () => RemoveCreatedAsync(created)));
                break;
            }
            case JobKind.Move when job.Moved.Count > 0:
            {
                var moved = job.Moved;
                var dest = job.Destination!;
                UndoStack.Push(new UndoEntry($"move of {Describe(moved.Select(m => m.To).ToList())}",
                    async () =>
                    {
                        foreach (var group in moved.GroupBy(m => Path.GetDirectoryName(m.From) ?? "", PathUtil.Comparer))
                            await WaitFor(_queue.Move(group.Select(m => m.To).ToList(), group.Key));
                    },
                    () => WaitFor(_queue.Move(moved.Select(m => m.From).ToList(), dest))));
                break;
            }
            case JobKind.Recycle when job.Deleted.Count > 0:
            {
                var deleted = job.Deleted;
                var since = job.StartedUtc.AddSeconds(-2);
                UndoStack.Push(new UndoEntry($"delete of {Describe(deleted)}",
                    async () =>
                    {
                        var restored = await Task.Run(() => RecycleBin.Restore(RecycleBin.Find(deleted, since)));
                        if (restored.Count < deleted.Count)
                            Toasts.Show($"Restored {restored.Count} of {deleted.Count} items — the rest are no longer in the Recycle Bin or their place is taken.", ToastKind.Error);
                    },
                    () => WaitFor(_queue.Delete(deleted, permanent: false))));
                break;
            }
        }
    }

    private async Task WaitFor(FileJob job)
    {
        _undoing.Add(job.Id);
        _ops.Poke();
        await job.Completion;
    }

    private async Task UndoAsync()
    {
        try
        {
            if (await UndoStack.UndoAsync() is { } label) Toasts.Show($"Undid {label}", ToastKind.Success, "Redo", () => _ = RedoAsync(), seconds: 3);
            else Toasts.Show("Nothing to undo.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't undo: " + ex.Message, ToastKind.Error);
        }
    }

    private async Task RedoAsync()
    {
        try
        {
            if (await UndoStack.RedoAsync() is { } label) Toasts.Show($"Redid {label}", ToastKind.Success, seconds: 2.5);
        }
        catch (Exception ex)
        {
            Toasts.Show("Couldn't redo: " + ex.Message, ToastKind.Error);
        }
    }

    private IEnumerable<FolderView> Views() => _panes.SelectMany(p => p.Tabs);
}
