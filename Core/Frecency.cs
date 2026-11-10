using System.Text.Json;

namespace pxdCommander.Core;

/// <summary>
/// Remembers visited folders ranked by how often and how recently they were opened (the zoxide idea), so "Go to" can
/// find a folder from a couple of letters.
/// </summary>
public static class Frecency
{
    private sealed class Entry
    {
        public string Path { get; set; } = "";
        public double Rank { get; set; }
        public long Last { get; set; }
    }

    private static readonly Dictionary<string, Entry> Entries = new(PathUtil.Comparer);
    private static readonly object Gate = new();
    private static Timer? _saveTimer;
    private static string FilePath => AppPaths.File("history.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? [];
            lock (Gate)
                foreach (var e in list)
                    Entries[e.Path] = e;
        }
        catch (Exception ex)
        {
            CrashLog.Write("history load", ex);
        }
    }

    public static void Visit(string path)
    {
        if (PathUtil.IsVirtual(path)) return;
        // Session-only connections (typed URLs) would leave dead entries behind.
        if (PathUtil.IsRemote(path) && !(Remote.RemotePath.TryParse(path, out var c, out _) && c is not null && Remote.Connections.Find(c.Id) is not null)) return;
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out var e))
                Entries[path] = e = new Entry { Path = path };
            e.Rank += 1;
            e.Last = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Aging keeps the total bounded so old habits fade and the file stays small.
            if (Entries.Values.Sum(x => x.Rank) > 5000)
            {
                foreach (var x in Entries.Values) x.Rank *= 0.9;
                foreach (var dead in Entries.Values.Where(x => x.Rank < 1).Select(x => x.Path).ToList())
                    Entries.Remove(dead);
            }
        }
        _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(3000, Timeout.Infinite);
    }

    public static void Forget(string path)
    {
        lock (Gate) Entries.Remove(path);
        Save();
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
        Save();
    }

    /// <summary>Folders by score, best first.</summary>
    public static List<(string Path, double Score)> Ranked(int max = 200)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (Gate)
        {
            return Entries.Values
                .Select(e => (e.Path, Score: e.Rank * RecencyWeight(now - e.Last)))
                .OrderByDescending(x => x.Score)
                .Take(max)
                .ToList();
        }
    }

    private static double RecencyWeight(long ageSeconds) => ageSeconds switch
    {
        < 3600 => 4,
        < 86400 => 2,
        < 604800 => 0.5,
        _ => 0.25,
    };

    public static void Save()
    {
        try
        {
            List<Entry> copy;
            lock (Gate) copy = Entries.Values.ToList();
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
        }
        catch (Exception ex)
        {
            CrashLog.Write("history save", ex);
        }
    }
}

/// <summary>Colour labels the user puts on files and folders, kept by path in labels.json (the files themselves are untouched).</summary>
public static class Labels
{
    public static readonly (string Name, string Color)[] All =
    [
        ("None", "#00000000"),
        ("Red", "#F2666A"),
        ("Orange", "#F5934C"),
        ("Yellow", "#F5D04C"),
        ("Green", "#19F58C"),
        ("Blue", "#00C8FF"),
        ("Purple", "#B07CFF"),
    ];

    private static readonly Dictionary<string, int> ByPath = new(PathUtil.Comparer);
    private static readonly object Gate = new();
    private static string FilePath => AppPaths.File("labels.json");

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) ?? [];
            lock (Gate)
                foreach (var (k, v) in map)
                    ByPath[k] = v;
        }
        catch (Exception ex)
        {
            CrashLog.Write("labels load", ex);
        }
    }

    public static int Get(string path)
    {
        lock (Gate) return ByPath.TryGetValue(path, out var v) ? v : 0;
    }

    public static bool Any
    {
        get { lock (Gate) return ByPath.Count > 0; }
    }

    public static void Set(IEnumerable<string> paths, int label)
    {
        lock (Gate)
        {
            foreach (var p in paths)
            {
                if (label == 0) ByPath.Remove(p);
                else ByPath[p] = label;
            }
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Keeps labels attached when the app renames or moves things (including everything inside a moved folder).</summary>
    public static void Moved(string from, string to)
    {
        var changed = false;
        lock (Gate)
        {
            foreach (var key in ByPath.Keys.ToList())
            {
                if (!PathUtil.IsSameOrInside(from, key)) continue;
                var value = ByPath[key];
                ByPath.Remove(key);
                ByPath[to + key[PathUtil.Trim(from).Length..]] = value;
                changed = true;
            }
        }
        if (changed) Save();
    }

    public static List<(string Path, int Label)> Snapshot()
    {
        lock (Gate) return ByPath.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static void Save()
    {
        try
        {
            Dictionary<string, int> copy;
            lock (Gate) copy = new Dictionary<string, int>(ByPath);
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
        }
        catch (Exception ex)
        {
            CrashLog.Write("labels save", ex);
        }
    }
}
