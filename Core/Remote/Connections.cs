using System.Text.Json;
using System.Text.Json.Serialization;
using PaxCommander.Platform.Win32;

namespace PaxCommander.Core.Remote;

public enum RemoteProtocol { Sftp, Ftps, Ftp }

/// <summary>A saved server. Holds no secrets: passwords/passphrases live in Windows Credential Manager.</summary>
public sealed class Connection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    /// <summary>Private key file for SFTP; empty = password login.</summary>
    public string KeyFile { get; set; } = "";
    public string StartPath { get; set; } = "";
    /// <summary>SHA-256 fingerprint of the server's SSH host key, trusted on first connect.</summary>
    public string? HostKey { get; set; }
    /// <summary>Thumbprint of an FTPS certificate the user accepted despite errors.</summary>
    public string? CertThumbprint { get; set; }

    [JsonIgnore] public int EffectivePort => Port > 0 ? Port : Protocol == RemoteProtocol.Sftp ? 22 : 21;
    [JsonIgnore] public string Scheme => Protocol switch { RemoteProtocol.Sftp => "sftp", RemoteProtocol.Ftps => "ftps", _ => "ftp" };
    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Name) ? Host : Name;
    /// <summary>"sftp://user@host:2222" for people (and the clipboard).</summary>
    [JsonIgnore] public string Url => $"{Scheme}://{(User.Length > 0 ? Uri.EscapeDataString(User) + "@" : "")}{Host}{(Port > 0 ? ":" + Port : "")}";

    public Connection Clone() => (Connection)MemberwiseClone();
}

/// <summary>Saved connections in connections.json, with their secrets in Credential Manager.</summary>
public static class Connections
{
    private static readonly List<Connection> List = [];
    private static string FilePath => AppPaths.File("connections.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static event Action? Changed;

    public static IReadOnlyList<Connection> All
    {
        get { lock (List) return List.ToList(); }
    }

    public static Connection? Find(string id)
    {
        lock (List) return List.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>Connections opened from a typed URL that weren't saved: kept for this session only.</summary>
    private static readonly Dictionary<string, Connection> Transient = [];

    public static Connection? Resolve(string id) => Find(id) ?? (Transient.TryGetValue(id, out var t) ? t : null);

    public static void AddTransient(Connection c) => Transient[c.Id] = c;

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var items = JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(FilePath), Json) ?? [];
            lock (List)
            {
                List.Clear();
                List.AddRange(items);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("connections load", ex);
        }
    }

    public static void Save(Connection c)
    {
        lock (List)
        {
            var i = List.FindIndex(x => x.Id == c.Id);
            if (i >= 0) List[i] = c;
            else List.Add(c);
        }
        Transient.Remove(c.Id);
        Persist();
        Changed?.Invoke();
    }

    public static void Delete(Connection c)
    {
        lock (List) List.RemoveAll(x => x.Id == c.Id);
        CredentialStore.Delete(c.Id);
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Remembers a password (or key passphrase) in Credential Manager; null forgets it.</summary>
    public static void SetSecret(Connection c, string? secret)
    {
        if (secret is null) CredentialStore.Delete(c.Id);
        else CredentialStore.Write(c.Id, $"{c.User}@{c.Host}", secret);
    }

    public static string? GetSecret(Connection c) => CredentialStore.Read(c.Id);

    /// <summary>Finds a saved connection for "sftp://user@host:port", or makes a session-only one.</summary>
    public static Connection? FromUrl(string url, out string path)
    {
        path = "/";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        RemoteProtocol? protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "sftp" or "ssh" or "scp" => RemoteProtocol.Sftp,
            "ftps" => RemoteProtocol.Ftps,
            "ftp" => RemoteProtocol.Ftp,
            _ => null,
        };
        if (protocol is null || string.IsNullOrEmpty(uri.Host)) return null;
        var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (string.IsNullOrEmpty(path)) path = "/";
        var port = uri.IsDefaultPort || uri.Port <= 0 ? 0 : uri.Port;
        var saved = All.FirstOrDefault(c => c.Protocol == protocol && c.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
                                            && (user.Length == 0 || c.User == user) && (port == 0 || c.EffectivePort == port));
        return saved ?? new Connection { Name = uri.Host, Protocol = protocol.Value, Host = uri.Host, Port = port, User = user };
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write("connections save", ex);
        }
    }
}

/// <summary>
/// Remote locations inside the app: "sftp://{connectionId}/abs/path" (also ftp/ftps). Remote paths always use '/'
/// and are case-sensitive.
/// </summary>
public static class RemotePath
{
    public static bool IsRemote(string? path) =>
        path is not null && (path.StartsWith("sftp://", StringComparison.Ordinal) || path.StartsWith("ftp://", StringComparison.Ordinal) || path.StartsWith("ftps://", StringComparison.Ordinal));

    public static string Make(Connection c, string remote) => $"{c.Scheme}://{c.Id}{Normalize(remote)}";

    public static bool TryParse(string path, out Connection? connection, out string remote)
    {
        connection = null;
        remote = "/";
        if (!IsRemote(path)) return false;
        var rest = path[(path.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest[..slash];
        remote = Normalize(slash < 0 ? "/" : rest[slash..]);
        connection = Connections.Resolve(id);
        return connection is not null;
    }

    public static string Normalize(string remote)
    {
        if (string.IsNullOrEmpty(remote)) return "/";
        remote = remote.Replace('\\', '/');
        if (!remote.StartsWith('/')) remote = "/" + remote;
        while (remote.Length > 1 && remote.EndsWith('/')) remote = remote[..^1];
        return remote;
    }

    /// <summary>The remote part of an in-app path ("/home/me/file.txt").</summary>
    public static string RemotePart(string path) => TryParse(path, out _, out var r) ? r : "/";

    /// <summary>"sftp://{id}" without the remote path.</summary>
    public static string Prefix(string path)
    {
        var start = path.IndexOf("://", StringComparison.Ordinal) + 3;
        var slash = path.IndexOf('/', start);
        return slash < 0 ? path : path[..slash];
    }

    public static bool IsRoot(string path) => RemotePart(path) == "/";

    public static string? Parent(string path)
    {
        var r = RemotePart(path);
        if (r == "/") return null;
        var i = r.LastIndexOf('/');
        return Prefix(path) + (i <= 0 ? "/" : r[..i]);
    }

    public static string Combine(string folder, string name)
    {
        var r = RemotePart(folder);
        return Prefix(folder) + (r == "/" ? "/" + name : r + "/" + name);
    }

    public static string Name(string path)
    {
        var r = RemotePart(path);
        return r == "/" ? "/" : r[(r.LastIndexOf('/') + 1)..];
    }

    public static string Trim(string path) => IsRemote(path) ? Prefix(path) + RemotePart(path) : path;

    /// <summary>A copy-able URL for people: sftp://user@host/path.</summary>
    public static string ToUrl(string path) =>
        TryParse(path, out var c, out var r) && c is not null ? c.Url + string.Join('/', r.Split('/').Select(Uri.EscapeDataString)) : path;
}
