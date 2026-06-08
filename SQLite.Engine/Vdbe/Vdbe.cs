using SQLite.Engine.BTree;
using SQLite.Engine.Func;
using SQLite.Engine.IO;

namespace SQLite.Engine.Vdbe;

/// <summary>
/// The Virtual Database Engine — executes a program of VdbeOp instructions.
/// This is the core execution loop equivalent to sqlite3VdbeExec() in the C code.
/// </summary>
public sealed class Vdbe
{
    private readonly VdbeOp[] _program;
    private readonly Mem[] _registers;
    private readonly BTreeCursor?[] _cursors;
    private readonly BTreeWriter?[] _writers;
    private readonly Pager _pager;
    private int _pc; // program counter
    private bool _halted;

    /// <summary>The last result row produced by ResultRow opcode.</summary>
    public object?[]? CurrentRow { get; private set; }

    public Vdbe(VdbeOp[] program, int registerCount, int cursorCount, Pager pager)
    {
        _program = program;
        _registers = new Mem[registerCount];
        for (int i = 0; i < registerCount; i++)
            _registers[i] = Mem.MakeNull();
        _cursors = new BTreeCursor?[cursorCount];
        _writers = new BTreeWriter?[cursorCount];
        _pager = pager;
        _pc = 0;
        _halted = false;
    }

    /// <summary>
    /// Callback invoked after schema changes (CREATE/DROP TABLE).
    /// Set by the Database class to trigger schema reload.
    /// </summary>
    public Action? OnSchemaChange { get; set; }

    /// <summary>
    /// Execute until a ResultRow is produced (returns Row) or the program halts (returns Done).
    /// Call repeatedly to get all rows.
    /// </summary>
    public SqliteResult Step()
    {
        if (_halted) return SqliteResult.Done;
        CurrentRow = null;

        while (_pc < _program.Length)
        {
            var op = _program[_pc];
            _pc++;

            switch (op.Opcode)
            {
                case OpCode.Halt:
                    _halted = true;
                    return SqliteResult.Done;

                case OpCode.Goto:
                    _pc = op.P2;
                    break;

                case OpCode.OpenRead:
                {
                    int cursorId = op.P1;
                    int rootPage = op.P2;
                    var btree = new BTree.BTree(_pager, rootPage);
                    _cursors[cursorId] = btree.OpenCursor();
                    break;
                }

                case OpCode.Rewind:
                {
                    var cursor = _cursors[op.P1]!;
                    cursor.MoveToFirst();
                    if (cursor.Eof)
                        _pc = op.P2; // jump to P2 (skip loop body)
                    break;
                }

                case OpCode.Next:
                {
                    var cursor = _cursors[op.P1]!;
                    cursor.Next();
                    if (!cursor.Eof)
                        _pc = op.P2; // jump back to loop body
                    break;
                }

                case OpCode.Column:
                {
                    var cursor = _cursors[op.P1]!;
                    var record = cursor.GetRecord();
                    int colIdx = op.P2;
                    int destReg = op.P3;
                    if (colIdx == -1)
                    {
                        // Special: rowid
                        _registers[destReg].SetInt(record.RowId);
                    }
                    else if (colIdx < record.Values.Length)
                    {
                        _registers[destReg].SetFromObject(record.Values[colIdx]);
                    }
                    else
                    {
                        _registers[destReg].SetNull();
                    }
                    break;
                }

                case OpCode.Rowid:
                {
                    var cursor = _cursors[op.P1]!;
                    var record = cursor.GetRecord();
                    _registers[op.P2].SetInt(record.RowId);
                    break;
                }

                case OpCode.ResultRow:
                {
                    int startReg = op.P1;
                    int count = op.P2;
                    var row = new object?[count];
                    for (int i = 0; i < count; i++)
                        row[i] = _registers[startReg + i].ToObject();
                    CurrentRow = row;
                    return SqliteResult.Row;
                }

                case OpCode.Close:
                    _cursors[op.P1] = null;
                    break;

                case OpCode.Integer:
                    _registers[op.P1].SetInt(op.P2);
                    break;

                case OpCode.String:
                    _registers[op.P1].SetText((string)op.P4!);
                    break;

                case OpCode.Real:
                    _registers[op.P1].SetDouble((double)op.P4!);
                    break;

                case OpCode.Null:
                    _registers[op.P1].SetNull();
                    break;

                case OpCode.Copy:
                    _registers[op.P2].CopyFrom(_registers[op.P1]);
                    break;

                // ── Comparison ops: compare reg[P1] with reg[P3], jump to P2 on true ──

                case OpCode.Eq:
                    if (Mem.Compare(_registers[op.P1], _registers[op.P3]) == 0)
                        _pc = op.P2;
                    break;

                case OpCode.Ne:
                    if (Mem.Compare(_registers[op.P1], _registers[op.P3]) != 0)
                        _pc = op.P2;
                    break;

                case OpCode.Lt:
                    if (Mem.Compare(_registers[op.P1], _registers[op.P3]) < 0)
                        _pc = op.P2;
                    break;

                case OpCode.Le:
                    if (Mem.Compare(_registers[op.P1], _registers[op.P3]) <= 0)
                        _pc = op.P2;
                    break;

                case OpCode.Gt:
                    if (Mem.Compare(_registers[op.P1], _registers[op.P3]) > 0)
                        _pc = op.P2;
                    break;

                case OpCode.Ge:
                    if (Mem.Compare(_registers[op.P1], _registers[op.P3]) >= 0)
                        _pc = op.P2;
                    break;

                // ── Conditional jumps ──

                case OpCode.If:
                    if (_registers[op.P1].IsTrue())
                        _pc = op.P2;
                    break;

                case OpCode.IfNot:
                    if (!_registers[op.P1].IsTrue())
                        _pc = op.P2;
                    break;

                // ── Arithmetic ──

                case OpCode.Add:
                    DoArithmetic(op.P1, op.P2, op.P3, (a, b) => a + b, (a, b) => a + b);
                    break;

                case OpCode.Subtract:
                    DoArithmetic(op.P1, op.P2, op.P3, (a, b) => a - b, (a, b) => a - b);
                    break;

                case OpCode.Multiply:
                    DoArithmetic(op.P1, op.P2, op.P3, (a, b) => a * b, (a, b) => a * b);
                    break;

                case OpCode.Divide:
                    DoArithmetic(op.P1, op.P2, op.P3, (a, b) => b != 0 ? a / b : 0, (a, b) => b != 0 ? a / b : 0);
                    break;

                case OpCode.Remainder:
                    DoArithmetic(op.P1, op.P2, op.P3, (a, b) => b != 0 ? a % b : 0, (a, b) => b != 0 ? a % b : 0);
                    break;

                case OpCode.Concat:
                {
                    string left = _registers[op.P1].ToText();
                    string right = _registers[op.P2].ToText();
                    _registers[op.P3].SetText(left + right);
                    break;
                }

                case OpCode.Negate:
                    if (_registers[op.P1].Type == MemType.Double)
                        _registers[op.P2].SetDouble(-_registers[op.P1].RealValue);
                    else
                        _registers[op.P2].SetInt(-_registers[op.P1].ToInt64());
                    break;

                case OpCode.DecrJumpZero:
                {
                    long val = _registers[op.P1].ToInt64() - 1;
                    _registers[op.P1].SetInt(val);
                    if (val == 0)
                        _pc = op.P2;
                    break;
                }

                case OpCode.Function:
                {
                    string funcName = (string)op.P4!;
                    int destReg = op.P1;
                    int firstArg = op.P2;
                    int argCount = op.P3;
                    ExecuteFunction(funcName, destReg, firstArg, argCount);
                    break;
                }

                case OpCode.AggStep:
                {
                    string funcName = (string)op.P4!;
                    int destReg = op.P1;
                    int firstArg = op.P2;
                    int argCount = op.P3;
                    ExecuteAggStep(funcName, destReg, firstArg, argCount);
                    break;
                }

                case OpCode.AggFinal:
                {
                    string funcName = (string)op.P4!;
                    int destReg = op.P1;
                    ExecuteAggFinal(funcName, destReg);
                    break;
                }

                // ── Write opcodes (Phase 4) ──

                case OpCode.Transaction:
                {
                    // P2=1 means write transaction
                    if (op.P2 == 1)
                        _pager.Begin();
                    break;
                }

                case OpCode.AutoCommit:
                {
                    _pager.Commit();
                    break;
                }

                case OpCode.OpenWrite:
                {
                    int cursorId = op.P1;
                    int rootPage = op.P2;
                    var btree = new BTree.BTree(_pager, rootPage);
                    _cursors[cursorId] = btree.OpenCursor();
                    _writers[cursorId] = new BTreeWriter(_pager, rootPage);
                    break;
                }

                case OpCode.NewRowid:
                {
                    int cursorId = op.P1;
                    int destReg2 = op.P2;
                    var writer = _writers[cursorId]!;
                    long newId = writer.GetMaxRowId() + 1;
                    _registers[destReg2].SetInt(newId);
                    break;
                }

                case OpCode.MakeRecord:
                {
                    int firstReg = op.P1;
                    int fieldCount = op.P2;
                    int destReg2 = op.P3;
                    var values = new object?[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                        values[i] = _registers[firstReg + i].ToObject();
                    byte[] record = Cell.BuildRecord(values);
                    _registers[destReg2].SetBlob(record);
                    break;
                }

                case OpCode.InsertInt:
                {
                    int cursorId = op.P1;
                    int dataReg = op.P2;
                    int keyReg = op.P3;
                    var writer = _writers[cursorId]!;
                    long rowId = _registers[keyReg].ToInt64();
                    byte[] record = _registers[dataReg].BlobValue!;
                    writer.Insert(rowId, record);
                    break;
                }

                case OpCode.DeleteOp:
                {
                    int cursorId = op.P1;
                    int keyReg = op.P3;
                    var writer = _writers[cursorId]!;
                    long rowId = _registers[keyReg].ToInt64();
                    writer.Delete(rowId);
                    break;
                }

                case OpCode.SeekRowid:
                {
                    int cursorId = op.P1;
                    int keyReg = op.P3;
                    long rowId = _registers[keyReg].ToInt64();
                    // For now, just store the rowid for the subsequent delete
                    // The cursor doesn't need to physically seek for our implementation
                    break;
                }

                // ── DDL opcodes (Phase 5) ──

                case OpCode.CreateBtree:
                {
                    // Allocate a new page and initialize as empty leaf table
                    int destReg = op.P1;
                    int newPageNum = _pager.AllocatePage();
                    byte[] newPage = _pager.GetPageWritable(newPageNum);
                    // Initialize as leaf table page (type 13)
                    newPage[0] = 13;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(newPage.AsSpan(1), 0);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(newPage.AsSpan(3), 0);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(newPage.AsSpan(5), (ushort)_pager.PageSize);
                    newPage[7] = 0;
                    _registers[destReg].SetInt(newPageNum);
                    break;
                }

                case OpCode.SchemaInsert:
                {
                    // Insert into sqlite_schema (root page 1)
                    int cursorId = op.P1;
                    int dataReg = op.P2;
                    int keyReg = op.P3;
                    var writer = _writers[cursorId]!;
                    long rowId = _registers[keyReg].ToInt64();
                    byte[] record = _registers[dataReg].BlobValue!;
                    writer.Insert(rowId, record);
                    break;
                }

                case OpCode.SchemaDelete:
                {
                    int cursorId = op.P1;
                    int keyReg = op.P3;
                    var writer = _writers[cursorId]!;
                    long rowId = _registers[keyReg].ToInt64();
                    writer.Delete(rowId);
                    break;
                }

                case OpCode.IncrSchemaCookie:
                {
                    // Increment the schema cookie at offset 40 in page 1
                    byte[] page1 = _pager.GetPageWritable(1);
                    int cookie = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(page1.AsSpan(40));
                    cookie++;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(40), (uint)cookie);
                    break;
                }

                case OpCode.ReloadSchema:
                {
                    OnSchemaChange?.Invoke();
                    break;
                }

                default:
                    throw new SqliteException(SqliteResult.Internal, $"Unimplemented opcode: {op.Opcode}");
            }
        }

        _halted = true;
        return SqliteResult.Done;
    }

    private void DoArithmetic(int regA, int regB, int regDest,
        Func<long, long, long> intOp, Func<double, double, double> realOp)
    {
        var a = _registers[regA];
        var b = _registers[regB];

        if (a.Type == MemType.Null || b.Type == MemType.Null)
        {
            _registers[regDest].SetNull();
            return;
        }

        if (a.Type == MemType.Double || b.Type == MemType.Double)
            _registers[regDest].SetDouble(realOp(a.ToDouble(), b.ToDouble()));
        else
            _registers[regDest].SetInt(intOp(a.ToInt64(), b.ToInt64()));
    }

    private void ExecuteFunction(string name, int destReg, int firstArg, int argCount)
    {
        switch (name.ToLowerInvariant())
        {
            case "abs":
                if (argCount >= 1)
                {
                    var v = _registers[firstArg];
                    if (v.Type == MemType.Double)
                        _registers[destReg].SetDouble(Math.Abs(v.RealValue));
                    else if (v.Type == MemType.Int64)
                        _registers[destReg].SetInt(Math.Abs(v.IntValue));
                    else
                        _registers[destReg].SetNull();
                }
                break;

            case "length":
                if (argCount >= 1)
                {
                    var v = _registers[firstArg];
                    if (v.Type == MemType.Text)
                        _registers[destReg].SetInt(v.TextValue?.Length ?? 0);
                    else if (v.Type == MemType.Blob)
                        _registers[destReg].SetInt(v.BlobValue?.Length ?? 0);
                    else if (v.Type == MemType.Null)
                        _registers[destReg].SetNull();
                    else
                        _registers[destReg].SetInt(v.ToText().Length);
                }
                break;

            case "upper":
                if (argCount >= 1)
                    _registers[destReg].SetText(_registers[firstArg].ToText().ToUpperInvariant());
                break;

            case "lower":
                if (argCount >= 1)
                    _registers[destReg].SetText(_registers[firstArg].ToText().ToLowerInvariant());
                break;

            case "typeof":
                if (argCount >= 1)
                {
                    var typeName = _registers[firstArg].Type switch
                    {
                        MemType.Null => "null",
                        MemType.Int64 => "integer",
                        MemType.Double => "real",
                        MemType.Text => "text",
                        MemType.Blob => "blob",
                        _ => "null"
                    };
                    _registers[destReg].SetText(typeName);
                }
                break;

            default:
                // Try math functions
                if (MathFunctions.Execute(name.ToLowerInvariant(), _registers, destReg, firstArg, argCount))
                    break;
                // Try JSON functions
                if (JsonFunctions.Execute(name.ToLowerInvariant(), _registers, destReg, firstArg, argCount))
                    break;
                _registers[destReg].SetNull();
                break;
        }
    }

    private void ExecuteAggStep(string name, int destReg, int firstArg, int argCount)
    {
        var acc = _registers[destReg];

        switch (name.ToLowerInvariant())
        {
            case "count":
                acc.AggCount++;
                break;

            case "sum":
            case "total":
                if (argCount >= 1)
                {
                    var v = _registers[firstArg];
                    if (v.Type != MemType.Null)
                    {
                        if (acc.Type == MemType.Null && name.Equals("sum", StringComparison.OrdinalIgnoreCase))
                            acc.SetInt(0);
                        if (v.Type == MemType.Double || acc.Type == MemType.Double)
                            acc.SetDouble(acc.ToDouble() + v.ToDouble());
                        else
                            acc.SetInt(acc.ToInt64() + v.ToInt64());
                    }
                }
                break;

            case "min":
                if (argCount >= 1)
                {
                    var v = _registers[firstArg];
                    if (v.Type != MemType.Null)
                    {
                        if (acc.AggCount == 0 || Mem.Compare(v, acc) < 0)
                        {
                            acc.CopyFrom(v);
                        }
                    }
                }
                acc.AggCount++;
                break;

            case "max":
                if (argCount >= 1)
                {
                    var v = _registers[firstArg];
                    if (v.Type != MemType.Null)
                    {
                        if (acc.AggCount == 0 || Mem.Compare(v, acc) > 0)
                        {
                            acc.CopyFrom(v);
                        }
                    }
                }
                acc.AggCount++;
                break;

            default:
                // Try JSON aggregate functions
                JsonFunctions.ExecuteAggStep(name.ToLowerInvariant(), _registers, destReg, firstArg, argCount);
                break;
        }
    }

    private void ExecuteAggFinal(string name, int destReg)
    {
        var acc = _registers[destReg];

        switch (name.ToLowerInvariant())
        {
            case "count":
                acc.SetInt(acc.AggCount);
                break;

            case "total":
                if (acc.Type == MemType.Null)
                    acc.SetDouble(0.0);
                break;

            // sum, min, max: value is already in the register

            default:
                // Try JSON aggregate finals
                JsonFunctions.ExecuteAggFinal(name.ToLowerInvariant(), _registers, destReg);
                break;
        }
    }

    /// <summary>Dump the program for debugging.</summary>
    public string Explain()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _program.Length; i++)
        {
            sb.AppendLine($"  {i,3}: {_program[i]}");
        }
        return sb.ToString();
    }
}
