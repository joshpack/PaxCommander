using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PaxCommander.Platform.Win32;

/// <summary>A disc image or virtual disk mounted at a drive letter.</summary>
/// <param name="DriveRoot">"D:\".</param>
/// <param name="ImagePath">The image file as Windows knows it (often a UNC path for images on a share).</param>
/// <param name="DisplayPath">The same file through a mapped drive letter when there is one ("G:\…\game.iso").</param>
/// <param name="DevicePath">"\\.\CDROM0".</param>
/// <param name="StorageType">1 = ISO, 2 = VHD, 3 = VHDX, 4 = VHD set.</param>
public sealed record MountedImage(string DriveRoot, string ImagePath, string DisplayPath, string DevicePath, int StorageType)
{
    public string Kind => StorageType switch
    {
        1 => "ISO",
        2 => "VHD",
        3 => "VHDX",
        _ => "virtual disk",
    };
}

/// <summary>Finds which drive letters are mounted disc images, and mounts/unmounts them like Explorer does.</summary>
[SupportedOSPlatform("windows")]
public static class VirtualDrives
{
    private const int HostVolumesFlag = 1;               // GET_STORAGE_DEPENDENCY_FLAG_HOST_VOLUMES
    private const int ErrorInsufficientBuffer = 0x7A;
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    /// <summary>The image behind a drive letter, or null for a physical drive. Never throws; needs no elevation.</summary>
    public static MountedImage? GetMountedImage(string driveRoot)
    {
        try
        {
            var letter = driveRoot.TrimEnd('\\', '/');
            if (letter.Length != 2 || letter[1] != ':') return null;
            // Zero access rights are enough to ask the volume what it is stored on.
            using var volume = CreateFileW(@"\\.\" + letter, 0, 3, 0, 3, 0, 0);
            if (volume.IsInvalid) return null;

            var size = 4096;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                    Marshal.WriteInt32(buffer, 0, 2); // STORAGE_DEPENDENCY_INFO_VERSION_2
                    var rc = GetStorageDependencyInformation(volume, HostVolumesFlag, size, buffer, out var used);
                    if (rc == ErrorInsufficientBuffer && used > size)
                    {
                        size = used;
                        continue;
                    }
                    if (rc != 0 || Marshal.ReadInt32(buffer, 4) == 0) return null;
                    return Read(buffer + 8, driveRoot);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch
        {
            // Not something we can identify; treat it as a physical drive.
        }
        return null;
    }

    /// <summary>STORAGE_DEPENDENCY_INFO_TYPE_2: four ints and a GUID, then four string pointers starting at offset 32.</summary>
    private static MountedImage? Read(IntPtr entry, string driveRoot)
    {
        var storageType = Marshal.ReadInt32(entry, 8);
        string? Str(int index) => Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 32 + index * IntPtr.Size));
        var device = Str(0) ?? "";
        var host = Str(1) ?? "";
        var relative = Str(3) ?? "";
        if (host.Length == 0 && relative.Length == 0) return null;

        // Images on a local disk report a volume GUID; turn it into the drive it's mounted at.
        if (host.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            var names = new char[1024];
            if (GetVolumePathNamesForVolumeNameW(host.EndsWith('\\') ? host : host + "\\", names, names.Length, out _))
            {
                var first = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(first)) host = first;
            }
        }
        var image = host.TrimEnd('\\') + "\\" + relative.TrimStart('\\');
        return new MountedImage(driveRoot, image, ThroughMappedDrive(image), device, storageType);
    }

    /// <summary>"\\nas\Games\x.iso" → "G:\x.iso" when G: is mapped to that share; unchanged otherwise.</summary>
    public static string ThroughMappedDrive(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network))
        {
            var unc = ShellInfo.GetUniversalPath(drive.RootDirectory.FullName)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(unc)) continue;
            if (path.Equals(unc, StringComparison.OrdinalIgnoreCase))
                return drive.RootDirectory.FullName;
            if (path.StartsWith(unc + "\\", StringComparison.OrdinalIgnoreCase))
                return drive.RootDirectory.FullName.TrimEnd('\\') + path[unc.Length..];
        }
        return path;
    }

    /// <summary>Every mounted image, for "unmount" pickers.</summary>
    public static IReadOnlyList<MountedImage> All() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.CDRom or DriveType.Fixed or DriveType.Removable)
            .Select(d => GetMountedImage(d.RootDirectory.FullName))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

    /// <summary>
    /// Unmounts an image. Detaches it through the Virtual Disk API (what Explorer's Eject does underneath — no elevation
    /// needed for images the user mounted), falling back to the Storage service's Dismount-DiskImage. Returns null on
    /// success or a message saying why not. Slow-ish (up to a few seconds): call off the UI thread.
    /// </summary>
    public static string? Unmount(MountedImage image)
    {
        var error = TryDetach(image);
        if (error is null && WaitForDriveLetter(image.DriveRoot, present: false)) return null;

        var ps = RunWindowsPowerShell($"Dismount-DiskImage -DevicePath '{image.DevicePath.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        if (ps is null && WaitForDriveLetter(image.DriveRoot, present: false)) return null;
        return ps ?? error ?? $"{image.DriveRoot.TrimEnd('\\')} is still mounted — something may be using it.";
    }

    private static string? TryDetach(MountedImage image)
    {
        var type = new VirtualStorageType { DeviceId = image.StorageType, VendorId = MicrosoftVendor };
        // ISOs can only be opened read-only (READ includes DETACH); VHDs just need DETACH + GET_INFO.
        var access = image.StorageType == 1 ? 0x000d0000 : 0x000c0000;
        var rc = OpenVirtualDisk(ref type, image.ImagePath, access, 0, 0, out var handle);
        if (rc != 0) return new System.ComponentModel.Win32Exception(rc).Message;
        using (handle)
        {
            rc = DetachVirtualDisk(handle, 0, 0);
            return rc == 0 ? null : new System.ComponentModel.Win32Exception(rc).Message;
        }
    }

    /// <summary>Mounts an image (no Explorer window) and returns its drive root, or an error message.</summary>
    public static (string? DriveRoot, string? Error) Mount(string imagePath)
    {
        var before = DriveInfo.GetDrives().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var error = RunWindowsPowerShell($"Mount-DiskImage -ImagePath '{imagePath.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        if (error is not null) return (null, error);
        // The letter shows up a moment after the attach completes.
        for (var i = 0; i < 40; i++)
        {
            var added = DriveInfo.GetDrives().Select(d => d.Name).FirstOrDefault(n => !before.Contains(n));
            if (added is not null) return (added, null);
            var existing = DriveInfo.GetDrives().Select(d => GetMountedImage(d.Name)).FirstOrDefault(m => m is not null && SameFile(m.ImagePath, imagePath));
            if (existing is not null) return (existing.DriveRoot, null);
            Thread.Sleep(150);
        }
        return (null, "The image was mounted but didn't get a drive letter.");
    }

    /// <summary>The drive an image file is currently mounted at, or null.</summary>
    public static MountedImage? FindMount(string imagePath) =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom || d.DriveType == DriveType.Fixed)
            .Select(d => GetMountedImage(d.Name))
            .FirstOrDefault(m => m is not null && (SameFile(m.ImagePath, imagePath) || SameFile(m.DisplayPath, imagePath)));

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool WaitForDriveLetter(string driveRoot, bool present)
    {
        for (var i = 0; i < 30; i++)
        {
            var exists = DriveInfo.GetDrives().Any(d => d.Name.Equals(driveRoot, StringComparison.OrdinalIgnoreCase));
            if (exists == present) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    /// <summary>Runs a Storage cmdlet in Windows PowerShell with no window; null = success, else the error text.</summary>
    private static string? RunWindowsPowerShell(string command)
    {
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return "PowerShell didn't start.";
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                p.Kill();
                return "Timed out.";
            }
            if (p.ExitCode == 0) return null;
            var text = err.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("At line", StringComparison.Ordinal));
            return string.IsNullOrEmpty(text) ? $"PowerShell exit code {p.ExitCode}." : text;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public int DeviceId;
        public Guid VendorId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int GetStorageDependencyInformation(SafeFileHandle handle, int flags, int infoSize, IntPtr info, out int sizeUsed);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int OpenVirtualDisk(ref VirtualStorageType type, string path, int accessMask, int flags, IntPtr parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern int DetachVirtualDisk(SafeFileHandle handle, int flags, int providerSpecificFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volumeName, char[] names, int length, out int returned);
}
