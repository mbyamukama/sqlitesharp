using SQLite.Engine.IO;

namespace SQLite.Engine.BTree;

/// <summary>
/// Opens a table B-tree given a root page and provides cursor access.
/// </summary>
public sealed class BTree
{
    private readonly Pager _pager;

    public int RootPage { get; }

    public BTree(Pager pager, int rootPage)
    {
        _pager = pager;
        RootPage = rootPage;
    }

    /// <summary>
    /// Create a read-only cursor positioned before the first row.
    /// Call MoveToFirst() to begin scanning.
    /// </summary>
    public BTreeCursor OpenCursor(bool isIndex = false)
    {
        return new BTreeCursor(_pager, RootPage, isIndex);
    }
}
