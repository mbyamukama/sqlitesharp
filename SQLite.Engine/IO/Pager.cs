using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SQLite.Engine.IO;

/// <summary>
/// Database file header — the first 100 bytes of every SQLite database file.
/// See https://sqlite.org/fileformat2.html
/// </summary>
public sealed class DatabaseHeader
{
    public const int HeaderSize = 100;
    public static readonly byte[] MagicString = "SQLite format 3\0"u8.ToArray();

    public int PageSize { get; init; }
    public byte FileFormatWriteVersion { get; init; }
    public byte FileFormatReadVersion { get; init; }
    public byte ReservedSpacePerPage { get; init; }
    public int FileChangeCounter { get; init; }
    public int PageCount { get; init; }
    public int FirstFreelistTrunkPage { get; init; }
    public int FreelistPageCount { get; init; }
    public int SchemaCookie { get; init; }
    public int SchemaFormatNumber { get; init; }
    public int DefaultPageCacheSize { get; init; }
    public int LargestRootBTreePage { get; init; }
    public int TextEncoding { get; init; }  // 1=UTF8, 2=UTF16le, 3=UTF16be
    public int UserVersion { get; init; }
    public int IncrementalVacuumMode { get; init; }
    public int ApplicationId { get; init; }
    public int VersionValidFor { get; init; }
    public int SqliteVersionNumber { get; init; }
}

/// <summary>
/// Pager state machine (simplified from C).
/// </summary>
public enum PagerState
{
    Open,
    Reader,
    WriterLocked,
    WriterCacheMod,
    WriterFinished,
}

/// <summary>
/// Page cache and file I/O layer with transaction support.
/// Supports read-only mode and read-write mode with rollback journal.
/// </summary>
public sealed class Pager : IDisposable
{
    private readonly VfsFile _file;
    private readonly Dictionary<int, byte[]> _pageCache = new();
    private readonly HashSet<int> _dirtyPages = new();
    private readonly bool _readOnly;
    private bool _disposed;

    // Transaction state
    private PagerState _state = PagerState.Reader;
    private FileStream? _journal;
    private string? _journalPath;
    private int _pageCount;
    private int _originalPageCount;

    // WAL mode
    private Wal? _wal;
    private bool _walMode;

    public DatabaseHeader Header { get; private set; }
    public int PageSize => Header.PageSize;
    public int PageCount => _pageCount;
    public int UsableSize => PageSize - Header.ReservedSpacePerPage;
    public bool IsReadOnly => _readOnly;
    public PagerState State => _state;
    public bool WalMode => _walMode;

    public Pager(string dbPath, bool readOnly = false)
    {
        _readOnly = readOnly;
        _file = new VfsFile(dbPath, readOnly);
        Header = ReadHeader();
        _pageCount = Header.PageCount;
        _journalPath = dbPath + "-journal";

        // Check if a WAL file exists (database was in WAL mode)
        string walPath = dbPath + "-wal";
        if (File.Exists(walPath))
        {
            _wal = new Wal(dbPath, Header.PageSize);
            _wal.Open();
            _walMode = true;
        }
    }

    /// <summary>
    /// Enable WAL mode for this connection. Creates the WAL file if needed.
    /// </summary>
    public void EnableWalMode()
    {
        if (_readOnly)
            throw new SqliteException(SqliteResult.ReadOnly, "Cannot enable WAL on read-only database.");
        if (_walMode) return;

        _wal = new Wal(_file.FilePath, PageSize);
        _wal.Open();
        _walMode = true;
    }

    /// <summary>
    /// Disable WAL mode — checkpoint and remove the WAL file.
    /// </summary>
    public void DisableWalMode()
    {
        if (!_walMode || _wal == null) return;

        // Checkpoint first
        _wal.Checkpoint(_file);
        _wal.Close(delete: true);
        _wal = null;
        _walMode = false;

        // Invalidate cache (pages may have been updated during checkpoint)
        foreach (var page in _pageCache.Values)
            ArrayPool<byte>.Shared.Return(page);
        _pageCache.Clear();
    }

    private DatabaseHeader ReadHeader()
    {
        if (_file.FileSize < DatabaseHeader.HeaderSize)
        {
            throw new SqliteException(SqliteResult.NotADb, "File is too small to be a database.");
        }

        byte[] buf = new byte[DatabaseHeader.HeaderSize];
        _file.Read(buf, 0, DatabaseHeader.HeaderSize);

        // Validate magic string
        if (!buf.AsSpan(0, 16).SequenceEqual(DatabaseHeader.MagicString))
        {
            throw new SqliteException(SqliteResult.NotADb, "Not a SQLite database (invalid magic string).");
        }

        int rawPageSize = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(16));
        int pageSize = rawPageSize == 1 ? 65536 : rawPageSize;

        int pageCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(28));
        if (pageCount == 0)
        {
            pageCount = (int)(_file.FileSize / pageSize);
        }

        return new DatabaseHeader
        {
            PageSize = pageSize,
            FileFormatWriteVersion = buf[18],
            FileFormatReadVersion = buf[19],
            ReservedSpacePerPage = buf[20],
            FileChangeCounter = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(24)),
            PageCount = pageCount,
            FirstFreelistTrunkPage = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(32)),
            FreelistPageCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(36)),
            SchemaCookie = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(40)),
            SchemaFormatNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(44)),
            DefaultPageCacheSize = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(48)),
            LargestRootBTreePage = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(52)),
            TextEncoding = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(56)),
            UserVersion = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(60)),
            IncrementalVacuumMode = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(64)),
            ApplicationId = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(68)),
            VersionValidFor = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(92)),
            SqliteVersionNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(96)),
        };
    }

    /// <summary>
    /// Get a page by 1-based page number. Returns a cached buffer.
    /// </summary>
    public ReadOnlySpan<byte> GetPage(int pageNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pageNumber < 1 || pageNumber > _pageCount)
        {
            throw new SqliteException(SqliteResult.Corrupt, $"Page number {pageNumber} out of range [1..{_pageCount}].");
        }

        EnsurePageLoaded(pageNumber);
        return _pageCache[pageNumber].AsSpan(0, PageSize);
    }

    /// <summary>
    /// Get a writable reference to a page. Journals the original content if this
    /// is the first write to this page in the current transaction.
    /// </summary>
    public byte[] GetPageWritable(int pageNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_readOnly)
            throw new SqliteException(SqliteResult.ReadOnly, "Database is opened read-only.");

        if (_state < PagerState.WriterLocked)
            throw new SqliteException(SqliteResult.Misuse, "No write transaction active. Call Begin() first.");

        if (pageNumber < 1 || pageNumber > _pageCount)
            throw new SqliteException(SqliteResult.Corrupt, $"Page number {pageNumber} out of range [1..{_pageCount}].");

        EnsurePageLoaded(pageNumber);

        // Journal the original page content before first modification
        if (!_dirtyPages.Contains(pageNumber))
        {
            if (!_walMode)
            {
                JournalPage(pageNumber);
            }
            _dirtyPages.Add(pageNumber);
            if (_state == PagerState.WriterLocked)
                _state = PagerState.WriterCacheMod;
        }

        return _pageCache[pageNumber];
    }

    /// <summary>
    /// Allocate a new page at the end of the file. Returns the page number.
    /// </summary>
    public int AllocatePage()
    {
        if (_state < PagerState.WriterLocked)
            throw new SqliteException(SqliteResult.Misuse, "No write transaction active.");

        _pageCount++;
        byte[] newPage = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(newPage, 0, PageSize);
        _pageCache[_pageCount] = newPage;
        _dirtyPages.Add(_pageCount);

        if (_state == PagerState.WriterLocked)
            _state = PagerState.WriterCacheMod;

        return _pageCount;
    }

    /// <summary>
    /// Begin a write transaction. Opens the rollback journal (or WAL).
    /// </summary>
    public void Begin()
    {
        if (_readOnly)
            throw new SqliteException(SqliteResult.ReadOnly, "Cannot begin write transaction on read-only database.");
        if (_state >= PagerState.WriterLocked)
            return; // Already in a write transaction

        _originalPageCount = _pageCount;

        if (!_walMode)
        {
            OpenJournal();
        }
        else if (_wal != null && !_wal.IsActive)
        {
            _wal.Open();
        }

        _state = PagerState.WriterLocked;
    }

    /// <summary>
    /// Commit the current write transaction. In journal mode, flushes dirty pages to disk.
    /// In WAL mode, writes frames to the WAL file.
    /// </summary>
    public void Commit()
    {
        if (_state < PagerState.WriterLocked)
            return; // Nothing to commit

        if (_dirtyPages.Count > 0)
        {
            if (_walMode && _wal != null)
            {
                // WAL mode: write dirty pages as frames
                int frameIdx = 0;
                int totalDirty = _dirtyPages.Count;
                foreach (int pgno in _dirtyPages)
                {
                    frameIdx++;
                    bool isCommit = frameIdx == totalDirty;
                    _wal.WriteFrame(pgno, _pageCache[pgno][..PageSize], isCommit, _pageCount);
                }
            }
            else
            {
                // Journal mode: update header and write to db file
                UpdateFileHeader();

                // Sync journal to ensure durability before writing to DB
                _journal?.Flush();

                // Write all dirty pages to the database file
                foreach (int pgno in _dirtyPages)
                {
                    long offset = (long)(pgno - 1) * PageSize;
                    _file.Write(_pageCache[pgno], offset, PageSize);
                }

                // Sync the database file
                _file.Sync();
            }
        }

        if (!_walMode)
        {
            // Remove the journal — this is the commit point
            CloseAndDeleteJournal();
        }

        _dirtyPages.Clear();
        _state = PagerState.Reader;

        // Re-read header to pick up updated page count
        if (!_walMode)
        {
            Header = ReadHeader();
            _pageCount = Header.PageCount;
        }
    }

    /// <summary>
    /// Rollback the current write transaction. Restores original pages from the journal.
    /// </summary>
    public void Rollback()
    {
        if (_state < PagerState.WriterLocked)
            return;

        if (_journal != null && _journal.Length > 0)
        {
            // Replay journal: restore original page contents
            PlaybackJournal();
        }

        // Discard dirty pages from cache (reload from disk on next access)
        foreach (int pgno in _dirtyPages)
        {
            if (_pageCache.TryGetValue(pgno, out byte[]? page))
            {
                // If this was a newly allocated page, remove from cache
                if (pgno > _originalPageCount)
                {
                    ArrayPool<byte>.Shared.Return(page);
                    _pageCache.Remove(pgno);
                }
                // Otherwise the journal playback already restored it
            }
        }

        _pageCount = _originalPageCount;
        _dirtyPages.Clear();
        CloseAndDeleteJournal();
        _state = PagerState.Reader;
    }

    // ─── Journal management ─────────────────────────────────────────────────

    private void OpenJournal()
    {
        if (_journal != null) return;
        _journal = new FileStream(_journalPath!, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // Write journal header: magic + page count + page size + sector size
        // Simplified journal header (28 bytes):
        // [0..7]   magic: 0xd9d505f920a163d7
        // [8..11]  record count (0, updated on close — we use nRec=0xFFFFFFFF for hot-journal detection)
        // [12..15] zero (no super-journal)
        // [16..19] page size
        // [20..23] sector size (512)
        // [24..27] page size again (for compatibility)
        byte[] header = new byte[28];
        // Magic bytes
        header[0] = 0xd9; header[1] = 0xd5; header[2] = 0x05; header[3] = 0xf9;
        header[4] = 0x20; header[5] = 0xa1; header[6] = 0x63; header[7] = 0xd7;
        // Record count = 0xFFFFFFFF (indicates unknown — will be updated at commit)
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0xFFFFFFFF);
        // Page size
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)PageSize);
        // Sector size
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 512);
        // Page size again
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), (uint)PageSize);

        _journal.Write(header, 0, header.Length);
    }

    private void JournalPage(int pageNumber)
    {
        if (_journal == null) return;
        if (pageNumber > _originalPageCount) return; // New page, no original to journal

        byte[] page = _pageCache[pageNumber];

        // Journal record format: [4-byte page number] [page data] [4-byte checksum]
        byte[] record = new byte[4 + PageSize + 4];
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0), (uint)pageNumber);
        Buffer.BlockCopy(page, 0, record, 4, PageSize);
        // Simple checksum (sum of all bytes in page, mod 2^32)
        uint checksum = 0;
        for (int i = 0; i < PageSize; i++)
            checksum += page[i];
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4 + PageSize), checksum);

        _journal.Write(record, 0, record.Length);
    }

    private void PlaybackJournal()
    {
        if (_journal == null) return;

        _journal.Seek(28, SeekOrigin.Begin); // Skip header
        byte[] record = new byte[4 + PageSize + 4];

        while (true)
        {
            int bytesRead = 0;
            while (bytesRead < record.Length)
            {
                int n = _journal.Read(record, bytesRead, record.Length - bytesRead);
                if (n == 0) goto done;
                bytesRead += n;
            }

            int pgno = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(0));
            if (pgno < 1 || pgno > _originalPageCount) continue;

            // Restore the original page to cache
            if (_pageCache.TryGetValue(pgno, out byte[]? cached))
            {
                Buffer.BlockCopy(record, 4, cached, 0, PageSize);
            }

            // Also restore to disk
            long offset = (long)(pgno - 1) * PageSize;
            byte[] pageData = new byte[PageSize];
            Buffer.BlockCopy(record, 4, pageData, 0, PageSize);
            _file.Write(pageData, offset, PageSize);
        }

    done:
        _file.Sync();
    }

    private void CloseAndDeleteJournal()
    {
        if (_journal != null)
        {
            _journal.Close();
            _journal.Dispose();
            _journal = null;
            try { File.Delete(_journalPath!); } catch { /* best effort */ }
        }
    }

    private void UpdateFileHeader()
    {
        // Page 1 contains the file header. Update page count and change counter.
        EnsurePageLoaded(1);
        byte[] page1 = _pageCache[1];
        if (!_dirtyPages.Contains(1))
        {
            JournalPage(1);
            _dirtyPages.Add(1);
        }

        // Update page count at offset 28
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(28), (uint)_pageCount);

        // Increment change counter at offset 24
        int changeCounter = (int)BinaryPrimitives.ReadUInt32BigEndian(page1.AsSpan(24)) + 1;
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(24), (uint)changeCounter);

        // Update version-valid-for at offset 92 (same as change counter)
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(92), (uint)changeCounter);
    }

    private void EnsurePageLoaded(int pageNumber)
    {
        if (!_pageCache.TryGetValue(pageNumber, out _))
        {
            byte[]? walPage = null;

            // Check WAL for a more recent version of this page
            if (_walMode && _wal != null)
            {
                walPage = _wal.ReadPage(pageNumber);
            }

            byte[] page = ArrayPool<byte>.Shared.Rent(PageSize);

            if (walPage != null)
            {
                Buffer.BlockCopy(walPage, 0, page, 0, PageSize);
            }
            else
            {
                long offset = (long)(pageNumber - 1) * PageSize;
                int read = _file.Read(page, offset, PageSize);
                if (read < PageSize)
                {
                    if (pageNumber <= _pageCount)
                        Array.Clear(page, read, PageSize - read);
                    else
                        throw new SqliteException(SqliteResult.Corrupt, $"Short read on page {pageNumber}.");
                }
            }
            _pageCache[pageNumber] = page;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CloseAndDeleteJournal();
            _wal?.Dispose();
            foreach (var page in _pageCache.Values)
            {
                ArrayPool<byte>.Shared.Return(page);
            }
            _pageCache.Clear();
            _file.Dispose();
            _disposed = true;
        }
    }
}
