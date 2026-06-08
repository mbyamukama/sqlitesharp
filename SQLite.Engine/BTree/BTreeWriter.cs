using System.Buffers.Binary;
using SQLite.Engine.IO;

namespace SQLite.Engine.BTree;

/// <summary>
/// Provides write operations on a table B-tree: Insert, Delete, and page balancing.
/// Works with the Pager's writable page mechanism to maintain transactional consistency.
/// </summary>
public sealed class BTreeWriter
{
    private readonly Pager _pager;
    private readonly int _rootPage;

    public BTreeWriter(Pager pager, int rootPage)
    {
        _pager = pager;
        _rootPage = rootPage;
    }

    /// <summary>
    /// Insert a record into the table B-tree with the given rowid.
    /// </summary>
    public void Insert(long rowId, byte[] record)
    {
        byte[] cell = Cell.BuildTableLeafCell(rowId, record);
        InsertCell(rowId, cell);
    }

    /// <summary>
    /// Delete the row with the given rowid from the table B-tree.
    /// </summary>
    public void Delete(long rowId)
    {
        // Navigate to the leaf page containing this rowid
        var (pageNum, cellIndex) = FindCell(_rootPage, rowId);
        if (pageNum < 0)
            throw new SqliteException(SqliteResult.Error, $"Row with rowid {rowId} not found.");

        byte[] page = _pager.GetPageWritable(pageNum);
        int headerOffset = pageNum == 1 ? 100 : 0;
        RemoveCellFromPage(page, headerOffset, cellIndex);
    }

    /// <summary>
    /// Get the largest rowid in the table, or 0 if the table is empty.
    /// </summary>
    public long GetMaxRowId()
    {
        if (_rootPage < 1) return 0;

        // Navigate to the rightmost leaf
        int pageNum = _rootPage;
        while (true)
        {
            var pageData = _pager.GetPage(pageNum).ToArray();
            int headerOffset = pageNum == 1 ? 100 : 0;
            byte pageType = pageData[headerOffset];

            if (pageType == 13) // Leaf table
            {
                int cellCount = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(headerOffset + 3));
                if (cellCount == 0) return 0;

                // Last cell has the largest rowid
                int lastCellPtrOffset = headerOffset + 8 + (cellCount - 1) * 2;
                int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(lastCellPtrOffset));
                int pos = cellOffset;
                int n = Cell.ReadVarint(pageData.AsSpan(pos), out _); // payload size
                pos += n;
                Cell.ReadVarint(pageData.AsSpan(pos), out long rowId);
                return rowId;
            }
            else if (pageType == 5) // Interior table
            {
                // Follow rightmost child
                int rightChild = (int)BinaryPrimitives.ReadUInt32BigEndian(pageData.AsSpan(headerOffset + 8));
                pageNum = rightChild;
            }
            else
            {
                return 0;
            }
        }
    }

    // ─── Internal implementation ────────────────────────────────────────────

    private void InsertCell(long rowId, byte[] cell)
    {
        // Find the leaf page where this rowid should go
        var (leafPage, insertIndex) = FindInsertPosition(_rootPage, rowId);
        byte[] page = _pager.GetPageWritable(leafPage);
        int headerOffset = leafPage == 1 ? 100 : 0;

        // Check if cell fits on this page
        int freeSpace = ComputeFreeSpace(page, headerOffset);
        int needed = cell.Length + 2; // cell data + 2 bytes for pointer

        if (needed <= freeSpace)
        {
            InsertCellOnPage(page, headerOffset, insertIndex, cell);
        }
        else
        {
            // Page overflow — need to split
            InsertCellOnPage(page, headerOffset, insertIndex, cell);
            SplitPage(leafPage);
        }
    }

    private (int pageNum, int cellIndex) FindInsertPosition(int pageNum, long rowId)
    {
        while (true)
        {
            var pageData = _pager.GetPage(pageNum).ToArray();
            int headerOffset = pageNum == 1 ? 100 : 0;
            byte pageType = pageData[headerOffset];

            if (pageType == 13) // Leaf table
            {
                int cellCount = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(headerOffset + 3));
                // Binary search for insertion position
                int lo = 0, hi = cellCount;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    long midRowId = GetLeafCellRowId(pageData, headerOffset, mid);
                    if (midRowId < rowId) lo = mid + 1;
                    else hi = mid;
                }
                return (pageNum, lo);
            }
            else if (pageType == 5) // Interior table
            {
                int cellCount = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(headerOffset + 3));
                // Find which child to descend into
                int i;
                for (i = 0; i < cellCount; i++)
                {
                    int cellPtrOffset = headerOffset + 12 + i * 2;
                    int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(cellPtrOffset));
                    // Interior cell: [4-byte child page] [varint rowid]
                    int pos = cellOffset + 4;
                    Cell.ReadVarint(pageData.AsSpan(pos), out long cellRowId);
                    if (rowId <= cellRowId)
                    {
                        // Descend into left child
                        int childPage = (int)BinaryPrimitives.ReadUInt32BigEndian(pageData.AsSpan(cellOffset));
                        pageNum = childPage;
                        break;
                    }
                }
                if (i == cellCount)
                {
                    // Rowid is greater than all keys — use right child
                    int rightChild = (int)BinaryPrimitives.ReadUInt32BigEndian(pageData.AsSpan(headerOffset + 8));
                    pageNum = rightChild;
                }
            }
            else
            {
                throw new SqliteException(SqliteResult.Corrupt, $"Unexpected page type {pageType}");
            }
        }
    }

    private (int pageNum, int cellIndex) FindCell(int pageNum, long targetRowId)
    {
        while (true)
        {
            var pageData = _pager.GetPage(pageNum).ToArray();
            int headerOffset = pageNum == 1 ? 100 : 0;
            byte pageType = pageData[headerOffset];

            if (pageType == 13) // Leaf table
            {
                int cellCount = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(headerOffset + 3));
                for (int i = 0; i < cellCount; i++)
                {
                    long cellRowId = GetLeafCellRowId(pageData, headerOffset, i);
                    if (cellRowId == targetRowId)
                        return (pageNum, i);
                }
                return (-1, -1); // Not found
            }
            else if (pageType == 5) // Interior
            {
                int cellCount = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(headerOffset + 3));
                int i;
                for (i = 0; i < cellCount; i++)
                {
                    int cellPtrOffset = headerOffset + 12 + i * 2;
                    int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(pageData.AsSpan(cellPtrOffset));
                    int pos = cellOffset + 4;
                    Cell.ReadVarint(pageData.AsSpan(pos), out long cellRowId);
                    if (targetRowId <= cellRowId)
                    {
                        int childPage = (int)BinaryPrimitives.ReadUInt32BigEndian(pageData.AsSpan(cellOffset));
                        pageNum = childPage;
                        break;
                    }
                }
                if (i == cellCount)
                {
                    int rightChild = (int)BinaryPrimitives.ReadUInt32BigEndian(pageData.AsSpan(headerOffset + 8));
                    pageNum = rightChild;
                }
            }
            else
            {
                return (-1, -1);
            }
        }
    }

    private long GetLeafCellRowId(byte[] page, int headerOffset, int cellIndex)
    {
        int cellPtrOffset = headerOffset + 8 + cellIndex * 2;
        int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(cellPtrOffset));
        int pos = cellOffset;
        int n = Cell.ReadVarint(page.AsSpan(pos), out _); // payload size
        pos += n;
        Cell.ReadVarint(page.AsSpan(pos), out long rowId);
        return rowId;
    }

    private static int VarintSize(ReadOnlySpan<byte> buf)
    {
        // Compute how many bytes the varint at this position takes
        for (int i = 0; i < 8; i++)
        {
            if ((buf[i] & 0x80) == 0) return i + 1;
        }
        return 9;
    }

    // ─── Page manipulation ──────────────────────────────────────────────────

    private void InsertCellOnPage(byte[] page, int headerOffset, int cellIndex, byte[] cell)
    {
        // Page layout for leaf table (type 13):
        // [headerOffset+0] page type (13)
        // [headerOffset+1..2] first free block offset
        // [headerOffset+3..4] cell count
        // [headerOffset+5..6] cell content area offset
        // [headerOffset+7] fragmented free bytes
        // [headerOffset+8..] cell pointer array (2 bytes each)

        int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));
        int contentOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 5));
        if (contentOffset == 0) contentOffset = _pager.PageSize; // 0 means 65536

        // Place cell data at the bottom of the content area
        int newContentStart = contentOffset - cell.Length;
        Buffer.BlockCopy(cell, 0, page, newContentStart, cell.Length);

        // Shift cell pointers to make room for the new pointer
        int ptrArrayStart = headerOffset + 8;
        int insertAt = ptrArrayStart + cellIndex * 2;
        int ptrArrayEnd = ptrArrayStart + cellCount * 2;

        // Move pointers after insertAt forward by 2 bytes
        if (insertAt < ptrArrayEnd)
        {
            Buffer.BlockCopy(page, insertAt, page, insertAt + 2, ptrArrayEnd - insertAt);
        }

        // Write the new cell pointer
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(insertAt), (ushort)newContentStart);

        // Update cell count
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 3), (ushort)(cellCount + 1));

        // Update content area offset
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 5), (ushort)newContentStart);
    }

    private void RemoveCellFromPage(byte[] page, int headerOffset, int cellIndex)
    {
        int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));
        if (cellIndex >= cellCount) return;

        // Remove cell pointer by shifting subsequent pointers left
        int ptrArrayStart = headerOffset + 8;
        int removeAt = ptrArrayStart + cellIndex * 2;
        int ptrArrayEnd = ptrArrayStart + cellCount * 2;

        if (removeAt + 2 < ptrArrayEnd)
        {
            Buffer.BlockCopy(page, removeAt + 2, page, removeAt, ptrArrayEnd - removeAt - 2);
        }

        // Decrement cell count
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 3), (ushort)(cellCount - 1));

        // Note: we don't reclaim space from the content area for simplicity.
        // A full implementation would update the free block list.
    }

    private int ComputeFreeSpace(byte[] page, int headerOffset)
    {
        int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));
        int contentOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 5));
        if (contentOffset == 0) contentOffset = _pager.PageSize;

        int ptrArrayEnd = headerOffset + 8 + cellCount * 2;
        return contentOffset - ptrArrayEnd;
    }

    // ─── Page splitting ─────────────────────────────────────────────────────

    private void SplitPage(int pageNum)
    {
        byte[] page = _pager.GetPageWritable(pageNum);
        int headerOffset = pageNum == 1 ? 100 : 0;
        int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));

        if (cellCount < 2) return; // Nothing to split

        // Collect all cells from this page
        var cells = new List<(long rowId, byte[] cellData)>();
        for (int i = 0; i < cellCount; i++)
        {
            int cellPtrOffset = headerOffset + 8 + i * 2;
            int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(cellPtrOffset));
            // Read the full cell
            int pos = cellOffset;
            int n1 = Cell.ReadVarint(page.AsSpan(pos), out long payloadSize);
            pos += n1;
            int n2 = Cell.ReadVarint(page.AsSpan(pos), out long rowId);
            int cellSize = n1 + n2 + (int)payloadSize;
            byte[] cellData = new byte[cellSize];
            Buffer.BlockCopy(page, cellOffset, cellData, 0, cellSize);
            cells.Add((rowId, cellData));
        }

        // Split point: middle
        int splitIdx = cellCount / 2;
        long splitRowId = cells[splitIdx].rowId;

        // Allocate a new page for the right half
        int newPageNum = _pager.AllocatePage();
        byte[] newPage = _pager.GetPageWritable(newPageNum);

        // Initialize new page as leaf table
        int newHeaderOffset = 0;
        newPage[newHeaderOffset] = 13; // leaf table
        BinaryPrimitives.WriteUInt16BigEndian(newPage.AsSpan(newHeaderOffset + 1), 0); // no free blocks
        BinaryPrimitives.WriteUInt16BigEndian(newPage.AsSpan(newHeaderOffset + 3), 0); // cell count
        BinaryPrimitives.WriteUInt16BigEndian(newPage.AsSpan(newHeaderOffset + 5), (ushort)_pager.PageSize); // content area
        newPage[newHeaderOffset + 7] = 0; // fragmented bytes

        // Clear the original page and re-insert left half
        ClearPageCells(page, headerOffset);

        // Insert left half into original page
        for (int i = 0; i < splitIdx; i++)
        {
            InsertCellOnPage(page, headerOffset, i, cells[i].cellData);
        }

        // Insert right half into new page
        for (int i = splitIdx; i < cells.Count; i++)
        {
            InsertCellOnPage(newPage, newHeaderOffset, i - splitIdx, cells[i].cellData);
        }

        // Now we need to update the parent to reference both pages.
        // If the current page IS the root, we need to convert it to an interior page.
        if (pageNum == _rootPage)
        {
            ConvertRootToInterior(pageNum, newPageNum, splitRowId, cells, splitIdx);
        }
        else
        {
            // Insert a divider cell into the parent (not implemented for deep trees yet)
            // For Phase 4 we handle the common case of root splits only.
            throw new SqliteException(SqliteResult.Full, "Deep tree splits not yet implemented.");
        }
    }

    private void ConvertRootToInterior(int rootPageNum, int rightPageNum, long splitRowId,
        List<(long rowId, byte[] cellData)> allCells, int splitIdx)
    {
        // Allocate a new page for the left leaf
        int leftPageNum = _pager.AllocatePage();
        byte[] leftPage = _pager.GetPageWritable(leftPageNum);

        // Initialize left page as leaf table
        leftPage[0] = 13;
        BinaryPrimitives.WriteUInt16BigEndian(leftPage.AsSpan(1), 0);
        BinaryPrimitives.WriteUInt16BigEndian(leftPage.AsSpan(3), 0);
        BinaryPrimitives.WriteUInt16BigEndian(leftPage.AsSpan(5), (ushort)_pager.PageSize);
        leftPage[7] = 0;

        // Insert left half cells into left page
        for (int i = 0; i < splitIdx; i++)
        {
            InsertCellOnPage(leftPage, 0, i, allCells[i].cellData);
        }

        // Convert root page to interior table page
        byte[] rootPage = _pager.GetPageWritable(rootPageNum);
        int rootHeaderOffset = rootPageNum == 1 ? 100 : 0;
        ClearPageCells(rootPage, rootHeaderOffset);

        rootPage[rootHeaderOffset] = 5; // interior table
        BinaryPrimitives.WriteUInt16BigEndian(rootPage.AsSpan(rootHeaderOffset + 1), 0);
        BinaryPrimitives.WriteUInt16BigEndian(rootPage.AsSpan(rootHeaderOffset + 3), 0);
        BinaryPrimitives.WriteUInt16BigEndian(rootPage.AsSpan(rootHeaderOffset + 5), (ushort)_pager.PageSize);
        rootPage[rootHeaderOffset + 7] = 0;
        // Right-child pointer (offset +8 from header, 4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(rootPage.AsSpan(rootHeaderOffset + 8), (uint)rightPageNum);

        // Build interior cell: [4-byte left child page] [varint splitRowId]
        int rowidLen = Cell.VarintSize(splitRowId);
        byte[] interiorCell = new byte[4 + rowidLen];
        BinaryPrimitives.WriteUInt32BigEndian(interiorCell.AsSpan(0), (uint)leftPageNum);
        Cell.WriteVarint(interiorCell.AsSpan(4), splitRowId);

        // Insert the divider cell into the root interior page
        InsertInteriorCellOnPage(rootPage, rootHeaderOffset, 0, interiorCell);
    }

    private void InsertInteriorCellOnPage(byte[] page, int headerOffset, int cellIndex, byte[] cell)
    {
        // Interior page has 12-byte header (vs 8 for leaf)
        int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));
        int contentOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 5));
        if (contentOffset == 0) contentOffset = _pager.PageSize;

        int newContentStart = contentOffset - cell.Length;
        Buffer.BlockCopy(cell, 0, page, newContentStart, cell.Length);

        int ptrArrayStart = headerOffset + 12; // 12 bytes for interior header
        int insertAt = ptrArrayStart + cellIndex * 2;
        int ptrArrayEnd = ptrArrayStart + cellCount * 2;

        if (insertAt < ptrArrayEnd)
        {
            Buffer.BlockCopy(page, insertAt, page, insertAt + 2, ptrArrayEnd - insertAt);
        }

        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(insertAt), (ushort)newContentStart);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 3), (ushort)(cellCount + 1));
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 5), (ushort)newContentStart);
    }

    private void ClearPageCells(byte[] page, int headerOffset)
    {
        // Reset cell count and content offset
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 3), 0);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 5), (ushort)_pager.PageSize);
    }
}
