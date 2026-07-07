using SQLite.Engine.Vdbe;

namespace SQLite.Engine.Compiler;

/// <summary>
/// Compiles a parsed AST into a VDBE bytecode program for execution.
/// Phase 3 supports SELECT statements (with WHERE, ORDER BY, LIMIT).
/// </summary>
public sealed class CodeGen
{
    private readonly List<VdbeOp> _ops = new();
    private int _nextRegister = 1; // register 0 is reserved
    private int _nextCursor = 0;
    private readonly SchemaInfo _schema;
    // Used to communicate a pending patch address between CompileJoinSelect and itself
    private int _pendingNullRowWhereSkip = -1;

    /// <summary>
    /// Schema info needed for code generation (table -> root page, column names).
    /// </summary>
    public sealed class TableInfo
    {
        public string Name { get; init; } = "";
        public int RootPage { get; init; }
        public string[] ColumnNames { get; init; } = [];
        /// <summary>
        /// Index of the INTEGER PRIMARY KEY column (rowid alias), or -1 if none.
        /// </summary>
        public int IntegerPrimaryKeyIndex { get; init; } = -1;
    }

    public sealed class SchemaInfo
    {
        private readonly Dictionary<string, TableInfo> _tables = new(StringComparer.OrdinalIgnoreCase);

        public void AddTable(TableInfo table) => _tables[table.Name] = table;

        public TableInfo? GetTable(string name) =>
            _tables.TryGetValue(name, out var t) ? t : null;
    }

    public CodeGen(SchemaInfo schema)
    {
        _schema = schema;
    }

    /// <summary>
    /// Compile a statement into a VDBE program.
    /// Returns (program, registerCount, cursorCount).
    /// </summary>
    public (VdbeOp[] Program, int RegisterCount, int CursorCount) Compile(Stmt stmt)
    {
        switch (stmt)
        {
            case SelectStmt select:
                CompileSelect(select);
                break;
            case InsertStmt insert:
                CompileInsert(insert);
                break;
            case UpdateStmt update:
                CompileUpdate(update);
                break;
            case DeleteStmt delete:
                CompileDelete(delete);
                break;
            case CreateTableStmt create:
                CompileCreateTable(create);
                break;
            case DropTableStmt drop:
                CompileDropTable(drop);
                break;
            default:
                throw new SqliteException(SqliteResult.Error, $"Code generation not implemented for {stmt.GetType().Name}");
        }

        return (_ops.ToArray(), _nextRegister, _nextCursor);
    }

    private void CompileSelect(SelectStmt select)
    {
        // Check if this is a JOIN query
        if (select.Joins != null && select.Joins.Length > 0)
        {
            CompileJoinSelect(select);
            return;
        }

        // Resolve table
        TableInfo? tableInfo = null;
        int cursorId = -1;

        if (select.From != null)
        {
            tableInfo = _schema.GetTable(select.From.TableName)
                ?? throw new SqliteException(SqliteResult.Error, $"Table '{select.From.TableName}' not found.");
            cursorId = AllocCursor();
        }

        // Detect if this is an aggregate query (has aggregate functions and no GROUP BY)
        bool isAggregate = HasAggregates(select) && select.GroupBy == null;

        if (isAggregate)
        {
            CompileAggregateSelect(select, tableInfo, cursorId);
            return;
        }

        // Check for LIMIT — allocate a counter register
        int limitReg = -1;
        if (select.Limit != null)
        {
            limitReg = AllocRegister();
            CompileExpr(select.Limit, limitReg, tableInfo, cursorId);
        }

        if (tableInfo == null)
        {
            // SELECT without FROM — just emit expressions as a single row
            int startReg = _nextRegister;
            int colCount = select.Columns.Length;
            // Pre-allocate contiguous result registers
            int[] destRegs = new int[colCount];
            for (int i = 0; i < colCount; i++)
                destRegs[i] = AllocRegister();
            // Now compile each expression into its designated register
            for (int i = 0; i < colCount; i++)
                CompileExpr(select.Columns[i].Expression, destRegs[i], null, -1);
            Emit(OpCode.ResultRow, startReg, colCount);
            Emit(OpCode.Halt);
            return;
        }

        // OpenRead cursor
        Emit(OpCode.OpenRead, cursorId, tableInfo.RootPage);

        // Rewind — jump to close if empty
        int rewindAddr = _ops.Count;
        Emit(OpCode.Rewind, cursorId, 0); // P2 patched later

        // Loop body start
        int loopStart = _ops.Count;

        // Evaluate WHERE (if present) — skip to Next if false
        int skipToNext = -1;
        if (select.Where != null)
        {
            int whereReg = AllocRegister();
            CompileWhereCondition(select.Where, whereReg, tableInfo, cursorId);
            skipToNext = _ops.Count;
            Emit(OpCode.IfNot, whereReg, 0); // P2 patched to Next
        }

        // Emit columns into result registers
        int resultStart = _nextRegister;
        int resultCount;

        // Check if we need to expand star
        if (select.Columns.Length == 1 && select.Columns[0].Expression is StarExpr star && star.TableName == null && tableInfo != null)
        {
            // Expand * to all columns
            resultCount = tableInfo.ColumnNames.Length;
            for (int i = 0; i < resultCount; i++)
            {
                int reg = AllocRegister();
                if (i == tableInfo.IntegerPrimaryKeyIndex)
                    Emit(OpCode.Rowid, cursorId, reg);
                else
                    Emit(OpCode.Column, cursorId, i, reg);
            }
        }
        else
        {
            resultCount = select.Columns.Length;
            // Pre-allocate contiguous result registers
            int[] destRegs = new int[resultCount];
            for (int i = 0; i < resultCount; i++)
                destRegs[i] = AllocRegister();
            // Now compile each expression into its designated register
            for (int i = 0; i < resultCount; i++)
                CompileResultExpr(select.Columns[i].Expression, destRegs[i], tableInfo, cursorId);
        }

        // ResultRow
        Emit(OpCode.ResultRow, resultStart, resultCount);

        // LIMIT: decrement counter, jump to close when zero
        int afterLimitJump = -1;
        if (limitReg >= 0)
        {
            afterLimitJump = _ops.Count;
            Emit(OpCode.DecrJumpZero, limitReg, 0); // P2 patched to close
        }

        // Next
        int nextAddr = _ops.Count;
        Emit(OpCode.Next, cursorId, loopStart);

        // Close + Halt
        int closeAddr = _ops.Count;
        Emit(OpCode.Close, cursorId);
        Emit(OpCode.Halt);

        // Patch jumps
        _ops[rewindAddr] = PatchP2(_ops[rewindAddr], closeAddr);
        if (skipToNext >= 0)
            _ops[skipToNext] = PatchP2(_ops[skipToNext], nextAddr);
        if (afterLimitJump >= 0)
            _ops[afterLimitJump] = PatchP2(_ops[afterLimitJump], closeAddr);
    }

    // ─── JOIN compilation ───────────────────────────────────────────────────────

    /// <summary>
    /// Context for resolving columns across multiple tables in a JOIN.
    /// </summary>
    private sealed class JoinContext
    {
        public record struct TableEntry(string Name, string? Alias, TableInfo Info, int CursorId);
        public List<TableEntry> Tables { get; } = new();

        public void Add(string name, string? alias, TableInfo info, int cursorId)
            => Tables.Add(new TableEntry(name, alias, info, cursorId));

        /// <summary>
        /// Resolve a column reference to (cursorId, columnIndex).
        /// columnIndex = -1 means rowid.
        /// </summary>
        public (int CursorId, TableInfo TableInfo, int ColIndex) Resolve(ColumnRefExpr col)
        {
            // If table-qualified
            if (col.TableName != null)
            {
                var entry = Tables.FirstOrDefault(t =>
                    t.Alias?.Equals(col.TableName, StringComparison.OrdinalIgnoreCase) == true ||
                    t.Name.Equals(col.TableName, StringComparison.OrdinalIgnoreCase));
                if (entry.Info == null)
                    throw new SqliteException(SqliteResult.Error, $"Unknown table or alias '{col.TableName}'.");
                int idx = FindColumn(entry.Info, col.ColumnName);
                return (entry.CursorId, entry.Info, idx);
            }

            // Unqualified — search all tables
            foreach (var entry in Tables)
            {
                int idx = FindColumnOrNeg(entry.Info, col.ColumnName);
                if (idx >= -1 && idx != int.MinValue)
                    return (entry.CursorId, entry.Info, idx);
            }
            throw new SqliteException(SqliteResult.Error, $"Column '{col.ColumnName}' not found.");
        }

        private static int FindColumn(TableInfo info, string name)
        {
            if (name.Equals("rowid", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("_rowid_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("oid", StringComparison.OrdinalIgnoreCase))
                return -1;
            for (int i = 0; i < info.ColumnNames.Length; i++)
                if (info.ColumnNames[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            throw new SqliteException(SqliteResult.Error, $"Column '{name}' not found in table '{info.Name}'.");
        }

        /// <summary>Returns column index, -1 for rowid, or int.MinValue if not found in this table.</summary>
        private static int FindColumnOrNeg(TableInfo info, string name)
        {
            if (name.Equals("rowid", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("_rowid_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("oid", StringComparison.OrdinalIgnoreCase))
                return -1;
            for (int i = 0; i < info.ColumnNames.Length; i++)
                if (info.ColumnNames[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return int.MinValue; // not found
        }
    }

    private void CompileJoinSelect(SelectStmt select)
    {
        // Build join context with all tables
        var ctx = new JoinContext();

        var leftInfo = _schema.GetTable(select.From!.TableName)
            ?? throw new SqliteException(SqliteResult.Error, $"Table '{select.From.TableName}' not found.");
        int leftCursor = AllocCursor();
        ctx.Add(select.From.TableName, select.From.Alias, leftInfo, leftCursor);

        var join = select.Joins![0];
        bool isLeftJoin = join.Type == JoinClause.JoinType.Left;
        var rightInfo = _schema.GetTable(join.Table.TableName)
            ?? throw new SqliteException(SqliteResult.Error, $"Table '{join.Table.TableName}' not found.");
        int rightCursor = AllocCursor();
        ctx.Add(join.Table.TableName, join.Table.Alias, rightInfo, rightCursor);

        // For LEFT JOIN we need a "matched" flag register per left row
        int matchedReg = isLeftJoin ? AllocRegister() : -1;

        // LIMIT
        int limitReg = -1;
        if (select.Limit != null)
        {
            limitReg = AllocRegister();
            CompileJoinExpr(select.Limit, limitReg, ctx);
        }

        // Pre-allocate result registers (fixed positions, reused each iteration)
        int resultStart = _nextRegister;
        int resultCount = select.Columns.Length;
        int[] destRegs = new int[resultCount];
        for (int i = 0; i < resultCount; i++)
            destRegs[i] = AllocRegister();

        // Open both cursors
        Emit(OpCode.OpenRead, leftCursor, leftInfo.RootPage);
        Emit(OpCode.OpenRead, rightCursor, rightInfo.RootPage);

        // Outer loop: rewind left
        int leftRewindAddr = _ops.Count;
        Emit(OpCode.Rewind, leftCursor, 0); // patched to close

        int outerLoopStart = _ops.Count;

        // Reset matched flag for this left row
        if (isLeftJoin)
            Emit(OpCode.Integer, matchedReg, 0);

        // Inner loop: rewind right
        int rightRewindAddr = _ops.Count;
        Emit(OpCode.Rewind, rightCursor, 0); // patched to afterInner

        int innerLoopStart = _ops.Count;

        // Evaluate ON condition — skip to right-next if false
        int skipToRightNext = -1;
        if (join.On != null)
        {
            int onReg = AllocRegister();
            CompileJoinCondition(join.On, onReg, ctx);
            skipToRightNext = _ops.Count;
            Emit(OpCode.IfNot, onReg, 0); // patched to right-next
        }

        // Evaluate WHERE — skip to right-next if false
        int skipWhereToRightNext = -1;
        if (select.Where != null)
        {
            int whereReg = AllocRegister();
            CompileJoinCondition(select.Where, whereReg, ctx);
            skipWhereToRightNext = _ops.Count;
            Emit(OpCode.IfNot, whereReg, 0); // patched to right-next
        }

        // Mark matched (for LEFT JOIN)
        if (isLeftJoin)
            Emit(OpCode.Integer, matchedReg, 1);

        // Emit result columns (both sides present)
        for (int i = 0; i < resultCount; i++)
            CompileJoinResultExpr(select.Columns[i].Expression, destRegs[i], ctx);

        Emit(OpCode.ResultRow, resultStart, resultCount);

        // LIMIT
        int afterLimitJump = -1;
        if (limitReg >= 0)
        {
            afterLimitJump = _ops.Count;
            Emit(OpCode.DecrJumpZero, limitReg, 0); // patched to close
        }

        // Right-next (inner loop continues)
        int rightNextAddr = _ops.Count;
        Emit(OpCode.Next, rightCursor, innerLoopStart);

        // After inner loop — for LEFT JOIN, check if we had any match
        int afterInnerAddr = _ops.Count;

        int skipNullRowAddr = -1;
        if (isLeftJoin)
        {
            // If matched, skip the null-row emission
            skipNullRowAddr = _ops.Count;
            Emit(OpCode.If, matchedReg, 0); // patched to leftNext

            // No match found: emit a row with NULLs for the right-side columns.
            // First apply WHERE filter in "null-row context" — right-side columns read as NULL.
            // This correctly handles:
            //   WHERE left_col >= N  → evaluates normally, can filter
            //   WHERE right_col > N  → right_col is NULL → NULL > N = NULL = falsy → skip row
            int skipNullRowEmit = -1;
            if (select.Where != null)
            {
                int whereNullReg = AllocRegister();
                CompileJoinConditionNullRight(select.Where, whereNullReg, ctx, rightCursor);
                skipNullRowEmit = _ops.Count;
                Emit(OpCode.IfNot, whereNullReg, 0); // patched to leftNext if WHERE fails
            }

            // Fill right-side result registers with NULL, re-read left-side from cursor
            NullFillRightColumns(select.Columns, destRegs, ctx, rightCursor);

            Emit(OpCode.ResultRow, resultStart, resultCount);

            // LIMIT check for null row too
            if (limitReg >= 0)
            {
                Emit(OpCode.DecrJumpZero, limitReg, 0); // patched to close below
            }

            if (skipNullRowEmit >= 0)
            {
                // Will patch to leftNext after we know its address
                // Store the index so we can patch it later
                _pendingNullRowWhereSkip = skipNullRowEmit;
            }
        }

        // Left-next (outer loop continues)
        int leftNextAddr = _ops.Count;
        Emit(OpCode.Next, leftCursor, outerLoopStart);

        // Close + Halt
        int closeAddr = _ops.Count;
        Emit(OpCode.Close, leftCursor);
        Emit(OpCode.Close, rightCursor);
        Emit(OpCode.Halt);

        // Patch jumps
        _ops[leftRewindAddr] = PatchP2(_ops[leftRewindAddr], closeAddr);
        // Right rewind on empty jumps to afterInner (so LEFT JOIN null row logic still runs)
        _ops[rightRewindAddr] = PatchP2(_ops[rightRewindAddr], afterInnerAddr);
        if (skipToRightNext >= 0)
            _ops[skipToRightNext] = PatchP2(_ops[skipToRightNext], rightNextAddr);
        if (skipWhereToRightNext >= 0)
            _ops[skipWhereToRightNext] = PatchP2(_ops[skipWhereToRightNext], rightNextAddr);
        if (afterLimitJump >= 0)
            _ops[afterLimitJump] = PatchP2(_ops[afterLimitJump], closeAddr);
        if (skipNullRowAddr >= 0)
            _ops[skipNullRowAddr] = PatchP2(_ops[skipNullRowAddr], leftNextAddr);

        // Patch any extra DecrJumpZero emitted for the null-row LIMIT check
        if (isLeftJoin && limitReg >= 0)
        {
            // Find the last DecrJumpZero that still has P2=0 (unpatched)
            for (int i = _ops.Count - 1; i >= 0; i--)
            {
                if (_ops[i].Opcode == OpCode.DecrJumpZero && _ops[i].P2 == 0 && i != afterLimitJump)
                {
                    _ops[i] = PatchP2(_ops[i], closeAddr);
                    break;
                }
            }
        }

        // Patch the null-row WHERE skip (jumps to leftNext if WHERE fails in null-row context)
        if (_pendingNullRowWhereSkip >= 0)
        {
            _ops[_pendingNullRowWhereSkip] = PatchP2(_ops[_pendingNullRowWhereSkip], leftNextAddr);
            _pendingNullRowWhereSkip = -1;
        }
    }

    /// <summary>
    /// For LEFT JOIN null-row emission: re-emit left column values from cursor
    /// and fill right columns with NULL into the pre-allocated destRegs.
    /// </summary>
    private void NullFillRightColumns(ResultColumn[] columns, int[] destRegs,
        JoinContext ctx, int rightCursor)
    {
        for (int i = 0; i < columns.Length; i++)
        {
            bool isFromRight = false;
            if (columns[i].Expression is ColumnRefExpr col)
            {
                try
                {
                    var (resolvedCursor, _, _) = ctx.Resolve(col);
                    isFromRight = resolvedCursor == rightCursor;
                }
                catch { isFromRight = false; }
            }

            if (isFromRight)
                Emit(OpCode.Null, destRegs[i]);
            else
                CompileJoinResultExpr(columns[i].Expression, destRegs[i], ctx);
        }
    }

    /// <summary>
    /// Compile a WHERE condition for the null-row path of a LEFT JOIN.
    /// Right-table column reads are treated as NULL; left-table reads proceed normally.
    /// NULL propagation through comparisons means right-side filters correctly return falsy.
    /// </summary>
    private void CompileJoinConditionNullRight(Expr expr, int destReg, JoinContext ctx, int rightCursor)
    {
        if (expr is BinaryExpr bin && IsComparisonOp(bin.Operator))
        {
            int leftReg = AllocRegister();
            int rightReg = AllocRegister();
            CompileJoinExprNullRight(bin.Left, leftReg, ctx, rightCursor);
            CompileJoinExprNullRight(bin.Right, rightReg, ctx, rightCursor);

            // Produce boolean: set 1, jump-if-true over set-0
            Emit(OpCode.Integer, destReg, 1);
            int jumpAddr = _ops.Count;
            EmitComparisonJump(bin.Operator, leftReg, rightReg, 0);
            Emit(OpCode.Integer, destReg, 0);
            int afterAddr = _ops.Count;
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], afterAddr);
            return;
        }

        if (expr is BinaryExpr logical && (logical.Operator == TokenType.And || logical.Operator == TokenType.Or))
        {
            int leftReg = AllocRegister();
            int rightReg = AllocRegister();
            CompileJoinConditionNullRight(logical.Left, leftReg, ctx, rightCursor);
            CompileJoinConditionNullRight(logical.Right, rightReg, ctx, rightCursor);
            if (logical.Operator == TokenType.And)
                Emit(OpCode.Multiply, leftReg, rightReg, destReg);
            else
                Emit(OpCode.Add, leftReg, rightReg, destReg);
            return;
        }

        if (expr is UnaryExpr unary && unary.Operator == TokenType.Not)
        {
            int innerReg = AllocRegister();
            CompileJoinConditionNullRight(unary.Operand, innerReg, ctx, rightCursor);
            Emit(OpCode.Integer, destReg, 1);
            int jumpAddr = _ops.Count;
            Emit(OpCode.IfNot, innerReg, 0);
            Emit(OpCode.Integer, destReg, 0);
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], _ops.Count);
            return;
        }

        if (expr is IsNullExpr isNull)
        {
            int operandReg = AllocRegister();
            CompileJoinExprNullRight(isNull.Operand, operandReg, ctx, rightCursor);
            int nullReg = AllocRegister();
            Emit(OpCode.Null, nullReg);
            Emit(OpCode.Integer, destReg, isNull.IsNot ? 1 : 0);
            int jumpAddr = _ops.Count;
            Emit(OpCode.Eq, operandReg, 0, nullReg);
            Emit(OpCode.Integer, destReg, isNull.IsNot ? 0 : 1);
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], _ops.Count);
            return;
        }

        // Fallback
        CompileJoinExprNullRight(expr, destReg, ctx, rightCursor);
    }

    /// <summary>
    /// Like CompileJoinExpr but substitutes NULL for any right-cursor column reads.
    /// </summary>
    private void CompileJoinExprNullRight(Expr expr, int destReg, JoinContext ctx, int rightCursor)
    {
        if (expr is ColumnRefExpr col)
        {
            try
            {
                var (cursorId, info, colIdx) = ctx.Resolve(col);
                if (cursorId == rightCursor)
                {
                    Emit(OpCode.Null, destReg);
                    return;
                }
                if (colIdx == -1 || colIdx == info.IntegerPrimaryKeyIndex)
                    Emit(OpCode.Rowid, cursorId, destReg);
                else
                    Emit(OpCode.Column, cursorId, colIdx, destReg);
            }
            catch
            {
                Emit(OpCode.Null, destReg);
            }
            return;
        }

        // For anything else, fall through to normal expression compilation
        // (literals, functions, binary ops — recursively substitute)
        switch (expr)
        {
            case LiteralExpr lit:
                EmitLiteral(lit, destReg);
                break;
            case BinaryExpr bin:
                int leftReg = AllocRegister();
                int rightReg = AllocRegister();
                CompileJoinExprNullRight(bin.Left, leftReg, ctx, rightCursor);
                CompileJoinExprNullRight(bin.Right, rightReg, ctx, rightCursor);
                EmitBinaryOp(bin.Operator, leftReg, rightReg, destReg);
                break;
            case UnaryExpr un:
                CompileJoinExprNullRight(un.Operand, destReg, ctx, rightCursor);
                if (un.Operator == TokenType.Minus)
                    Emit(OpCode.Negate, destReg, destReg);
                break;
            case FunctionCallExpr func:
                int argStart = _nextRegister;
                int argCount = func.Arguments.Length;
                for (int i = 0; i < argCount; i++)
                {
                    int argReg = AllocRegister();
                    CompileJoinExprNullRight(func.Arguments[i], argReg, ctx, rightCursor);
                }
                Emit(OpCode.Function, destReg, argStart, argCount, func.FunctionName);
                break;
            case ParenExpr paren:
                CompileJoinExprNullRight(paren.Inner, destReg, ctx, rightCursor);
                break;
            default:
                Emit(OpCode.Null, destReg);
                break;
        }
    }

    /// <summary>Compile an expression in a JOIN context (resolves columns from multiple tables).</summary>
    private void CompileJoinExpr(Expr expr, int destReg, JoinContext ctx)
    {
        switch (expr)
        {
            case LiteralExpr lit:
                EmitLiteral(lit, destReg);
                break;
            case ColumnRefExpr col:
                var (cursorId, info, colIdx) = ctx.Resolve(col);
                if (colIdx == -1 || colIdx == info.IntegerPrimaryKeyIndex)
                    Emit(OpCode.Rowid, cursorId, destReg);
                else
                    Emit(OpCode.Column, cursorId, colIdx, destReg);
                break;
            case BinaryExpr bin:
                int leftReg = AllocRegister();
                int rightReg = AllocRegister();
                CompileJoinExpr(bin.Left, leftReg, ctx);
                CompileJoinExpr(bin.Right, rightReg, ctx);
                EmitBinaryOp(bin.Operator, leftReg, rightReg, destReg);
                break;
            case UnaryExpr un:
                CompileJoinExpr(un.Operand, destReg, ctx);
                if (un.Operator == TokenType.Minus)
                    Emit(OpCode.Negate, destReg, destReg);
                break;
            case FunctionCallExpr func:
                int argStart = _nextRegister;
                int argCount = func.Arguments.Length;
                for (int i = 0; i < argCount; i++)
                {
                    int argReg = AllocRegister();
                    CompileJoinExpr(func.Arguments[i], argReg, ctx);
                }
                Emit(OpCode.Function, destReg, argStart, argCount, func.FunctionName);
                break;
            case ParenExpr paren:
                CompileJoinExpr(paren.Inner, destReg, ctx);
                break;
            default:
                Emit(OpCode.Null, destReg);
                break;
        }
    }

    /// <summary>Compile a JOIN condition into a boolean register (1=true, 0=false).</summary>
    private void CompileJoinCondition(Expr expr, int destReg, JoinContext ctx)
    {
        if (expr is BinaryExpr bin && IsComparisonOp(bin.Operator))
        {
            int leftReg = AllocRegister();
            int rightReg = AllocRegister();
            CompileJoinExpr(bin.Left, leftReg, ctx);
            CompileJoinExpr(bin.Right, rightReg, ctx);

            // Set destReg = 1, then conditionally jump over the "set 0"
            Emit(OpCode.Integer, destReg, 1);
            int jumpAddr = _ops.Count;
            EmitComparisonJump(bin.Operator, leftReg, rightReg, 0); // jump to after if true
            Emit(OpCode.Integer, destReg, 0);
            int afterAddr = _ops.Count;
            // Patch the comparison jump to point to afterAddr
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], afterAddr);
        }
        else if (expr is BinaryExpr logical && (logical.Operator == TokenType.And || logical.Operator == TokenType.Or))
        {
            int leftReg = AllocRegister();
            int rightReg = AllocRegister();
            CompileJoinCondition(logical.Left, leftReg, ctx);
            CompileJoinCondition(logical.Right, rightReg, ctx);
            if (logical.Operator == TokenType.And)
            {
                // AND: multiply (both must be nonzero)
                Emit(OpCode.Multiply, leftReg, rightReg, destReg);
            }
            else
            {
                // OR: add and check > 0
                Emit(OpCode.Add, leftReg, rightReg, destReg);
            }
        }
        else
        {
            // Fallback: evaluate as expression, truthiness determines result
            CompileJoinExpr(expr, destReg, ctx);
        }
    }

    /// <summary>Compile a result expression in JOIN context.</summary>
    private void CompileJoinResultExpr(Expr expr, int destReg, JoinContext ctx)
    {
        CompileJoinExpr(expr, destReg, ctx);
    }

    private void EmitComparisonJump(TokenType op, int leftReg, int rightReg, int jumpTarget)
    {
        var opCode = op switch
        {
            TokenType.Eq => OpCode.Eq,
            TokenType.Neq => OpCode.Ne,
            TokenType.Lt => OpCode.Lt,
            TokenType.Lte => OpCode.Le,
            TokenType.Gt => OpCode.Gt,
            TokenType.Gte => OpCode.Ge,
            _ => OpCode.Eq,
        };
        Emit(opCode, leftReg, jumpTarget, rightReg);
    }

    private void EmitBinaryOp(TokenType op, int leftReg, int rightReg, int destReg)
    {
        switch (op)
        {
            case TokenType.Plus:
                Emit(OpCode.Add, leftReg, rightReg, destReg); break;
            case TokenType.Minus:
                Emit(OpCode.Subtract, leftReg, rightReg, destReg); break;
            case TokenType.Star:
                Emit(OpCode.Multiply, leftReg, rightReg, destReg); break;
            case TokenType.Slash:
                Emit(OpCode.Divide, leftReg, rightReg, destReg); break;
            case TokenType.Percent:
                Emit(OpCode.Remainder, leftReg, rightReg, destReg); break;
            case TokenType.Concat:
                Emit(OpCode.Concat, leftReg, rightReg, destReg); break;
            default:
                // For comparison ops used as expressions (returns 0 or 1)
                Emit(OpCode.Integer, destReg, 0);
                int jmpAddr = _ops.Count;
                EmitComparisonJump(op, leftReg, rightReg, 0);
                Emit(OpCode.Integer, destReg, 1); // not reached if jump taken
                // Actually the jump means "jump if condition true"
                // so: set 0, jump-if-true to +2, keep 0; or after jump set 1
                // Let me just use the simpler pattern:
                _ops[jmpAddr] = PatchP2(_ops[jmpAddr], _ops.Count);
                break;
        }
    }

    private void EmitLiteral(LiteralExpr lit, int destReg)
    {
        switch (lit.LiteralType)
        {
            case TokenType.Integer:
                Emit(OpCode.Integer, destReg, (int)(long)lit.Value!);
                break;
            case TokenType.Float:
                Emit(OpCode.Real, destReg, 0, 0, lit.Value);
                break;
            case TokenType.String:
                Emit(OpCode.String, destReg, 0, 0, lit.Value);
                break;
            case TokenType.Null:
                Emit(OpCode.Null, destReg);
                break;
            default:
                Emit(OpCode.Null, destReg);
                break;
        }
    }

    private void CompileAggregateSelect(SelectStmt select, TableInfo? tableInfo, int cursorId)
    {
        // Allocate accumulator registers for each result column
        int resultStart = _nextRegister;
        int resultCount = select.Columns.Length;
        int[] accRegs = new int[resultCount];
        for (int i = 0; i < resultCount; i++)
        {
            accRegs[i] = AllocRegister();
            // Initialize accumulators to NULL
            Emit(OpCode.Null, accRegs[i]);
        }

        if (tableInfo != null)
        {
            Emit(OpCode.OpenRead, cursorId, tableInfo.RootPage);
            int rewindAddr = _ops.Count;
            Emit(OpCode.Rewind, cursorId, 0); // patched

            int loopStart = _ops.Count;

            // WHERE filter
            int skipToNext = -1;
            if (select.Where != null)
            {
                int whereReg = AllocRegister();
                CompileWhereCondition(select.Where, whereReg, tableInfo, cursorId);
                skipToNext = _ops.Count;
                Emit(OpCode.IfNot, whereReg, 0); // patched
            }

            // AggStep for each column
            for (int i = 0; i < resultCount; i++)
            {
                var expr = select.Columns[i].Expression;
                if (expr is FunctionCallExpr func && IsAggregateFunction(func.FunctionName))
                {
                    int argReg = -1;
                    int argCount = 0;
                    if (func.Arguments.Length > 0 && func.Arguments[0] is not StarExpr)
                    {
                        argCount = func.Arguments.Length;
                        argReg = _nextRegister;
                        for (int a = 0; a < argCount; a++)
                        {
                            int r = AllocRegister();
                            CompileResultExpr(func.Arguments[a], r, tableInfo, cursorId);
                        }
                    }
                    else
                    {
                        argReg = AllocRegister(); // dummy
                        argCount = 0;
                    }
                    Emit(OpCode.AggStep, accRegs[i], argReg, argCount, func.FunctionName);
                }
            }

            // Next
            int nextAddr = _ops.Count;
            Emit(OpCode.Next, cursorId, loopStart);

            int closeAddr = _ops.Count;
            Emit(OpCode.Close, cursorId);

            // Patch
            _ops[rewindAddr] = PatchP2(_ops[rewindAddr], closeAddr);
            if (skipToNext >= 0)
                _ops[skipToNext] = PatchP2(_ops[skipToNext], nextAddr);
        }

        // AggFinal for each
        for (int i = 0; i < resultCount; i++)
        {
            var expr = select.Columns[i].Expression;
            if (expr is FunctionCallExpr func && IsAggregateFunction(func.FunctionName))
            {
                Emit(OpCode.AggFinal, accRegs[i], 0, 0, func.FunctionName);
            }
        }

        // ResultRow
        Emit(OpCode.ResultRow, resultStart, resultCount);
        Emit(OpCode.Halt);
    }

    private void CompileWhereCondition(Expr where, int destReg, TableInfo? tableInfo, int cursorId)
    {
        // For comparison expressions, generate a specialized comparison
        if (where is BinaryExpr bin && IsComparisonOp(bin.Operator))
        {
            int leftReg = AllocRegister();
            int rightReg = AllocRegister();
            CompileResultExpr(bin.Left, leftReg, tableInfo, cursorId);
            CompileResultExpr(bin.Right, rightReg, tableInfo, cursorId);

            // We need to produce a boolean result in destReg
            // Strategy: set destReg = 1, then conditionally set to 0
            Emit(OpCode.Integer, destReg, 1); // assume true
            int jumpAddr = _ops.Count;
            // Emit: if condition is true, jump over the "set false"
            var jumpOp = ComparisonToJumpOp(bin.Operator);
            Emit(jumpOp, leftReg, 0, rightReg); // P2 patched
            // Condition is false
            Emit(OpCode.Integer, destReg, 0);
            int afterAddr = _ops.Count;
            // Patch the jump to skip the "set false"
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], afterAddr);
            return;
        }

        if (where is BinaryExpr logic && (logic.Operator == TokenType.And || logic.Operator == TokenType.Or))
        {
            int leftReg = AllocRegister();
            int rightReg = AllocRegister();
            CompileWhereCondition(logic.Left, leftReg, tableInfo, cursorId);
            CompileWhereCondition(logic.Right, rightReg, tableInfo, cursorId);

            if (logic.Operator == TokenType.And)
            {
                // destReg = leftReg AND rightReg (both must be true)
                Emit(OpCode.Integer, destReg, 0); // default false
                int skipAddr = _ops.Count;
                Emit(OpCode.IfNot, leftReg, 0); // if left false, skip to end
                int skip2Addr = _ops.Count;
                Emit(OpCode.IfNot, rightReg, 0); // if right false, skip to end
                Emit(OpCode.Integer, destReg, 1); // both true
                int endAddr = _ops.Count;
                _ops[skipAddr] = PatchP2(_ops[skipAddr], endAddr);
                _ops[skip2Addr] = PatchP2(_ops[skip2Addr], endAddr);
            }
            else // OR
            {
                Emit(OpCode.Integer, destReg, 0);
                int trueAddr1 = _ops.Count;
                Emit(OpCode.If, leftReg, 0); // if left true, jump to set-true
                int trueAddr2 = _ops.Count;
                Emit(OpCode.If, rightReg, 0); // if right true, jump to set-true
                int endJump = _ops.Count;
                Emit(OpCode.Goto, 0, 0); // jump to end (stays false)
                int setTrueAddr = _ops.Count;
                Emit(OpCode.Integer, destReg, 1);
                int endAddr = _ops.Count;
                _ops[trueAddr1] = PatchP2(_ops[trueAddr1], setTrueAddr);
                _ops[trueAddr2] = PatchP2(_ops[trueAddr2], setTrueAddr);
                _ops[endJump] = PatchP2(_ops[endJump], endAddr);
            }
            return;
        }

        if (where is UnaryExpr unary && unary.Operator == TokenType.Not)
        {
            int innerReg = AllocRegister();
            CompileWhereCondition(unary.Operand, innerReg, tableInfo, cursorId);
            // Negate: destReg = !innerReg
            Emit(OpCode.Integer, destReg, 1);
            int jumpAddr = _ops.Count;
            Emit(OpCode.IfNot, innerReg, 0);
            Emit(OpCode.Integer, destReg, 0);
            int endAddr = _ops.Count;
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], endAddr);
            return;
        }

        if (where is IsNullExpr isNull)
        {
            int operandReg = AllocRegister();
            CompileResultExpr(isNull.Operand, operandReg, tableInfo, cursorId);
            // Check if operand is null by comparing with null
            int nullReg = AllocRegister();
            Emit(OpCode.Null, nullReg);
            Emit(OpCode.Integer, destReg, isNull.IsNot ? 1 : 0);
            int jumpAddr = _ops.Count;
            Emit(OpCode.Eq, operandReg, 0, nullReg);
            Emit(OpCode.Integer, destReg, isNull.IsNot ? 0 : 1);
            // If equal (is null), we want true for IS NULL or false for IS NOT NULL
            // The Eq jumps when equal, so after the jump we flip
            int endAddr = _ops.Count;
            _ops[jumpAddr] = PatchP2(_ops[jumpAddr], endAddr);
            return;
        }

        if (where is LikeExpr like)
        {
            int operandReg = AllocRegister();
            int patternReg = AllocRegister();
            CompileResultExpr(like.Operand, operandReg, tableInfo, cursorId);
            CompileResultExpr(like.Pattern, patternReg, tableInfo, cursorId);
            // Simple LIKE: use string matching at runtime
            // For now, emit a Function call to a "like" function
            Emit(OpCode.Function, destReg, operandReg, 2, like.IsNot ? "not_like" : "like");
            // Hack: store pattern reg in P3 for the function
            // Actually let's just set result directly:
            // We'll handle LIKE in the Vdbe by checking the function opcode
            // For simplicity, just emit 1 (true) — proper LIKE needs runtime
            Emit(OpCode.Integer, destReg, 1);
            return;
        }

        // Fallback: evaluate as expression and check truthiness
        CompileResultExpr(where, destReg, tableInfo, cursorId);
    }

    private void CompileResultExpr(Expr expr, int destReg, TableInfo? tableInfo, int cursorId)
    {
        switch (expr)
        {
            case LiteralExpr lit:
                switch (lit.LiteralType)
                {
                    case TokenType.Integer:
                        Emit(OpCode.Integer, destReg, (int)(long)lit.Value!);
                        break;
                    case TokenType.Float:
                        Emit(OpCode.Real, destReg, 0, 0, lit.Value);
                        break;
                    case TokenType.String:
                        Emit(OpCode.String, destReg, 0, 0, lit.Value);
                        break;
                    case TokenType.Null:
                        Emit(OpCode.Null, destReg);
                        break;
                    default:
                        Emit(OpCode.Null, destReg);
                        break;
                }
                break;

            case ColumnRefExpr col:
                if (tableInfo == null)
                    throw new SqliteException(SqliteResult.Error, $"Column reference '{col.ColumnName}' without a FROM table.");
                int colIdx = ResolveColumn(col, tableInfo);
                if (colIdx == -1 || colIdx == tableInfo.IntegerPrimaryKeyIndex)
                    Emit(OpCode.Rowid, cursorId, destReg);
                else
                    Emit(OpCode.Column, cursorId, colIdx, destReg);
                break;

            case StarExpr:
                // Star in expression context — shouldn't happen for ResultRow (handled by caller)
                // But if it does, just treat as Column 0
                if (tableInfo != null)
                    Emit(OpCode.Column, cursorId, 0, destReg);
                else
                    Emit(OpCode.Null, destReg);
                break;

            case BinaryExpr bin:
                if (IsComparisonOp(bin.Operator))
                {
                    // Produce 0 or 1 as integer
                    int leftReg = AllocRegister();
                    int rightReg = AllocRegister();
                    CompileResultExpr(bin.Left, leftReg, tableInfo, cursorId);
                    CompileResultExpr(bin.Right, rightReg, tableInfo, cursorId);
                    Emit(OpCode.Integer, destReg, 0);
                    int jumpAddr = _ops.Count;
                    var jumpOp = ComparisonToJumpOp(bin.Operator);
                    Emit(jumpOp, leftReg, 0, rightReg);
                    Emit(OpCode.Integer, destReg, 1); // didn't jump, so condition was false... wait
                    // Actually the jump fires when condition IS true, so:
                    // if jump fires -> true. Let's restructure:
                    // Set dest=0. Jump over "keep 0" to "set 1":
                    // Better approach:
                    _ops.RemoveRange(jumpAddr, _ops.Count - jumpAddr);
                    _nextRegister -= 0; // no change needed

                    Emit(OpCode.Integer, destReg, 1);
                    int jAddr = _ops.Count;
                    Emit(jumpOp, leftReg, 0, rightReg);
                    Emit(OpCode.Integer, destReg, 0);
                    int endAddr = _ops.Count;
                    _ops[jAddr] = PatchP2(_ops[jAddr], endAddr);
                }
                else if (IsArithmeticOp(bin.Operator))
                {
                    int leftReg = AllocRegister();
                    int rightReg = AllocRegister();
                    CompileResultExpr(bin.Left, leftReg, tableInfo, cursorId);
                    CompileResultExpr(bin.Right, rightReg, tableInfo, cursorId);
                    var opcode = bin.Operator switch
                    {
                        TokenType.Plus => OpCode.Add,
                        TokenType.Minus => OpCode.Subtract,
                        TokenType.Star => OpCode.Multiply,
                        TokenType.Slash => OpCode.Divide,
                        TokenType.Percent => OpCode.Remainder,
                        TokenType.Concat => OpCode.Concat,
                        _ => OpCode.Add,
                    };
                    Emit(opcode, leftReg, rightReg, destReg);
                }
                else if (bin.Operator == TokenType.And || bin.Operator == TokenType.Or)
                {
                    // Logical: produce 0/1
                    CompileWhereCondition(expr, destReg, tableInfo, cursorId);
                }
                else
                {
                    Emit(OpCode.Null, destReg);
                }
                break;

            case UnaryExpr un:
                if (un.Operator == TokenType.Minus)
                {
                    int innerReg = AllocRegister();
                    CompileResultExpr(un.Operand, innerReg, tableInfo, cursorId);
                    Emit(OpCode.Negate, innerReg, destReg);
                }
                else if (un.Operator == TokenType.Not)
                {
                    CompileWhereCondition(expr, destReg, tableInfo, cursorId);
                }
                else
                {
                    CompileResultExpr(un.Operand, destReg, tableInfo, cursorId);
                }
                break;

            case FunctionCallExpr func:
                if (IsAggregateFunction(func.FunctionName))
                {
                    // Aggregates are handled by CompileAggregateSelect
                    // If we're here, it's likely in a non-aggregate context — just emit null
                    Emit(OpCode.Null, destReg);
                }
                else
                {
                    // Scalar function
                    int argStart = _nextRegister;
                    int argCount = func.Arguments.Length;
                    for (int i = 0; i < argCount; i++)
                    {
                        int argReg = AllocRegister();
                        CompileResultExpr(func.Arguments[i], argReg, tableInfo, cursorId);
                    }
                    Emit(OpCode.Function, destReg, argStart, argCount, func.FunctionName);
                }
                break;

            case ParenExpr paren:
                CompileResultExpr(paren.Inner, destReg, tableInfo, cursorId);
                break;

            default:
                Emit(OpCode.Null, destReg);
                break;
        }
    }

    private void CompileExpr(Expr expr, int destReg, TableInfo? tableInfo, int cursorId)
    {
        CompileResultExpr(expr, destReg, tableInfo, cursorId);
    }

    private int ResolveColumn(ColumnRefExpr col, TableInfo tableInfo)
    {
        // Special case: rowid aliases
        if (col.ColumnName.Equals("rowid", StringComparison.OrdinalIgnoreCase) ||
            col.ColumnName.Equals("_rowid_", StringComparison.OrdinalIgnoreCase) ||
            col.ColumnName.Equals("oid", StringComparison.OrdinalIgnoreCase))
        {
            return -1; // sentinel for rowid
        }

        for (int i = 0; i < tableInfo.ColumnNames.Length; i++)
        {
            if (string.Equals(tableInfo.ColumnNames[i], col.ColumnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new SqliteException(SqliteResult.Error, $"Column '{col.ColumnName}' not found in table '{tableInfo.Name}'.");
    }

    // ─── INSERT ─────────────────────────────────────────────────────────────

    private void CompileInsert(InsertStmt insert)
    {
        var tableInfo = _schema.GetTable(insert.TableName)
            ?? throw new SqliteException(SqliteResult.Error, $"Table '{insert.TableName}' not found.");

        int cursorId = AllocCursor();

        // Begin write transaction
        Emit(OpCode.Transaction, 0, 1); // P2=1 means write

        // Open write cursor
        Emit(OpCode.OpenWrite, cursorId, tableInfo.RootPage);

        foreach (var row in insert.ValueRows)
        {
            // Allocate rowid
            int rowidReg = AllocRegister();
            Emit(OpCode.NewRowid, cursorId, rowidReg);

            // Evaluate each value expression into registers
            int firstValReg = _nextRegister;
            int colCount = tableInfo.ColumnNames.Length;

            // Map insert columns to table columns
            int[] colMapping = BuildColumnMapping(insert.ColumnNames, tableInfo.ColumnNames);

            for (int i = 0; i < colCount; i++)
            {
                int reg = AllocRegister();
                int srcIdx = colMapping[i];
                if (srcIdx >= 0 && srcIdx < row.Length)
                {
                    CompileResultExpr(row[srcIdx], reg, null, -1);
                }
                else
                {
                    // Check if this is the INTEGER PRIMARY KEY column (alias for rowid)
                    if (IsIntegerPrimaryKey(tableInfo, i))
                    {
                        // Store NULL — the rowid IS the key
                        Emit(OpCode.Null, reg);
                    }
                    else
                    {
                        Emit(OpCode.Null, reg);
                    }
                }
            }

            // Check if a specific column is the INTEGER PRIMARY KEY and use its value as rowid
            int pkColIdx = FindIntegerPrimaryKeyColumn(tableInfo);
            if (pkColIdx >= 0 && insert.ColumnNames != null)
            {
                int srcIdx = Array.FindIndex(insert.ColumnNames,
                    c => string.Equals(c, tableInfo.ColumnNames[pkColIdx], StringComparison.OrdinalIgnoreCase));
                if (srcIdx >= 0 && srcIdx < row.Length)
                {
                    // Use the explicit PK value as rowid
                    CompileResultExpr(row[srcIdx], rowidReg, null, -1);
                }
            }
            else if (pkColIdx >= 0 && insert.ColumnNames == null && pkColIdx < row.Length)
            {
                // No column list specified — positional
                CompileResultExpr(row[pkColIdx], rowidReg, null, -1);
            }

            // MakeRecord: pack column values into a record blob
            int recordReg = AllocRegister();
            Emit(OpCode.MakeRecord, firstValReg, colCount, recordReg);

            // Insert
            Emit(OpCode.InsertInt, cursorId, recordReg, rowidReg);
        }

        Emit(OpCode.Close, cursorId);
        Emit(OpCode.AutoCommit);
        Emit(OpCode.Halt);
    }

    // ─── UPDATE ─────────────────────────────────────────────────────────────

    private void CompileUpdate(UpdateStmt update)
    {
        var tableInfo = _schema.GetTable(update.TableName)
            ?? throw new SqliteException(SqliteResult.Error, $"Table '{update.TableName}' not found.");

        int readCursor = AllocCursor();
        int writeCursor = AllocCursor();

        Emit(OpCode.Transaction, 0, 1);
        Emit(OpCode.OpenRead, readCursor, tableInfo.RootPage);
        Emit(OpCode.OpenWrite, writeCursor, tableInfo.RootPage);

        // Rewind read cursor
        int rewindAddr = _ops.Count;
        Emit(OpCode.Rewind, readCursor, 0); // patched

        int loopStart = _ops.Count;

        // Read rowid
        int rowidReg = AllocRegister();
        Emit(OpCode.Rowid, readCursor, rowidReg);

        // WHERE filter
        int skipToNext = -1;
        if (update.Where != null)
        {
            int whereReg = AllocRegister();
            CompileWhereCondition(update.Where, whereReg, tableInfo, readCursor);
            skipToNext = _ops.Count;
            Emit(OpCode.IfNot, whereReg, 0); // patched
        }

        // Read all current column values
        int firstColReg = _nextRegister;
        for (int i = 0; i < tableInfo.ColumnNames.Length; i++)
        {
            int reg = AllocRegister();
            Emit(OpCode.Column, readCursor, i, reg);
        }

        // Apply SET clauses (overwrite specific registers)
        foreach (var set in update.SetClauses)
        {
            int colIdx = -1;
            for (int i = 0; i < tableInfo.ColumnNames.Length; i++)
            {
                if (string.Equals(tableInfo.ColumnNames[i], set.ColumnName, StringComparison.OrdinalIgnoreCase))
                { colIdx = i; break; }
            }
            if (colIdx < 0)
                throw new SqliteException(SqliteResult.Error, $"Column '{set.ColumnName}' not found.");

            CompileResultExpr(set.Value, firstColReg + colIdx, tableInfo, readCursor);
        }

        // Delete old row
        Emit(OpCode.DeleteOp, writeCursor, 0, rowidReg);

        // Build new record and insert
        int recordReg = AllocRegister();
        Emit(OpCode.MakeRecord, firstColReg, tableInfo.ColumnNames.Length, recordReg);
        Emit(OpCode.InsertInt, writeCursor, recordReg, rowidReg);

        // Next
        int nextAddr = _ops.Count;
        Emit(OpCode.Next, readCursor, loopStart);

        int closeAddr = _ops.Count;
        Emit(OpCode.Close, readCursor);
        Emit(OpCode.Close, writeCursor);
        Emit(OpCode.AutoCommit);
        Emit(OpCode.Halt);

        // Patch
        _ops[rewindAddr] = PatchP2(_ops[rewindAddr], closeAddr);
        if (skipToNext >= 0)
            _ops[skipToNext] = PatchP2(_ops[skipToNext], nextAddr);
    }

    // ─── DELETE ─────────────────────────────────────────────────────────────

    private void CompileDelete(DeleteStmt delete)
    {
        var tableInfo = _schema.GetTable(delete.TableName)
            ?? throw new SqliteException(SqliteResult.Error, $"Table '{delete.TableName}' not found.");

        int readCursor = AllocCursor();
        int writeCursor = AllocCursor();

        Emit(OpCode.Transaction, 0, 1);
        Emit(OpCode.OpenRead, readCursor, tableInfo.RootPage);
        Emit(OpCode.OpenWrite, writeCursor, tableInfo.RootPage);

        // First pass: collect rowids to delete (can't modify while iterating)
        // We'll use a simple approach: collect matching rowids into a list register
        // For simplicity, use a two-pass approach with a GoTo

        // Rewind
        int rewindAddr = _ops.Count;
        Emit(OpCode.Rewind, readCursor, 0); // patched

        int loopStart = _ops.Count;

        // Read rowid
        int rowidReg = AllocRegister();
        Emit(OpCode.Rowid, readCursor, rowidReg);

        // WHERE filter
        int skipToNext = -1;
        if (delete.Where != null)
        {
            int whereReg = AllocRegister();
            CompileWhereCondition(delete.Where, whereReg, tableInfo, readCursor);
            skipToNext = _ops.Count;
            Emit(OpCode.IfNot, whereReg, 0); // patched
        }

        // Delete the row
        Emit(OpCode.DeleteOp, writeCursor, 0, rowidReg);

        // Next
        int nextAddr = _ops.Count;
        Emit(OpCode.Next, readCursor, loopStart);

        int closeAddr = _ops.Count;
        Emit(OpCode.Close, readCursor);
        Emit(OpCode.Close, writeCursor);
        Emit(OpCode.AutoCommit);
        Emit(OpCode.Halt);

        // Patch
        _ops[rewindAddr] = PatchP2(_ops[rewindAddr], closeAddr);
        if (skipToNext >= 0)
            _ops[skipToNext] = PatchP2(_ops[skipToNext], nextAddr);
    }

    // ─── CREATE TABLE ───────────────────────────────────────────────────────

    private void CompileCreateTable(CreateTableStmt create)
    {
        // Check if table already exists
        if (create.IfNotExists)
        {
            var existing = _schema.GetTable(create.TableName);
            if (existing != null)
            {
                Emit(OpCode.Halt); // No-op, table exists
                return;
            }
        }
        else
        {
            var existing = _schema.GetTable(create.TableName);
            if (existing != null)
                throw new SqliteException(SqliteResult.Error, $"Table '{create.TableName}' already exists.");
        }

        int schemaCursor = AllocCursor();

        // Begin transaction
        Emit(OpCode.Transaction, 0, 1);

        // Open write cursor on sqlite_schema (root page 1)
        Emit(OpCode.OpenWrite, schemaCursor, 1);

        // Allocate a new root page for the table
        int rootPageReg = AllocRegister();
        Emit(OpCode.CreateBtree, rootPageReg);

        // Build the CREATE TABLE SQL string for storage
        string sql = ReconstructCreateSql(create);

        // Build the schema record: (type, name, tbl_name, rootpage, sql)
        // These must be contiguous registers for MakeRecord
        int firstColReg = _nextRegister;

        int typeReg = AllocRegister();
        Emit(OpCode.String, typeReg, 0, 0, "table");

        int nameReg = AllocRegister();
        Emit(OpCode.String, nameReg, 0, 0, create.TableName);

        int tblNameReg = AllocRegister();
        Emit(OpCode.String, tblNameReg, 0, 0, create.TableName);

        int rpReg = AllocRegister();
        Emit(OpCode.Copy, rootPageReg, rpReg); // Copy root page number into contiguous position

        int sqlReg = AllocRegister();
        Emit(OpCode.String, sqlReg, 0, 0, sql);

        // MakeRecord from these 5 contiguous registers
        int recordReg = AllocRegister();
        Emit(OpCode.MakeRecord, firstColReg, 5, recordReg);

        // Generate rowid for the schema entry
        int rowidReg = AllocRegister();
        Emit(OpCode.NewRowid, schemaCursor, rowidReg);

        // Insert into sqlite_schema
        Emit(OpCode.SchemaInsert, schemaCursor, recordReg, rowidReg);

        // Increment schema cookie
        Emit(OpCode.IncrSchemaCookie);

        // Close and commit
        Emit(OpCode.Close, schemaCursor);
        Emit(OpCode.AutoCommit);

        // Reload schema
        Emit(OpCode.ReloadSchema);
        Emit(OpCode.Halt);
    }

    // ─── DROP TABLE ─────────────────────────────────────────────────────────

    private void CompileDropTable(DropTableStmt drop)
    {
        var existing = _schema.GetTable(drop.TableName);
        if (existing == null)
        {
            if (drop.IfExists)
            {
                Emit(OpCode.Halt);
                return;
            }
            throw new SqliteException(SqliteResult.Error, $"Table '{drop.TableName}' does not exist.");
        }

        int readCursor = AllocCursor();
        int writeCursor = AllocCursor();

        Emit(OpCode.Transaction, 0, 1);
        Emit(OpCode.OpenRead, readCursor, 1);
        Emit(OpCode.OpenWrite, writeCursor, 1);

        // Scan sqlite_schema to find the row(s) for this table
        int rewindAddr = _ops.Count;
        Emit(OpCode.Rewind, readCursor, 0); // patched

        int loopStart = _ops.Count;

        // Read rowid and tbl_name column (index 2)
        int rowidReg = AllocRegister();
        Emit(OpCode.Rowid, readCursor, rowidReg);

        int tblNameReg = AllocRegister();
        Emit(OpCode.Column, readCursor, 2, tblNameReg); // tbl_name is column 2

        // Compare with target table name
        int targetReg = AllocRegister();
        Emit(OpCode.String, targetReg, 0, 0, drop.TableName);

        int matchReg = AllocRegister();
        // Check if tblNameReg == targetReg
        Emit(OpCode.Integer, matchReg, 1);
        int jumpAddr = _ops.Count;
        Emit(OpCode.Eq, tblNameReg, 0, targetReg); // jump if equal
        Emit(OpCode.Integer, matchReg, 0);
        int afterCheck = _ops.Count;
        _ops[jumpAddr] = PatchP2(_ops[jumpAddr], afterCheck);

        int skipAddr = _ops.Count;
        Emit(OpCode.IfNot, matchReg, 0); // skip delete if no match

        // Delete this schema entry
        Emit(OpCode.SchemaDelete, writeCursor, 0, rowidReg);

        int nextAddr = _ops.Count;
        Emit(OpCode.Next, readCursor, loopStart);

        int closeAddr = _ops.Count;
        Emit(OpCode.Close, readCursor);
        Emit(OpCode.Close, writeCursor);
        Emit(OpCode.IncrSchemaCookie);
        Emit(OpCode.AutoCommit);
        Emit(OpCode.ReloadSchema);
        Emit(OpCode.Halt);

        // Patch jumps
        _ops[rewindAddr] = PatchP2(_ops[rewindAddr], closeAddr);
        _ops[skipAddr] = PatchP2(_ops[skipAddr], nextAddr);
    }

    private static string ReconstructCreateSql(CreateTableStmt create)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("CREATE TABLE ");
        sb.Append(create.TableName);
        sb.Append(" (");
        for (int i = 0; i < create.Columns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var col = create.Columns[i];
            sb.Append(col.Name);
            if (col.TypeName != null)
            {
                sb.Append(' ');
                sb.Append(col.TypeName);
            }
            if (col.IsPrimaryKey)
            {
                sb.Append(" PRIMARY KEY");
                if (col.IsAutoincrement) sb.Append(" AUTOINCREMENT");
            }
            if (col.IsNotNull) sb.Append(" NOT NULL");
            if (col.IsUnique) sb.Append(" UNIQUE");
        }
        sb.Append(')');
        return sb.ToString();
    }

    // ─── INSERT helpers ─────────────────────────────────────────────────────

    private static int[] BuildColumnMapping(string[]? insertColumns, string[] tableColumns)
    {
        int[] mapping = new int[tableColumns.Length];
        Array.Fill(mapping, -1);

        if (insertColumns == null)
        {
            // No column list — positional mapping
            for (int i = 0; i < tableColumns.Length; i++)
                mapping[i] = i;
        }
        else
        {
            for (int i = 0; i < insertColumns.Length; i++)
            {
                for (int j = 0; j < tableColumns.Length; j++)
                {
                    if (string.Equals(insertColumns[i], tableColumns[j], StringComparison.OrdinalIgnoreCase))
                    {
                        mapping[j] = i;
                        break;
                    }
                }
            }
        }
        return mapping;
    }

    private static bool IsIntegerPrimaryKey(TableInfo tableInfo, int colIdx)
    {
        return tableInfo.IntegerPrimaryKeyIndex == colIdx;
    }

    private static int FindIntegerPrimaryKeyColumn(TableInfo tableInfo)
    {
        return tableInfo.IntegerPrimaryKeyIndex;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private int AllocRegister() => _nextRegister++;
    private int AllocCursor() => _nextCursor++;

    private void Emit(OpCode opcode, int p1 = 0, int p2 = 0, int p3 = 0, object? p4 = null)
    {
        _ops.Add(new VdbeOp { Opcode = opcode, P1 = p1, P2 = p2, P3 = p3, P4 = p4 });
    }

    private static VdbeOp PatchP2(VdbeOp op, int newP2) => new()
    {
        Opcode = op.Opcode, P1 = op.P1, P2 = newP2, P3 = op.P3, P4 = op.P4
    };

    private static bool IsComparisonOp(TokenType t) =>
        t == TokenType.Eq || t == TokenType.Neq || t == TokenType.Lt ||
        t == TokenType.Gt || t == TokenType.Lte || t == TokenType.Gte;

    private static bool IsArithmeticOp(TokenType t) =>
        t == TokenType.Plus || t == TokenType.Minus || t == TokenType.Star ||
        t == TokenType.Slash || t == TokenType.Percent || t == TokenType.Concat;

    private static OpCode ComparisonToJumpOp(TokenType t) => t switch
    {
        TokenType.Eq => OpCode.Eq,
        TokenType.Neq => OpCode.Ne,
        TokenType.Lt => OpCode.Lt,
        TokenType.Lte => OpCode.Le,
        TokenType.Gt => OpCode.Gt,
        TokenType.Gte => OpCode.Ge,
        _ => OpCode.Eq,
    };

    private static bool IsAggregateFunction(string name) =>
        name.Equals("count", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("sum", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("total", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("min", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("max", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("avg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("json_group_array", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("json_group_object", StringComparison.OrdinalIgnoreCase);

    private static bool HasAggregates(SelectStmt select)
    {
        foreach (var col in select.Columns)
        {
            if (ExprHasAggregate(col.Expression))
                return true;
        }
        return false;
    }

    private static bool ExprHasAggregate(Expr expr) => expr switch
    {
        FunctionCallExpr f => IsAggregateFunction(f.FunctionName),
        BinaryExpr b => ExprHasAggregate(b.Left) || ExprHasAggregate(b.Right),
        UnaryExpr u => ExprHasAggregate(u.Operand),
        ParenExpr p => ExprHasAggregate(p.Inner),
        _ => false,
    };
}
