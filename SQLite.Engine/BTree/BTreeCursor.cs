using SQLite.Engine.IO;

namespace SQLite.Engine.BTree;

/// <summary>
/// A row returned from a B-tree cursor scan.
/// </summary>
public sealed class BTreeRecord
{
    public long RowId { get; init; }
    public object?[] Values { get; init; } = [];
}

/// <summary>
/// Read-only cursor for scanning a table B-tree.
/// Navigates from the root page through interior pages down to leaves.
/// </summary>
public sealed class BTreeCursor
{
    private readonly Pager _pager;
    private readonly int _rootPage;
    private readonly bool _isIndex;

    // Navigation stack: (pageNumber, cellIndex) for each level
    private readonly Stack<(int pageNumber, int cellIndex)> _stack = new();
    private MemPage? _currentPage;
    private int _currentCellIndex;
    private bool _eof;

    public bool Eof => _eof;

    public BTreeCursor(Pager pager, int rootPage, bool isIndex = false)
    {
        _pager = pager;
        _rootPage = rootPage;
        _isIndex = isIndex;
        _eof = true;
    }

    /// <summary>
    /// Move the cursor to the first row in the table (leftmost leaf cell).
    /// </summary>
    public void MoveToFirst()
    {
        _stack.Clear();
        _eof = false;
        MoveToLeftmostLeaf(_rootPage);
        if (_currentPage!.CellCount == 0)
        {
            _eof = true;
        }
    }

    /// <summary>
    /// Advance the cursor to the next row.
    /// </summary>
    public void Next()
    {
        if (_eof) return;

        _currentCellIndex++;
        if (_currentCellIndex < _currentPage!.CellCount)
        {
            return; // More cells on this leaf page
        }

        // Move up the stack to find the next path down
        while (_stack.Count > 0)
        {
            var (parentPageNum, parentCellIdx) = _stack.Pop();
            var parentData = _pager.GetPage(parentPageNum).ToArray();
            var parentPage = MemPage.Parse(parentData, parentPageNum);

            int nextChildIdx = parentCellIdx + 1;
            if (nextChildIdx < parentPage.CellCount)
            {
                // Descend into the next child pointer
                _stack.Push((parentPageNum, nextChildIdx));
                var parentSpan = parentPage.Data.Span;
                int cellOffset = parentPage.CellPointers[nextChildIdx];
                int childPage = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    parentSpan[cellOffset..]);
                MoveToLeftmostLeaf(childPage);
                return;
            }
            else
            {
                // Use the right-child pointer
                if (parentPage.RightChild != 0)
                {
                    // But we need to check if we already visited right-child
                    // Push a sentinel so we know we took the right child
                    _stack.Push((parentPageNum, parentPage.CellCount));
                    MoveToLeftmostLeaf(parentPage.RightChild);
                    return;
                }
            }
        }

        _eof = true;
    }

    /// <summary>
    /// Get the current row's payload as a parsed record.
    /// </summary>
    public BTreeRecord GetRecord()
    {
        if (_eof || _currentPage == null)
            throw new SqliteException(SqliteResult.Misuse, "Cursor is at EOF.");

        var span = _currentPage.Data.Span;
        int cellOffset = _currentPage.CellPointers[_currentCellIndex];
        int pos = cellOffset;

        // Table leaf cell format: varint payload-size, varint rowid, payload
        int n = Cell.ReadVarint(span[pos..], out long payloadSize);
        pos += n;
        n = Cell.ReadVarint(span[pos..], out long rowId);
        pos += n;

        // Read local payload
        int usableSize = _pager.UsableSize;
        int localSize = Cell.LocalPayloadSize((int)payloadSize, usableSize, _isIndex);
        var payload = span.Slice(pos, localSize);

        // Parse record
        int headerEnd = Cell.ParseRecordHeader(payload, out int[] serialTypes);
        var values = new object?[serialTypes.Length];
        int dataOffset = headerEnd;
        for (int i = 0; i < serialTypes.Length; i++)
        {
            int size = Cell.SerialTypeSize(serialTypes[i]);
            values[i] = Cell.ReadValue(payload[dataOffset..], serialTypes[i]);
            dataOffset += size;
        }

        return new BTreeRecord { RowId = rowId, Values = values };
    }

    private void MoveToLeftmostLeaf(int pageNumber)
    {
        while (true)
        {
            var data = _pager.GetPage(pageNumber).ToArray();
            var page = MemPage.Parse(data, pageNumber);

            if (page.IsLeaf)
            {
                _currentPage = page;
                _currentCellIndex = 0;
                return;
            }

            // Interior page: push current position, descend left
            _stack.Push((pageNumber, 0));
            var span = page.Data.Span;
            int cellOffset = page.CellPointers[0];
            // Interior table cell: 4-byte child pointer, then varint rowid
            pageNumber = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                span[cellOffset..]);
        }
    }
}
