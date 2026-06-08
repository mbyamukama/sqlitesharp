namespace SQLite.Engine.IO;

/// <summary>
/// Wraps a FileStream and provides page-level read access with file locking.
/// Replaces os_unix.c / os_win.c for the C# port.
/// </summary>
public sealed class VfsFile : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    public string FilePath { get; }
    public long FileSize => _stream.Length;

    public VfsFile(string path, bool readOnly = false)
    {
        FilePath = Path.GetFullPath(path);
        var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        var share = readOnly ? FileShare.Read : FileShare.ReadWrite;
        var mode = readOnly ? FileMode.Open : FileMode.OpenOrCreate;
        _stream = new FileStream(FilePath, mode, access, share);
    }

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes at the given offset.
    /// </summary>
    public int Read(byte[] buffer, long offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Seek(offset, SeekOrigin.Begin);
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = _stream.Read(buffer, totalRead, count - totalRead);
            if (n == 0) break; // EOF
            totalRead += n;
        }
        return totalRead;
    }

    /// <summary>
    /// Write bytes at the given offset.
    /// </summary>
    public void Write(byte[] buffer, long offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(buffer, 0, count);
    }

    /// <summary>
    /// Acquire a shared (read) lock on a byte range.
    /// </summary>
    public void LockShared(long offset, long count)
    {
        _stream.Lock(offset, count);
    }

    /// <summary>
    /// Release a lock on a byte range.
    /// </summary>
    public void Unlock(long offset, long count)
    {
        _stream.Unlock(offset, count);
    }

    public void Sync()
    {
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _disposed = true;
        }
    }
}
