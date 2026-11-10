using System.Runtime.InteropServices;

namespace pxdCommander.Ops;

/// <summary>
/// Copies one file with progress, pause and cancel. Windows uses CopyFileEx, which keeps alternate streams, EFS,
/// sparse files and the OS's fast paths (server-side copy on SMB, block cloning on ReFS); elsewhere a 1 MB stream loop.
/// A failed or cancelled copy never leaves a partial destination behind.
/// </summary>
internal sealed class FileCopier
{
    // Above this, unbuffered I/O is faster and doesn't flush everything else out of the file cache.
    private const long NoBufferingThreshold = 256L * 1024 * 1024;
    private const int BufferSize = 1 << 20;

    private readonly FileJob _job;
    private OpsNative.CopyProgressRoutine? _routine; // rooted in a field: CopyFileEx calls it from native code
    private long _baseBytes;
    private bool _destinationOpened;
    private Exception? _callbackError;
    private byte[]? _buffer;

    /// <summary>Test hook: use the portable stream copy on Windows too, so that path is exercised.</summary>
    internal static bool ForceStreamCopy;

    public FileCopier(FileJob job) => _job = job;

    private static bool Native => OperatingSystem.IsWindows() && !ForceStreamCopy;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, reporting progress as
    /// <paramref name="baseBytes"/> + bytes copied. Throws on failure (DoneBytes reset to <paramref name="baseBytes"/>),
    /// OperationCanceledException on cancel, and DestinationExistsException when <paramref name="overwrite"/> is false
    /// and something appeared at the destination since the conflict check.
    /// </summary>
    public void Copy(FileInfo source, string destination, bool overwrite, long baseBytes)
    {
        try
        {
            if (Native) CopyNative(source, destination, overwrite, baseBytes);
            else CopyStream(source, destination, overwrite, baseBytes);
        }
        catch
        {
            _job.SetDoneBytes(baseBytes);
            throw;
        }
        ApplyMetadata(source, destination, Native);
    }

    private void CopyNative(FileInfo source, string destination, bool overwrite, long baseBytes)
    {
        _baseBytes = baseBytes;
        _destinationOpened = false;
        _callbackError = null;
        _routine ??= OnProgress;

        var flags = overwrite ? 0 : OpsNative.COPY_FILE_FAIL_IF_EXISTS;
        if (source.Length > NoBufferingThreshold) flags |= OpsNative.COPY_FILE_NO_BUFFERING;

        var ok = OpsNative.CopyFileEx(OpsPath.ToNative(source.FullName), OpsPath.ToNative(destination), _routine, IntPtr.Zero, IntPtr.Zero, flags);
        var error = ok ? 0 : Marshal.GetLastPInvokeError();
        GC.KeepAlive(_routine);
        if (ok) return;

        if (!overwrite && error is OpsNative.ERROR_FILE_EXISTS or OpsNative.ERROR_ALREADY_EXISTS)
            throw new DestinationExistsException(destination);
        // CopyFileEx deletes its partial output itself on PROGRESS_CANCEL; this covers the other failures. Only once the
        // callback has run is the destination ours — before that it may be someone else's file.
        if (_destinationOpened) OpsPath.DeletePartial(destination);
        if (_job.IsCancelRequested) throw new OperationCanceledException(_job.Token);
        if (_callbackError is not null) throw new IOException(_callbackError.Message, _callbackError);
        throw OpsNative.ToException(error, source.FullName);
    }

    private uint OnProgress(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data)
    {
        // Never let an exception escape into native code.
        try
        {
            _destinationOpened = true;
            _job.SetDoneBytes(_baseBytes + totalBytesTransferred);
            // Pausing blocks right here, inside the copy, so a huge file pauses mid-way.
            if (_job.IsPauseRequested) _job.WaitWhilePaused();
            return _job.IsCancelRequested ? OpsNative.PROGRESS_CANCEL : OpsNative.PROGRESS_CONTINUE;
        }
        catch (OperationCanceledException)
        {
            return OpsNative.PROGRESS_CANCEL;
        }
        catch (Exception ex)
        {
            _callbackError = ex;
            return OpsNative.PROGRESS_CANCEL;
        }
    }

    private void CopyStream(FileInfo source, string destination, bool overwrite, long baseBytes)
    {
        _buffer ??= new byte[BufferSize];
        using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

        FileStream output;
        try
        {
            output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
        }
        catch (IOException) when (!overwrite && OpsPath.Exists(destination))
        {
            throw new DestinationExistsException(destination);
        }

        var completed = false;
        try
        {
            using (output)
            {
                long copied = 0;
                int read;
                while ((read = input.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    _job.Checkpoint();
                    output.Write(_buffer, 0, read);
                    copied += read;
                    _job.SetDoneBytes(baseBytes + copied);
                }
            }
            completed = true;
        }
        finally
        {
            if (!completed) OpsPath.DeletePartial(destination);
        }
    }

    /// <summary>
    /// Timestamps and attributes. CopyFileEx already carries attributes and the last-write time but gives the copy a
    /// fresh creation time; the stream path carries nothing. Best effort: the content is what matters.
    /// </summary>
    private static void ApplyMetadata(FileInfo source, string destination, bool native)
    {
        try
        {
            // Times before attributes: a read-only file still accepts new times, but keep the order obvious.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) File.SetCreationTimeUtc(destination, source.CreationTimeUtc);
            if (native) return;
            File.SetLastWriteTimeUtc(destination, source.LastWriteTimeUtc);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source.FullName));
            var attributes = source.Attributes & OpsPath.SettableAttributes;
            if (attributes != 0) File.SetAttributes(destination, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
