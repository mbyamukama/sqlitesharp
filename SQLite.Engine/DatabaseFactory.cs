using System.Buffers.Binary;
using SQLite.Engine.IO;

namespace SQLite.Engine;

/// <summary>
/// Creates new empty SQLite database files from scratch.
/// </summary>
public static class DatabaseFactory
{
    /// <summary>
    /// Create a new empty SQLite database file at the given path.
    /// The file will have the standard SQLite header, page 1 as the sqlite_schema
    /// table (an empty leaf B-tree), and be immediately usable.
    /// </summary>
    public static void CreateNew(string path, int pageSize = 4096)
    {
        if (File.Exists(path))
            throw new SqliteException(SqliteResult.CantOpen, $"File already exists: {path}");

        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
            throw new SqliteException(SqliteResult.Error, "Page size must be a power of 2 between 512 and 65536.");

        byte[] page1 = new byte[pageSize];

        // ─── File header (100 bytes) ────────────────────────────────────────
        // Magic string at offset 0
        "SQLite format 3\0"u8.CopyTo(page1.AsSpan(0));

        // Page size at offset 16 (big-endian u16; 1 means 65536)
        ushort rawPageSize = pageSize == 65536 ? (ushort)1 : (ushort)pageSize;
        BinaryPrimitives.WriteUInt16BigEndian(page1.AsSpan(16), rawPageSize);

        // File format write version = 1 (legacy)
        page1[18] = 1;
        // File format read version = 1
        page1[19] = 1;
        // Reserved space per page = 0
        page1[20] = 0;
        // Max embedded payload fraction = 64
        page1[21] = 64;
        // Min embedded payload fraction = 32
        page1[22] = 32;
        // Leaf payload fraction = 32
        page1[23] = 32;

        // File change counter at offset 24
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(24), 0);

        // Page count at offset 28 = 1
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(28), 1);

        // First freelist trunk page at offset 32 = 0 (no freelist)
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(32), 0);

        // Freelist page count at offset 36 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(36), 0);

        // Schema cookie at offset 40 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(40), 0);

        // Schema format number at offset 44 = 4 (latest)
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(44), 4);

        // Default page cache size at offset 48 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(48), 0);

        // Largest root B-tree page at offset 52 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(52), 0);

        // Text encoding at offset 56: 1 = UTF-8
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(56), 1);

        // User version at offset 60 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(60), 0);

        // Incremental vacuum mode at offset 64 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(64), 0);

        // Application ID at offset 68 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(68), 0);

        // Reserved for expansion: offset 72..91 = 0 (already zeroed)

        // Version-valid-for at offset 92 = 0
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(92), 0);

        // SQLite version number at offset 96 = 3054000 (our reference version)
        BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(96), 3054000);

        // ─── B-tree page header (page 1, starting at offset 100) ────────────
        // This is the sqlite_schema table — initially an empty leaf table page.
        int hdrOffset = 100;

        // Page type: 13 = leaf table
        page1[hdrOffset] = 13;
        // First free block offset = 0
        BinaryPrimitives.WriteUInt16BigEndian(page1.AsSpan(hdrOffset + 1), 0);
        // Cell count = 0
        BinaryPrimitives.WriteUInt16BigEndian(page1.AsSpan(hdrOffset + 3), 0);
        // Cell content area offset (0 means end of page)
        BinaryPrimitives.WriteUInt16BigEndian(page1.AsSpan(hdrOffset + 5), (ushort)pageSize);
        // Fragmented free bytes = 0
        page1[hdrOffset + 7] = 0;

        // Write the file
        File.WriteAllBytes(path, page1);
    }
}
