namespace SQLite.Engine.Vdbe;

/// <summary>
/// VDBE opcode constants. A subset sufficient for read-path SELECT queries.
/// Mirrors the C sqlite3 opcodes we need for Phase 3.
/// </summary>
public enum OpCode : byte
{
    /// <summary>Open a read cursor on table with root page P2. Cursor id = P1.</summary>
    OpenRead,

    /// <summary>Rewind cursor P1 to the first row. Jump to P2 if table is empty.</summary>
    Rewind,

    /// <summary>Advance cursor P1 to the next row. Jump to P2 if more rows exist.</summary>
    Next,

    /// <summary>Read column P2 from cursor P1 into register P3.</summary>
    Column,

    /// <summary>Read the rowid of cursor P1 into register P2.</summary>
    Rowid,

    /// <summary>Output registers P1..P1+P2-1 as a result row.</summary>
    ResultRow,

    /// <summary>Close cursor P1.</summary>
    Close,

    /// <summary>Halt execution. P1 = result code (0 = SQLITE_OK).</summary>
    Halt,

    /// <summary>Store integer P2 in register P1. (P4 used for large values)</summary>
    Integer,

    /// <summary>Store the string in P4 into register P1.</summary>
    String,

    /// <summary>Store a real (double) value from P4 into register P1.</summary>
    Real,

    /// <summary>Store NULL in register P1.</summary>
    Null,

    /// <summary>Store a blob from P4 into register P1.</summary>
    Blob,

    /// <summary>Jump to instruction P2 if register P1 is true (non-zero, non-null).</summary>
    If,

    /// <summary>Jump to instruction P2 if register P1 is false (zero or null).</summary>
    IfNot,

    /// <summary>Unconditional jump to instruction P2.</summary>
    Goto,

    /// <summary>Compare registers: if reg[P1] == reg[P3], jump to P2.</summary>
    Eq,

    /// <summary>Compare registers: if reg[P1] != reg[P3], jump to P2.</summary>
    Ne,

    /// <summary>Compare registers: if reg[P1] &lt; reg[P3], jump to P2.</summary>
    Lt,

    /// <summary>Compare registers: if reg[P1] &lt;= reg[P3], jump to P2.</summary>
    Le,

    /// <summary>Compare registers: if reg[P1] &gt; reg[P3], jump to P2.</summary>
    Gt,

    /// <summary>Compare registers: if reg[P1] &gt;= reg[P3], jump to P2.</summary>
    Ge,

    /// <summary>reg[P3] = reg[P1] + reg[P2]</summary>
    Add,

    /// <summary>reg[P3] = reg[P1] - reg[P2]</summary>
    Subtract,

    /// <summary>reg[P3] = reg[P1] * reg[P2]</summary>
    Multiply,

    /// <summary>reg[P3] = reg[P1] / reg[P2]</summary>
    Divide,

    /// <summary>reg[P3] = reg[P1] % reg[P2]</summary>
    Remainder,

    /// <summary>reg[P3] = reg[P1] || reg[P2] (string concatenation)</summary>
    Concat,

    /// <summary>Negate: reg[P2] = -reg[P1]</summary>
    Negate,

    /// <summary>Copy register P1 to register P2.</summary>
    Copy,

    /// <summary>
    /// Decrement a counter in register P1 and jump to P2 when it reaches zero.
    /// Used for LIMIT implementation.
    /// </summary>
    DecrJumpZero,

    /// <summary>Store function result. P1=dest register, P4=func name, P2=first arg register, P3=arg count.</summary>
    Function,

    /// <summary>Aggregate step. P1=dest register, P4=func name, P2=first arg register, P3=arg count.</summary>
    AggStep,

    /// <summary>Aggregate final. P1=dest register, P4=func name.</summary>
    AggFinal,

    /// <summary>Sort the result set. P1 = first register of sort key.</summary>
    SorterOpen,

    /// <summary>Insert a row into the sorter.</summary>
    SorterInsert,

    /// <summary>Sort and rewind to first result.</summary>
    SorterSort,

    /// <summary>Get next sorter row. Jump to P2 if done.</summary>
    SorterNext,

    /// <summary>Read sorter data into registers.</summary>
    SorterData,

    // ── Write opcodes (Phase 4) ──

    /// <summary>Open a write cursor on table with root page P2. Cursor id = P1.</summary>
    OpenWrite,

    /// <summary>
    /// Build a record from registers P1..P1+P2-1 and store in register P3.
    /// </summary>
    MakeRecord,

    /// <summary>
    /// Generate a new unique rowid for cursor P1, store in register P2.
    /// </summary>
    NewRowid,

    /// <summary>
    /// Write record in register P2 with key from register P3 into cursor P1.
    /// </summary>
    InsertInt,

    /// <summary>
    /// Delete the current row at cursor P1.
    /// </summary>
    DeleteOp,

    /// <summary>
    /// Seek cursor P1 to the row with rowid in register P3. Jump to P2 if not found.
    /// </summary>
    SeekRowid,

    /// <summary>
    /// Begin a write transaction.
    /// </summary>
    Transaction,

    /// <summary>
    /// Commit the current transaction.
    /// </summary>
    AutoCommit,

    /// <summary>
    /// Create a new empty B-tree table. Store the new root page number in register P1.
    /// </summary>
    CreateBtree,

    /// <summary>
    /// Initialize a page (P2) as an empty leaf table B-tree.
    /// </summary>
    InitPage,

    /// <summary>
    /// Insert a row into the sqlite_schema table.
    /// P1=cursor on schema, P2=data reg, P3=rowid reg.
    /// </summary>
    SchemaInsert,

    /// <summary>
    /// Delete a row from the sqlite_schema table by rowid.
    /// P1=cursor on schema, P3=rowid reg.
    /// </summary>
    SchemaDelete,

    /// <summary>
    /// Increment the schema cookie in the file header.
    /// </summary>
    IncrSchemaCookie,

    /// <summary>
    /// Reload the in-memory schema (after DDL changes).
    /// </summary>
    ReloadSchema,
}
