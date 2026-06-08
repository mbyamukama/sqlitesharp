using System.Buffers.Binary;

namespace SQLite.Engine.BTree;

/// <summary>
/// B-tree page types as stored in the page header flag byte.
/// </summary>
public enum BTreePageType : byte
{
    InteriorIndex = 2,
    InteriorTable = 5,
    LeafIndex = 10,
    LeafTable = 13,
}

/// <summary>
/// In-memory representation of a parsed B-tree page.
/// </summary>
public sealed class MemPage
{
    /// <summary>Page type flag.</summary>
    public BTreePageType PageType { get; }

    /// <summary>True if this is a leaf page (no children).</summary>
    public bool IsLeaf => PageType == BTreePageType.LeafTable || PageType == BTreePageType.LeafIndex;

    /// <summary>Offset of the first free block on this page (0 if none).</summary>
    public int FirstFreeBlock { get; }

    /// <summary>Number of cells on this page.</summary>
    public int CellCount { get; }

    /// <summary>Offset to the first byte of the cell content area.</summary>
    public int CellContentOffset { get; }

    /// <summary>Number of fragmented free bytes.</summary>
    public int FragmentedFreeBytes { get; }

    /// <summary>Right-most pointer (interior pages only). 0 for leaf pages.</summary>
    public int RightChild { get; }

    /// <summary>Cell pointer array — offsets into the page data for each cell.</summary>
    public int[] CellPointers { get; }

    /// <summary>The raw page data (reference to the pager's cached buffer).</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// Offset within the page where the B-tree header starts.
    /// Page 1 has a 100-byte file header before the B-tree header.
    /// </summary>
    public int HeaderOffset { get; }

    private MemPage(ReadOnlyMemory<byte> data, int headerOffset)
    {
        Data = data;
        HeaderOffset = headerOffset;
        var span = data.Span;

        PageType = (BTreePageType)span[headerOffset];
        FirstFreeBlock = BinaryPrimitives.ReadUInt16BigEndian(span[(headerOffset + 1)..]);
        CellCount = BinaryPrimitives.ReadUInt16BigEndian(span[(headerOffset + 3)..]);
        CellContentOffset = BinaryPrimitives.ReadUInt16BigEndian(span[(headerOffset + 5)..]);
        if (CellContentOffset == 0) CellContentOffset = 65536;
        FragmentedFreeBytes = span[headerOffset + 7];

        if (!IsLeaf)
        {
            RightChild = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(headerOffset + 8)..]);
        }

        // Cell pointer array starts immediately after the header
        int headerSize = IsLeaf ? 8 : 12;
        int ptrArrayOffset = headerOffset + headerSize;
        CellPointers = new int[CellCount];
        for (int i = 0; i < CellCount; i++)
        {
            CellPointers[i] = BinaryPrimitives.ReadUInt16BigEndian(span[(ptrArrayOffset + i * 2)..]);
        }
    }

    /// <summary>
    /// Parse a B-tree page from raw page data.
    /// </summary>
    /// <param name="data">Full page contents from the pager.</param>
    /// <param name="pageNumber">1-based page number (page 1 has extra 100-byte header).</param>
    public static MemPage Parse(ReadOnlyMemory<byte> data, int pageNumber)
    {
        int headerOffset = pageNumber == 1 ? 100 : 0;
        return new MemPage(data, headerOffset);
    }
}
