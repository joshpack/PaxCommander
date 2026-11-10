using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace pxdCommander.Core.Remote;

public sealed record RemoteEntry(string Path, string Name, bool IsDirectory, long Size, DateTime ModifiedUtc, bool IsLink);

/// <summary>One live session to a server. Calls are serialized: neither SFTP nor FTP clients like overlapping use.</summary>
public interface IRemoteFs : IDisposable
{
    Connection Connection { get; }
    /// <summary>The folder the server put us in after logging in.</summary>
    string HomePath { get; }
    bool IsConnected { get; }
    Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct);
    Task<RemoteEntry?> StatAsync(string path, CancellationToken ct);
    Task DownloadAsync(string path, Stream to, Action<long> progress, CancellationToken ct);
    Task UploadAsync(Stream from, string path, Action<long> progress, CancellationToken ct);
    Task DeleteFileAsync(string path, CancellationToken ct);
    Task DeleteDirectoryAsync(string path, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
}

/// <summary>Questions a connection may need to ask the user (wired up by the window).</summary>
public static class RemotePrompts
{
    /// <summary>(connection, fingerprint, previous fingerprint or null) → trust?</summary>
    public static Func<Connection, string, string?, Task<bool>>? TrustHostKey;
    /// <summary>(connection, certificate summary, errors) → accept?</summary>
    public static Func<Connection, string, string, Task<bool>>? TrustCertificate;
    /// <summary>(connection, what's needed, e.g. "Password") → (secret, remember) or null if cancelled.</summary>
    public static Func<Connection, string, Task<(string Secret, bool Remember)?>>? AskSecret;
}

public sealed class SftpFs : IRemoteFs
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SftpFs(Connection c, SftpClient client)
    {
        Connection = c;
        _client = client;
        HomePath = RemotePath.Normalize(client.WorkingDirectory);
    }

    public Connection Connection { get; }
    public string HomePath { get; }
    public bool IsConnected => _client.IsConnected;

    public static IRemoteFs Connect(Connection c, string? secret)
    {
        var client = new SftpClient(BuildInfo(c, secret));
        client.HostKeyReceived += (_, e) => e.CanTrust = CheckHostKey(c, e);
        client.Connect();
        return new SftpFs(c, client);
    }

    /// <summary>Login details for SFTP and the SSH shell alike.</summary>
    internal static ConnectionInfo BuildInfo(Connection c, string? secret)
    {
        AuthenticationMethod[] methods;
        if (!string.IsNullOrEmpty(c.KeyFile))
        {
            var key = string.IsNullOrEmpty(secret) ? new PrivateKeyFile(c.KeyFile) : new PrivateKeyFile(c.KeyFile, secret);
            methods = [new PrivateKeyAuthenticationMethod(c.User, key)];
        }
        else
        {
            // Many servers only offer keyboard-interactive for passwords; answer its prompts with the same password.
            var interactive = new KeyboardInteractiveAuthenticationMethod(c.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = secret ?? "";
            };
            methods = [new PasswordAuthenticationMethod(c.User, secret ?? ""), interactive];
        }
        return new ConnectionInfo(c.Host, c.EffectivePort, c.User, methods) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Trust on first use, then insist on the same key — the protection against someone in the middle.</summary>
    internal static bool CheckHostKey(Connection c, HostKeyEventArgs e)
    {
        var fingerprint = "SHA256:" + e.FingerPrintSHA256;
        if (c.HostKey == fingerprint) return true;
        var ask = RemotePrompts.TrustHostKey;
        if (ask is null) return false;
        // Connect runs on a worker thread, so waiting for the dialog here doesn't block the UI.
        var ok = ask(c, fingerprint, c.HostKey).GetAwaiter().GetResult();
        if (ok)
        {
            c.HostKey = fingerprint;
            if (Connections.Find(c.Id) is not null) Connections.Save(c);
        }
        return ok;
    }

    private async Task<T> Run<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(work, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Run(Action work, CancellationToken ct) => Run(() =>
    {
        work();
        return true;
    }, ct);

    public Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Run(() =>
        _client.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..")
            .Select(Entry)
            .ToList(), ct);

    private static RemoteEntry Entry(Renci.SshNet.Sftp.ISftpFile f) =>
        new(f.FullName, f.Name, f.IsDirectory, f.IsDirectory ? 0 : f.Length, f.LastWriteTimeUtc, f.IsSymbolicLink);

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Run(() =>
        _client.Exists(path) ? Entry(_client.Get(path)) : null, ct);

    public Task DownloadAsync(string path, Stream to, Action<long> progress, CancellationToken ct) => Run(() =>
    {
        using var reg = ct.Register(() => _client.Disconnect());
        _client.DownloadFile(path, to, done => progress((long)done));
    }, ct);

    public Task UploadAsync(Stream from, string path, Action<long> progress, CancellationToken ct) => Run(() =>
    {
        using var reg = ct.Register(() => _client.Disconnect());
        _client.UploadFile(from, path, true, done => progress((long)done));
    }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) => Run(() => _client.DeleteFile(path), ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => Run(() => _client.DeleteDirectory(path), ct);
    public Task RenameAsync(string from, string to, CancellationToken ct) => Run(() => _client.RenameFile(from, to), ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(() => _client.CreateDirectory(path), ct);

    public void Dispose()
    {
        try { _client.Disconnect(); } catch { }
        _client.Dispose();
    }
}

public sealed class FtpFs : IRemoteFs
{
    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FtpFs(Connection c, AsyncFtpClient client, string home)
    {
        Connection = c;
        _client = client;
        HomePath = RemotePath.Normalize(home);
    }

    public Connection Connection { get; }
    public string HomePath { get; }
    public bool IsConnected => _client.IsConnected;

    public static async Task<IRemoteFs> ConnectAsync(Connection c, string? secret, CancellationToken ct)
    {
        var user = string.IsNullOrEmpty(c.User) ? "anonymous" : c.User;
        var client = new AsyncFtpClient(c.Host, user, secret ?? "", c.EffectivePort);
        client.Config.EncryptionMode = c.Protocol == RemoteProtocol.Ftps ? FtpEncryptionMode.Auto : FtpEncryptionMode.None;
        client.Config.ConnectTimeout = 15000;
        client.ValidateCertificate += (_, e) => e.Accept = CheckCertificate(c, e);
        await client.Connect(ct);
        var home = await client.GetWorkingDirectory(ct);
        return new FtpFs(c, client, home);
    }

    private static bool CheckCertificate(Connection c, FtpSslValidationEventArgs e)
    {
        if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None) return true;
        var cert = e.Certificate is null ? null : new X509Certificate2(e.Certificate);
        var thumb = cert?.Thumbprint;
        if (thumb is not null && thumb == c.CertThumbprint) return true;
        var ask = RemotePrompts.TrustCertificate;
        if (ask is null || cert is null) return false;
        var ok = ask(c, $"{cert.Subject}\nIssued by {cert.Issuer}\nSHA-1 {cert.Thumbprint}", e.PolicyErrors.ToString()).GetAwaiter().GetResult();
        if (ok)
        {
            c.CertThumbprint = thumb;
            if (Connections.Find(c.Id) is not null) Connections.Save(c);
        }
        return ok;
    }

    private async Task<T> Run<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Run(async () =>
        (await _client.GetListing(path, FtpListOption.AllFiles, ct))
            .Where(i => i.Name is not "." and not "..")
            .Select(Entry)
            .ToList(), ct);

    private static RemoteEntry Entry(FtpListItem i) =>
        new(i.FullName, i.Name, i.Type == FtpObjectType.Directory, i.Type == FtpObjectType.Directory ? 0 : i.Size,
            i.Modified == DateTime.MinValue ? DateTime.MinValue : DateTime.SpecifyKind(i.Modified, DateTimeKind.Local).ToUniversalTime(),
            i.Type == FtpObjectType.Link);

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Run(async () =>
    {
        if (await _client.DirectoryExists(path, ct))
            return new RemoteEntry(path, RemotePath.Normalize(path).Split('/')[^1], true, 0, DateTime.MinValue, false);
        if (!await _client.FileExists(path, ct)) return null;
        var size = await _client.GetFileSize(path, -1, ct);
        return (RemoteEntry?)new RemoteEntry(path, RemotePath.Normalize(path).Split('/')[^1], false, size, DateTime.MinValue, false);
    }, ct);

    public Task DownloadAsync(string path, Stream to, Action<long> progress, CancellationToken ct) => Run(async () =>
        await _client.DownloadStream(to, path, 0, new Progress<FtpProgress>(p => progress(p.TransferredBytes)), ct), ct);

    public Task UploadAsync(Stream from, string path, Action<long> progress, CancellationToken ct) => Run(async () =>
        await _client.UploadStream(from, path, FtpRemoteExists.Overwrite, true, new Progress<FtpProgress>(p => progress(p.TransferredBytes)), ct), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) => Run(async () =>
    {
        await _client.DeleteFile(path, ct);
        return true;
    }, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => Run(async () =>
    {
        await _client.DeleteDirectory(path, ct);
        return true;
    }, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Run(async () =>
    {
        await _client.Rename(from, to, ct);
        return true;
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(async () => await _client.CreateDirectory(path, ct), ct);

    public void Dispose()
    {
        // Off the caller's thread: FluentFTP's disconnect resumes on the captured context, so waiting for it on the UI
        // thread would deadlock.
        var client = _client;
        Task.Run(async () =>
        {
            try { await client.Disconnect(); } catch { }
            client.Dispose();
        });

    }
}

/// <summary>
/// Keeps one browsing session per connection and opens extra ones for transfers (so a big upload doesn't freeze
/// browsing). Handles asking for a password when none is saved.
/// </summary>
public static class RemoteSessions
{
    private static readonly Dictionary<string, IRemoteFs> Browse = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>Passwords typed for this session only ("don't remember").</summary>
    private static readonly Dictionary<string, string> SessionSecrets = [];

    public static async Task<IRemoteFs> GetAsync(Connection c, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (Browse.TryGetValue(c.Id, out var fs) && fs.IsConnected) return fs;
            if (fs is not null) _ = Task.Run(fs.Dispose);
            fs = await OpenAsync(c, ct);
            Browse[c.Id] = fs;
            return fs;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A separate session for one transfer job; the caller disposes it.</summary>
    public static Task<IRemoteFs> OpenAsync(Connection c, CancellationToken ct = default) => OpenAsync(c, ct, retry: true);

    private static async Task<IRemoteFs> OpenAsync(Connection c, CancellationToken ct, bool retry)
    {
        var secret = Connections.GetSecret(c) ?? (SessionSecrets.TryGetValue(c.Id, out var s) ? s : null);
        var needsSecret = string.IsNullOrEmpty(c.KeyFile) && c.User.Length > 0;
        if (secret is null && needsSecret)
            secret = await Ask(c, "Password") ?? throw new OperationCanceledException();
        try
        {
            return c.Protocol == RemoteProtocol.Sftp
                ? await Task.Run(() => SftpFs.Connect(c, secret), ct)
                : await FtpFs.ConnectAsync(c, secret, ct);
        }
        catch (Exception ex) when (retry && IsAuthFailure(ex))
        {
            // Wrong or changed password: forget it and ask once more.
            SessionSecrets.Remove(c.Id);
            if (Connections.GetSecret(c) is not null) Connections.SetSecret(c, null);
            var again = await Ask(c, string.IsNullOrEmpty(c.KeyFile) ? "Password (the saved one was refused)" : "Key passphrase");
            if (again is null) throw new OperationCanceledException();
            return await OpenAsync(c, ct, retry: false);
        }
        catch (SshConnectionException ex) when (ex.Message.Contains("Key exchange negotiation failed") || ex.Message.Contains("host key", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The server's identity wasn't trusted, so the connection was closed.", ex);
        }
    }

    private static bool IsAuthFailure(Exception ex) =>
        ex is SshAuthenticationException || ex is FluentFTP.Exceptions.FtpAuthenticationException ||
        (ex is SshException && ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> Ask(Connection c, string what)
    {
        if (RemotePrompts.AskSecret is not { } ask) return null;
        var answer = await ask(c, what);
        if (answer is not { } a) return null;
        if (a.Remember && Connections.Find(c.Id) is not null) Connections.SetSecret(c, a.Secret);
        else SessionSecrets[c.Id] = a.Secret;
        return a.Secret;
    }

    /// <summary>Tests a connection with these settings and secret, without saving anything.</summary>
    public static async Task TestAsync(Connection c, string? secret, CancellationToken ct)
    {
        using var fs = c.Protocol == RemoteProtocol.Sftp
            ? await Task.Run(() => SftpFs.Connect(c, secret), ct)
            : await FtpFs.ConnectAsync(c, secret, ct);
        await fs.ListAsync(fs.HomePath, ct);
    }

    /// <summary>An interactive shell on an SSH server, logging in the same way as browsing it does.</summary>
    public static async Task<RemoteShell> OpenShellAsync(Connection c, CancellationToken ct = default)
    {
        await GetAsync(c, ct); // asks for a password / checks the host key first if needed
        var secret = Connections.GetSecret(c) ?? (SessionSecrets.TryGetValue(c.Id, out var s) ? s : null);
        return await Task.Run(() => RemoteShell.Connect(c, secret), ct);
    }

    public static void Forget(string connectionId)
    {
        SessionSecrets.Remove(connectionId);
        if (Browse.Remove(connectionId, out var fs)) Task.Run(fs.Dispose);
    }

    /// <summary>Downloads a remote file into a per-connection temp folder (for opening and previews).</summary>
    public static async Task<string> DownloadToTempAsync(string path, CancellationToken ct)
    {
        if (!RemotePath.TryParse(path, out var c, out var remote) || c is null) throw new IOException("Unknown connection.");
        var folder = Path.Combine(Path.GetTempPath(), "pxdCommander", "remote", c.Id, Path.GetDirectoryName(remote.TrimStart('/'))?.Replace('/', '\\') ?? "");
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, RemotePath.Name(path));
        var fs = await GetAsync(c, ct);
        var tmp = local + ".part";
        await using (var stream = File.Create(tmp))
            await fs.DownloadAsync(remote, stream, _ => { }, ct);
        File.Move(tmp, local, true);
        return local;
    }
}
