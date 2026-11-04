using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaxCommander.Search;

/// <summary>
/// Streams one file at a time looking for content: needles (case-insensitive). Holds its own buffers, so each search
/// worker owns one and nothing is allocated per file beyond the handle. Not thread-safe.
/// </summary>
internal sealed class ContentScanner
{
    internal const long MaxFileSize = 50L * 1024 * 1024;
    private const int ChunkSize = 64 * 1024;
    private const int SniffSize = 4096;

    // Cloud-only placeholders (OneDrive etc.): reading one would download it, so content search leaves them alone.
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */
        | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private readonly ContentTerm[] _terms;
    private readonly bool[] _found;
    private readonly int _overlap; // chars kept between chunks so a needle split across two reads is still seen
    private readonly bool _hasNegative;
    private readonly byte[] _bytes = new byte[ChunkSize];
    private readonly char[] _chars;
    private readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    private readonly Decoder _utf16Le = new UnicodeEncoding(false, false, false).GetDecoder();
    private readonly Decoder _utf16Be = new UnicodeEncoding(true, false, false).GetDecoder();

    internal ContentScanner(ContentTerm[] terms)
    {
        _terms = terms;
        _found = new bool[terms.Length];
        int longest = 1;
        foreach (var t in terms)
        {
            longest = Math.Max(longest, t.Needle.Length);
            _hasNegative |= t.Negate;
        }
        _overlap = longest - 1;
        _chars = new char[_overlap + ChunkSize + 8];
    }

    /// <summary>
    /// True when the file satisfies every term: positive needles found, negative needles absent. Files that are not
    /// scanned (too big, binary, unreadable, cloud-only) contain nothing, so they fail positive terms and pass negative ones.
    /// Returns false when cancelled mid-file (the caller is stopping anyway).
    /// </summary>
    internal bool Matches(string path, ReadOnlySpan<char> name, long length, FileAttributes attributes, CancellationToken ct)
    {
        Array.Clear(_found);
        var ext = QueryFileTypes.ExtensionOf(name);
        if (length <= MaxFileSize && (attributes & CloudOnly) == 0 && !QueryFileTypes.IsKnownBinary(ext) && length > 0)
        {
            if (!Scan(path, ext, ct)) return false;
        }
        for (int i = 0; i < _terms.Length; i++)
            if (_found[i] == _terms[i].Negate) return false;
        return true;
    }

    /// <summary>Returns false only when cancelled.</summary>
    private bool Scan(string path, ReadOnlySpan<char> ext, CancellationToken ct)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // unreadable: treated as containing nothing
        }

        using (handle)
        {
            try
            {
                long offset = 0;
                int read = RandomAccess.Read(handle, _bytes, offset);
                if (read <= 0) return true;
                offset += read;

                Decoder decoder;
                int skip = 0;
                var head = _bytes.AsSpan(0, read);
                if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { decoder = _utf8; skip = 3; }
                else if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) { decoder = _utf16Le; skip = 2; }
                else if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) { decoder = _utf16Be; skip = 2; }
                else
                {
                    var sniff = head[..Math.Min(read, SniffSize)];
                    if (sniff.IndexOf((byte)0) < 0) decoder = _utf8;
                    else if (LooksLikeUtf16(sniff, out bool bigEndian)) decoder = bigEndian ? _utf16Be : _utf16Le;
                    else if (QueryFileTypes.IsKnownText(ext)) decoder = _utf8; // a stray NUL does not make a .txt binary
                    else return true; // binary
                }
                decoder.Reset();

                int carry = 0;
                ReadOnlySpan<byte> bytes = _bytes.AsSpan(skip, read - skip);
                while (true)
                {
                    int produced = decoder.GetChars(bytes, _chars.AsSpan(carry), flush: false);
                    int total = carry + produced;
                    if (Check(_chars.AsSpan(0, total))) return true;
                    carry = Math.Min(_overlap, total);
                    _chars.AsSpan(total - carry, carry).CopyTo(_chars); // memmove semantics, overlap is fine

                    if (ct.IsCancellationRequested) return false;
                    read = RandomAccess.Read(handle, _bytes, offset);
                    if (read <= 0) break;
                    offset += read;
                    bytes = _bytes.AsSpan(0, read);
                }
                int tail = decoder.GetChars(ReadOnlySpan<byte>.Empty, _chars.AsSpan(carry), flush: true);
                if (tail > 0) Check(_chars.AsSpan(0, carry + tail));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Read failed part way (file locked or truncated, network dropped): keep what was found so far.
            }
        }
        return true;
    }

    /// <summary>Marks needles present in this window. True when the outcome can no longer change.</summary>
    private bool Check(ReadOnlySpan<char> window)
    {
        bool allPositiveFound = true;
        for (int i = 0; i < _terms.Length; i++)
        {
            if (!_found[i] && window.Contains(_terms[i].Needle, StringComparison.OrdinalIgnoreCase)) _found[i] = true;
            if (_found[i] && _terms[i].Negate) return true; // an excluded needle is present: fails, stop reading
            if (!_found[i] && !_terms[i].Negate) allPositiveFound = false;
        }
        // With a negative needle the whole file must be read to prove it absent.
        return allPositiveFound && !_hasNegative;
    }

    /// <summary>
    /// Text saved as UTF-16 without a BOM is mostly ASCII, so one byte of every pair is zero and the other almost never is.
    /// </summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> b, out bool bigEndian)
    {
        bigEndian = false;
        int pairs = b.Length / 2;
        if (pairs < 16) return false;
        int evenZero = 0, oddZero = 0;
        for (int i = 0; i + 1 < b.Length; i += 2)
        {
            if (b[i] == 0) evenZero++;
            if (b[i + 1] == 0) oddZero++;
        }
        if (oddZero >= pairs * 0.4 && evenZero <= pairs * 0.02) return true;
        if (evenZero >= pairs * 0.4 && oddZero <= pairs * 0.02) { bigEndian = true; return true; }
        return false;
    }
}
