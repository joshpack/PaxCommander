using System.Runtime.InteropServices;
using System.Text;

namespace pxdCommander.Platform.Win32;

/// <summary>
/// Secrets in Windows Credential Manager ("Generic credentials"): encrypted by Windows for this user account and never
/// written to the app's own files. Elsewhere nothing is persisted (the app asks each session).
/// </summary>
public static class CredentialStore
{
    private const string Prefix = "pxdCommander:";

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static bool Write(string key, string user, string secret)
    {
        if (!IsAvailable) return false;
        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new Credential
            {
                Type = 1, // CRED_TYPE_GENERIC
                TargetName = Prefix + key,
                CredentialBlobSize = blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE: survives sign-out, still only readable by this user
                UserName = user,
                Comment = "pxd Commander connection",
            };
            return CredWriteW(ref cred, 0);
        }
        finally
        {
            handle.Free();
            Array.Clear(blob);
        }
    }

    public static string? Read(string key)
    {
        if (!IsAvailable) return null;
        if (ReadRaw(Prefix + key) is { } found) return found.Secret;

        // Saved by a version from before the rename: move it to the new name the first time it's needed.
        if (ReadRaw(Core.Migration.FormerCredentialPrefix + key) is not { } former) return null;
        if (Write(key, former.User ?? "", former.Secret)) CredDeleteW(Core.Migration.FormerCredentialPrefix + key, 1, 0);
        return former.Secret;
    }

    private static (string? User, string Secret)? ReadRaw(string target)
    {
        if (!CredReadW(target, 1, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return (cred.UserName, "");
            return (cred.UserName, Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string key)
    {
        if (!IsAvailable) return;
        CredDeleteW(Prefix + key, 1, 0);
        CredDeleteW(Core.Migration.FormerCredentialPrefix + key, 1, 0); // one the rename hasn't moved yet
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
