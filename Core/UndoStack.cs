namespace PaxCommander.Core;

/// <summary>One reversible action. <see cref="Redo"/> is optional (some actions can only be undone).</summary>
public sealed record UndoEntry(string Label, Func<Task> Undo, Func<Task>? Redo = null);

/// <summary>App-wide undo/redo for file operations (rename, move, copy, new, delete to the Recycle Bin…).</summary>
public static class UndoStack
{
    private static readonly List<UndoEntry> Undos = [];
    private static readonly List<UndoEntry> Redos = [];
    private static bool _busy;

    public static event Action? Changed;

    public static string? NextUndo => Undos.Count > 0 ? Undos[^1].Label : null;
    public static string? NextRedo => Redos.Count > 0 ? Redos[^1].Label : null;

    public static void Push(UndoEntry entry)
    {
        Undos.Add(entry);
        if (Undos.Count > 60) Undos.RemoveAt(0);
        Redos.Clear();
        Changed?.Invoke();
    }

    /// <returns>The label of what was undone, or null if there was nothing (or it is already running).</returns>
    public static async Task<string?> UndoAsync()
    {
        if (_busy || Undos.Count == 0) return null;
        var e = Undos[^1];
        Undos.RemoveAt(Undos.Count - 1);
        _busy = true;
        try
        {
            await e.Undo();
            if (e.Redo is not null) Redos.Add(e);
            return e.Label;
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    public static async Task<string?> RedoAsync()
    {
        if (_busy || Redos.Count == 0) return null;
        var e = Redos[^1];
        Redos.RemoveAt(Redos.Count - 1);
        _busy = true;
        try
        {
            await e.Redo!();
            Undos.Add(e);
            return e.Label;
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }
}
