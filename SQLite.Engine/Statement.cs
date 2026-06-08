using SQLite.Engine.Compiler;
using SQLite.Engine.Vdbe;

namespace SQLite.Engine;

/// <summary>
/// A prepared statement — a compiled VDBE program ready for execution.
/// Call Step() to get rows, then Reset() to run again.
/// </summary>
public sealed class Statement
{
    private readonly Vdbe.Vdbe _vm;
    private readonly string[] _columnNames;

    /// <summary>Column names for the result set.</summary>
    public IReadOnlyList<string> ColumnNames => _columnNames;

    /// <summary>Number of columns in the result set.</summary>
    public int ColumnCount => _columnNames.Length;

    /// <summary>The current result row (valid after Step() returns Row).</summary>
    public object?[] CurrentRow => _vm.CurrentRow ?? [];

    internal Statement(Vdbe.Vdbe vm, string[] columnNames)
    {
        _vm = vm;
        _columnNames = columnNames;
    }

    /// <summary>
    /// Execute one step. Returns Row if a row is available, Done if finished.
    /// </summary>
    public SqliteResult Step() => _vm.Step();

    /// <summary>
    /// Get the VDBE program listing for debugging (EXPLAIN).
    /// </summary>
    public string Explain() => _vm.Explain();
}
