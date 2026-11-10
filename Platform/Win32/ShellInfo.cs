using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdCommander.Platform.Win32;

[SupportedOSPlatform("windows")]
public static class ShellInfo
{
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    private const string FolderKey = "\0folder"; // can't collide with an extension

    private static readonly ConcurrentDictionary<string, string> s_typeNames = new(StringComparer.Ordinal);

    /// <summary>Canonical names of the properties shown by <see cref="GetDetails"/>, in display order.</summary>
    private static readonly string[] s_detailNames =
    [
        "System.Image.Dimensions",
        "System.Photo.CameraManufacturer", "System.Photo.CameraModel", "System.Photo.DateTaken",
        "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
        "System.Media.Duration",
        "System.Video.FrameWidth", "System.Video.FrameHeight", "System.Video.FrameRate", "System.Video.TotalBitrate",
        "System.Audio.EncodingBitrate", "System.Audio.SampleRate", "System.Audio.ChannelCount",
        "System.Music.Artist", "System.Music.AlbumTitle", "System.Music.TrackNumber", "System.Music.Genre", "System.Media.Year",
        "System.Title", "System.Author", "System.Document.PageCount",
        "System.Company", "System.FileDescription", "System.FileVersion", "System.Software.ProductName", "System.Copyright",
        "System.Comment", "System.Keywords",
    ];

    // The schema's own label for the track number is just "#".
    private static readonly Dictionary<string, string> s_labelOverrides = new(StringComparer.Ordinal)
    {
        ["System.Music.TrackNumber"] = "Track",
    };

    private static volatile DetailProperty[]? s_detailProperties;

    private sealed record DetailProperty(string Name, NativeMethods.PROPERTYKEY Key, string Label);

    /// <summary>Explorer's type name ("Text Document", "File folder"), cached per extension. Null if unavailable.</summary>
    public static string? GetTypeName(string path, bool isDirectory)
    {
        string key = isDirectory ? FolderKey : Path.GetExtension(path ?? "").ToLowerInvariant();
        if (s_typeNames.TryGetValue(key, out string? cached))
            return cached;
        try
        {
            // SHGFI_USEFILEATTRIBUTES: only the registry is consulted, never the disk, so any thread will do.
            var info = new NativeMethods.SHFILEINFOW();
            string query = isDirectory ? "folder" : "file" + key;
            uint attributes = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
            if (NativeMethods.SHGetFileInfoW(query, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                    NativeMethods.SHGFI_TYPENAME | NativeMethods.SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero
                || string.IsNullOrEmpty(info.szTypeName))
                return null; // not cached, so a transient failure isn't remembered
            return s_typeNames.GetOrAdd(key, info.szTypeName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Explorer-style detail rows for the preview pane (dimensions, duration, bit rate, artist, album, camera, date taken,
    /// page count, authors…), formatted for display, empty values left out. Call on a <see cref="ShellThread"/>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetDetails(string path)
    {
        if (string.IsNullOrEmpty(path))
            return [];
        IPropertyStore? store = null;
        try
        {
            DetailProperty[] properties = GetDetailProperties();
            if (properties.Length == 0)
                return [];
            if (NativeMethods.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, NativeMethods.GPS_BESTEFFORT,
                    NativeMethods.IID_IPropertyStore, out IntPtr unknown) < 0 || unknown == IntPtr.Zero)
                return [];
            store = ComInterop.Take<IPropertyStore>(unknown);

            var rows = new List<KeyValuePair<string, string>>();
            string? artist = null;
            foreach (DetailProperty property in properties)
            {
                NativeMethods.PROPERTYKEY key = property.Key;
                var value = default(NativeMethods.PROPVARIANT);
                try
                {
                    if (store.GetValue(ref key, out value) < 0 || value.vt == NativeMethods.VT_EMPTY)
                        continue;
                    if (NativeMethods.PSFormatForDisplayAlloc(key, value, NativeMethods.PDFF_DEFAULT, out IntPtr text) < 0)
                        continue;
                    string display = CleanDisplayText(TakeCoTaskString(text));
                    if (display.Length == 0)
                        continue;
                    // Music files repeat the artist as System.Author; one "Contributing artists" row is enough.
                    if (property.Name == "System.Music.Artist")
                        artist = display;
                    else if (property.Name == "System.Author" && display == artist)
                        continue;
                    rows.Add(new KeyValuePair<string, string>(property.Label, display));
                }
                finally
                {
                    NativeMethods.PropVariantClear(ref value);
                }
            }
            return rows;
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            ComInterop.Release(store);
        }
    }

    /// <summary>\\server\share\… for a path on a mapped network drive; null when the path isn't on one.</summary>
    public static unsafe string? GetUniversalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return null;
        if (path.Length > 2 && path[2] is not ('\\' or '/'))
            return null; // drive-relative ("H:foo") — no meaningful UNC form
        try
        {
            string rest = path.Length > 2 ? path[2..].Replace('/', '\\') : "";

            // The drive mapping table is local, so this answers instantly even when the server is unreachable.
            string? remote = GetConnection(path[..2]);
            if (remote != null)
                return remote.TrimEnd('\\') + rest;

            // Some network providers only answer the universal-name query.
            var info = new byte[4096];
            int size = info.Length;
            fixed (byte* p = info)
            {
                if (NativeMethods.WNetGetUniversalNameW(path, NativeMethods.UNIVERSAL_NAME_INFO_LEVEL, p, ref size) == NativeMethods.ERROR_SUCCESS)
                {
                    // UNIVERSAL_NAME_INFO is a single pointer into the same buffer.
                    string? universal = Marshal.PtrToStringUni(*(IntPtr*)p);
                    if (universal != null && universal.StartsWith(@"\\", StringComparison.Ordinal))
                        return universal;
                }
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"\\server\share" behind a drive letter such as "H:", or null when it isn't a network mapping.</summary>
    private static unsafe string? GetConnection(string drive)
    {
        int length = 512;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new char[length];
            int capacity = length;
            int error;
            fixed (char* p = buffer)
                error = NativeMethods.WNetGetConnectionW(drive, p, ref capacity);
            // ERROR_CONNECTION_UNAVAIL = a remembered mapping that isn't connected right now; the name is still valid.
            if (error is NativeMethods.ERROR_SUCCESS or NativeMethods.ERROR_CONNECTION_UNAVAIL)
            {
                int end = Array.IndexOf(buffer, '\0');
                string remote = new(buffer, 0, end < 0 ? buffer.Length : end);
                return remote.StartsWith(@"\\", StringComparison.Ordinal) ? remote : null;
            }
            if (error != NativeMethods.ERROR_MORE_DATA || capacity <= length || capacity > 32768)
                return null;
            length = capacity;
        }
        return null;
    }

    /// <summary>Path of a known folder (e.g. <see cref="Downloads"/>), or null.</summary>
    public static string? GetKnownFolderPath(Guid knownFolderId)
    {
        IntPtr path = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.SHGetKnownFolderPath(knownFolderId, NativeMethods.KF_FLAG_DEFAULT, IntPtr.Zero, out path);
            return hr >= 0 && path != IntPtr.Zero ? Marshal.PtrToStringUni(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            // Freed whether or not the call succeeded, as the API requires.
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>
    /// Property keys and localized labels, resolved once. A failed lookup (e.g. COM not ready on the first caller's
    /// thread) is not cached, so the next call retries.
    /// </summary>
    private static DetailProperty[] GetDetailProperties()
    {
        DetailProperty[]? cached = s_detailProperties;
        if (cached != null)
            return cached;

        var list = new List<DetailProperty>(s_detailNames.Length);
        bool complete = true;
        foreach (string name in s_detailNames)
        {
            if (NativeMethods.PSGetPropertyKeyFromName(name, out NativeMethods.PROPERTYKEY key) < 0)
            {
                complete = false;
                continue;
            }
            string? label = s_labelOverrides.GetValueOrDefault(name) ?? GetPropertyLabel(key);
            if (label == null)
            {
                complete = false;
                label = name[(name.LastIndexOf('.') + 1)..];
            }
            list.Add(new DetailProperty(name, key, label));
        }
        DetailProperty[] result = [.. list];
        if (complete)
            s_detailProperties = result;
        return result;
    }

    private static string? GetPropertyLabel(NativeMethods.PROPERTYKEY key)
    {
        IPropertyDescription? description = null;
        try
        {
            if (NativeMethods.PSGetPropertyDescription(key, NativeMethods.IID_IPropertyDescription, out IntPtr unknown) < 0 || unknown == IntPtr.Zero)
                return null;
            description = ComInterop.Take<IPropertyDescription>(unknown);
            if (description.GetDisplayName(out IntPtr name) < 0)
                return null;
            string label = TakeCoTaskString(name);
            return label.Length > 0 ? label : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ComInterop.Release(description);
        }
    }

    private static string TakeCoTaskString(IntPtr text)
    {
        try
        {
            return text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>
    /// The property system wraps some values in Unicode direction marks ("‪640 x 480‬") so they read correctly in
    /// right-to-left UIs; they are invisible noise for us and break copy/paste and comparisons.
    /// </summary>
    internal static string CleanDisplayText(string text)
    {
        if (text.Length == 0)
            return text;
        var chars = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
                continue;
            chars.Append(c);
        }
        return chars.ToString().Trim();
    }
}
