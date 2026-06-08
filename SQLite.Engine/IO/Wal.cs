using System.Buffers.Binary;

namespace SQLite.Engine.IO;

/// <summary>
/// Write-Ahead Log (WAL) implementation.
/// 
/// WAL file format:
/// - 32-byte header: magic, version, page size, checkpoint seq, salt1, salt2, checksum1, checksum2
/// - Zero or more frames: 24-byte frame header + page-size bytes of page data
/// 
/// Frame header: page number, commit-size (non-zero on commit frames), salt1, salt2, checksum1, checksum2
/// </summary>
public sealed class Wal : IDisposable
{
    public const int WalHeaderSize = 32;
    public const int FrameHeaderSize = 24;
    public const uint MagicLe = 0x377f0682; // little-endian checksums
    public const uint MagicBe = 0x377f0683; // big-endian checksums
    public const uint WalVersion = 3007000;

    private FileStream? _walFile;
    private readonly string _walPath;
    private readonly int _pageSize;
    private bool _disposed;

    // WAL header state
    private uint _salt1;
    private uint _salt2;
    private uint _checksum1;
    private uint _checksum2;
    private uint _checkpointSeq;
    private bool _useBigEndian;

    // Frame index: maps page number → offset in WAL file of the latest frame
    private readonly Dictionary<int, long> _frameIndex = new();
    private int _frameCount;
    private int _commitFrameCount; // frames visible to readers (up to last commit)

    public int FrameCount => _frameCount;
    public bool IsActive => _walFile != null && _frameCount > 0;
    public string WalPath => _walPath;

    public Wal(string dbPath, int pageSize)
    {
        _walPath = dbPath + "-wal";
        _pageSize = pageSize;
    }

    /// <summary>
    /// Open or create the WAL file. If it exists, read existing frames.
    /// </summary>
    public void Open()
    {
        if (_walFile != null) return;

        bool exists = File.Exists(_walPath);
        _walFile = new FileStream(_walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        if (exists && _walFile.Length >= WalHeaderSize)
        {
            ReadHeader();
            IndexFrames();
        }
        else
        {
            WriteNewHeader();
        }
    }

    /// <summary>
    /// Check if the WAL contains a more recent version of the given page.
    /// Returns null if the page is not in the WAL.
    /// </summary>
    public byte[]? ReadPage(int pageNumber)
    {
        if (_walFile == null || !_frameIndex.TryGetValue(pageNumber, out long offset))
            return null;

        // Read the page data from the frame (skip frame header)
        byte[] page = new byte[_pageSize];
        _walFile.Seek(offset + FrameHeaderSize, SeekOrigin.Begin);
        int read = _walFile.Read(page, 0, _pageSize);
        if (read < _pageSize) return null;

        return page;
    }

    /// <summary>
    /// Write a frame to the WAL. If isCommit is true, this frame marks a transaction commit.
    /// </summary>
    public void WriteFrame(int pageNumber, byte[] pageData, bool isCommit, int dbSizePages)
    {
        if (_walFile == null)
            throw new SqliteException(SqliteResult.Error, "WAL file is not open.");

        long offset = WalHeaderSize + (long)_frameCount * (FrameHeaderSize + _pageSize);
        _walFile.Seek(offset, SeekOrigin.Begin);

        // Build frame header
        byte[] frameHeader = new byte[FrameHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(0), (uint)pageNumber);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(4), isCommit ? (uint)dbSizePages : 0);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(8), _salt1);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(12), _salt2);

        // Compute checksum over frame header (first 8 bytes) + page data
        ComputeFrameChecksum(frameHeader.AsSpan(0, 8), pageData, ref _checksum1, ref _checksum2);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(16), _checksum1);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(20), _checksum2);

        // Write frame
        _walFile.Write(frameHeader, 0, FrameHeaderSize);
        _walFile.Write(pageData, 0, _pageSize);

        // Update index
        _frameIndex[pageNumber] = offset;
        _frameCount++;

        if (isCommit)
        {
            _commitFrameCount = _frameCount;
            _walFile.Flush();
        }
    }

    /// <summary>
    /// Checkpoint: transfer all committed WAL frames back to the main database file.
    /// After checkpoint, the WAL is reset.
    /// </summary>
    public void Checkpoint(VfsFile dbFile)
    {
        if (_walFile == null || _commitFrameCount == 0) return;

        // Transfer each frame to the database file
        byte[] frameHeader = new byte[FrameHeaderSize];
        byte[] pageData = new byte[_pageSize];

        for (int i = 0; i < _commitFrameCount; i++)
        {
            long offset = WalHeaderSize + (long)i * (FrameHeaderSize + _pageSize);
            _walFile.Seek(offset, SeekOrigin.Begin);

            // Read frame header
            _walFile.Read(frameHeader, 0, FrameHeaderSize);
            int pageNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(0));

            // Read page data
            _walFile.Read(pageData, 0, _pageSize);

            // Write to database file
            long dbOffset = (long)(pageNumber - 1) * _pageSize;
            dbFile.Write(pageData, dbOffset, _pageSize);
        }

        dbFile.Sync();

        // Reset WAL
        _frameIndex.Clear();
        _frameCount = 0;
        _commitFrameCount = 0;
        _checkpointSeq++;

        // Rewrite header with new salt
        WriteNewHeader();
    }

    /// <summary>
    /// Close and optionally delete the WAL file.
    /// </summary>
    public void Close(bool delete = false)
    {
        if (_walFile != null)
        {
            _walFile.Close();
            _walFile.Dispose();
            _walFile = null;
        }

        if (delete)
        {
            try { File.Delete(_walPath); } catch { }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
    }

    // ─── Internal ───────────────────────────────────────────────────────────

    private void WriteNewHeader()
    {
        _salt1 = (uint)Random.Shared.Next();
        _salt2 = (uint)Random.Shared.Next();
        _useBigEndian = false; // Use little-endian checksums (simpler on x86)

        byte[] header = new byte[WalHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), _useBigEndian ? MagicBe : MagicLe);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), WalVersion);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)_pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), _checkpointSeq);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), _salt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), _salt2);

        // Compute checksum over first 24 bytes of header
        _checksum1 = 0;
        _checksum2 = 0;
        ComputeChecksum(header.AsSpan(0, 24), ref _checksum1, ref _checksum2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), _checksum1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), _checksum2);

        _walFile!.Seek(0, SeekOrigin.Begin);
        _walFile.Write(header, 0, WalHeaderSize);
        _walFile.SetLength(WalHeaderSize);
        _walFile.Flush();
    }

    private void ReadHeader()
    {
        byte[] header = new byte[WalHeaderSize];
        _walFile!.Seek(0, SeekOrigin.Begin);
        _walFile.Read(header, 0, WalHeaderSize);

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0));
        if (magic != MagicLe && magic != MagicBe)
            throw new SqliteException(SqliteResult.Corrupt, "Invalid WAL magic number.");

        _useBigEndian = magic == MagicBe;
        _checkpointSeq = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        _salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        _salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        _checksum1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
        _checksum2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
    }

    private void IndexFrames()
    {
        byte[] frameHeader = new byte[FrameHeaderSize];
        long offset = WalHeaderSize;
        int frameIdx = 0;

        while (offset + FrameHeaderSize + _pageSize <= _walFile!.Length)
        {
            _walFile.Seek(offset, SeekOrigin.Begin);
            int read = _walFile.Read(frameHeader, 0, FrameHeaderSize);
            if (read < FrameHeaderSize) break;

            int pageNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(0));
            uint commitSize = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4));
            uint frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8));
            uint frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12));

            // Validate salt matches header
            if (frameSalt1 != _salt1 || frameSalt2 != _salt2)
                break;

            if (pageNumber > 0)
            {
                _frameIndex[pageNumber] = offset;
                frameIdx++;
            }

            if (commitSize > 0)
                _commitFrameCount = frameIdx;

            offset += FrameHeaderSize + _pageSize;
        }

        _frameCount = frameIdx;
    }

    private void ComputeFrameChecksum(ReadOnlySpan<byte> frameHeaderFirst8, byte[] pageData,
        ref uint s0, ref uint s1)
    {
        // Checksum over frame header (first 8 bytes) then page data
        ComputeChecksum(frameHeaderFirst8, ref s0, ref s1);
        ComputeChecksum(pageData, ref s0, ref s1);
    }

    private void ComputeChecksum(ReadOnlySpan<byte> data, ref uint s0, ref uint s1)
    {
        // Process data as pairs of 32-bit integers
        int n = data.Length / 4;
        for (int i = 0; i < n - 1; i += 2)
        {
            uint x0, x1;
            if (_useBigEndian)
            {
                x0 = BinaryPrimitives.ReadUInt32BigEndian(data[(i * 4)..]);
                x1 = BinaryPrimitives.ReadUInt32BigEndian(data[((i + 1) * 4)..]);
            }
            else
            {
                x0 = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
                x1 = BinaryPrimitives.ReadUInt32LittleEndian(data[((i + 1) * 4)..]);
            }
            s0 += x0 + s1;
            s1 += x1 + s0;
        }
    }
}
