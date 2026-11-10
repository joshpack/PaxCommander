using System.ComponentModel;
using System.Runtime.InteropServices;

namespace pxdCommander.Platform.Win32;

/// <summary>
/// The local network, the way Explorer shows it: computers (from the shell's Network folder, which uses the same
/// discovery as Explorer), the shared folders on a computer, and mapping / disconnecting network drives.
/// </summary>
public static class NetworkBrowse
{
    public sealed record Computer(string Name, string Path);
    public sealed record Share(string Name, string Path, string Remark);

    /// <summary>Computers Explorer would show under Network. Runs on an STA thread (shell COM).</summary>
    public static List<Computer> ListComputers()
    {
        var list = new List<Computer>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return list;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic? folder = shell.NameSpace(18); // ssfNETWORK
            if (folder is null) return list;
            foreach (var item in folder.Items())
            {
                string path = item.Path ?? "";
                string name = item.Name ?? "";
                // Computers come back as \\NAME; media servers, routers and printers are something else.
                if (path.StartsWith(@"\\", StringComparison.Ordinal) && path.IndexOf('\\', 2) < 0)
                    list.Add(new Computer(name.Length > 0 ? name : path[2..], path));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        return list.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The shared folders on <paramref name="server"/> (\\NAME), without hidden admin shares (C$, IPC$).</summary>
    public static List<Share> ListShares(string server)
    {
        var host = server.TrimStart('\\').TrimEnd('\\');
        var status = NetShareEnum(@"\\" + host, 1, out var buffer, -1, out var read, out _, IntPtr.Zero);
        try
        {
            if (status != 0 && status != 234) // 234 = ERROR_MORE_DATA
                throw new IOException(status switch
                {
                    5 => $"Access to {server} was denied. Map one of its folders with a username and password to sign in.",
                    53 => $"Can't find {server} on the network.",
                    _ => $"Couldn't list the shared folders on {server}: {new Win32Exception(status).Message}",
                });
            var size = Marshal.SizeOf<ShareInfo1>();
            var list = new List<Share>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(buffer + i * size);
                if ((info.Type & 0xFF) != 0 || info.NetName.EndsWith('$')) continue; // disk shares only, no hidden ones
                list.Add(new Share(info.NetName, $@"\\{host}\{info.NetName}", info.Remark ?? ""));
            }
            return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    /// <summary>Drive letters not in use, Z: first (the usual pick for network drives).</summary>
    public static List<string> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => (char)c).Where(c => !used.Contains(c)).Select(c => c + ":").ToList();
    }

    /// <summary>
    /// Maps <paramref name="remote"/> (\\server\share) to <paramref name="letter"/>. With a username, those
    /// credentials are used; <paramref name="rememberCredentials"/> saves them in Windows Credential Manager (as
    /// Windows' own "Remember my credentials" does), so Windows reconnects without asking.
    /// </summary>
    public static void MapDrive(string letter, string remote, bool reconnectAtSignIn, string? user, string? password, bool rememberCredentials)
    {
        var resource = new NetResource { Type = 1, LocalName = letter, RemoteName = remote.TrimEnd('\\') }; // RESOURCETYPE_DISK
        var flags = reconnectAtSignIn ? 1 : 0; // CONNECT_UPDATE_PROFILE
        var status = WNetAddConnection2(ref resource, string.IsNullOrEmpty(user) ? null : password, string.IsNullOrEmpty(user) ? null : user, flags);
        if (status != 0) throw new IOException(MapError(status, remote));
        if (rememberCredentials && !string.IsNullOrEmpty(user) && password is not null)
            SaveWindowsCredential(ServerOf(remote), user, password);
    }

    public static void Disconnect(string letter)
    {
        var status = WNetCancelConnection2(letter.TrimEnd('\\'), 1, true); // update profile, force
        if (status != 0) throw new IOException($"Couldn't disconnect {letter}: {new Win32Exception(status).Message}");
    }

    /// <summary>\\server\share for a mapped drive letter, or null when it isn't a network drive.</summary>
    public static string? RemoteOf(string letter)
    {
        var size = 512;
        var sb = new System.Text.StringBuilder(size);
        return WNetGetConnection(letter.TrimEnd('\\'), sb, ref size) == 0 ? sb.ToString() : null;
    }

    private static string ServerOf(string remote) => remote.TrimStart('\\').Split('\\')[0];

    private static string MapError(int status, string remote) => status switch
    {
        5 or 86 or 1326 => "The username or password wasn't accepted.",
        53 or 67 => $"Can't find {remote}. Check the computer name and shared folder.",
        85 => "That drive letter is already in use.",
        1219 => $"Windows is already connected to {ServerOf(remote)} with a different username. Disconnect those drives first, or use the same account.",
        1203 or 1222 => "The network isn't available.",
        _ => new Win32Exception(status).Message,
    };

    /// <summary>A Windows (domain) credential for the server, the kind Explorer's "Remember my credentials" saves.</summary>
    private static void SaveWindowsCredential(string server, string user, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var cred = new NativeCredential
            {
                Type = 2, // CRED_TYPE_DOMAIN_PASSWORD
                TargetName = server,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = password.Length * 2,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string NetName;
        public uint Type;
        public string? Remark;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? user, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, System.Text.StringBuilder remoteName, ref int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);
}
