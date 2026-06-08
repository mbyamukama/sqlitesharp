using SQLite.Engine;
using SQLite.Engine.Vdbe;
using SQLite.Engine.Compiler;

namespace SQLite.Engine.Tests;

/// <summary>
/// Integration tests for the VDBE + CodeGen (Phase 3).
/// These tests run full SQL queries against sample.db.
/// </summary>
public class VdbeTests : IDisposable
{
    private readonly Database _db;

    public VdbeTests()
    {
        // Find sample.db relative to the test output directory
        string dbPath = FindSampleDb();
        _db = new Database(dbPath);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void SelectStarReturnsAllRows()
    {
        var rows = _db.Execute("SELECT * FROM users");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void SelectColumnProjection()
    {
        var rows = _db.Execute("SELECT name FROM users");
        Assert.Equal(3, rows.Count);
        // Each row should have exactly 1 column
        Assert.Single(rows[0]);
        Assert.Equal("Alice", rows[0][0]);
        Assert.Equal("Bob", rows[1][0]);
        Assert.Equal("Carol", rows[2][0]);
    }

    [Fact]
    public void SelectMultipleColumns()
    {
        var rows = _db.Execute("SELECT name, age FROM users");
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[0].Length);
        Assert.Equal("Alice", rows[0][0]);
        Assert.Equal(30L, rows[0][1]);
    }

    [Fact]
    public void WhereFilterGt()
    {
        var rows = _db.Execute("SELECT name FROM users WHERE age > 25");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", rows[0][0]);
        Assert.Equal("Carol", rows[1][0]);
    }

    [Fact]
    public void WhereFilterEq()
    {
        var rows = _db.Execute("SELECT name FROM users WHERE name = 'Bob'");
        Assert.Single(rows);
        Assert.Equal("Bob", rows[0][0]);
    }

    [Fact]
    public void WhereFilterLt()
    {
        var rows = _db.Execute("SELECT name FROM users WHERE age < 30");
        Assert.Single(rows);
        Assert.Equal("Bob", rows[0][0]);
    }

    [Fact]
    public void WhereFilterAnd()
    {
        var rows = _db.Execute("SELECT name FROM users WHERE age >= 25 AND age <= 30");
        Assert.Equal(2, rows.Count); // Alice(30), Bob(25)
    }

    [Fact]
    public void WhereFilterOr()
    {
        var rows = _db.Execute("SELECT name FROM users WHERE name = 'Alice' OR name = 'Carol'");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void LimitClause()
    {
        var rows = _db.Execute("SELECT name FROM users LIMIT 2");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void LimitOne()
    {
        var rows = _db.Execute("SELECT name FROM users LIMIT 1");
        Assert.Single(rows);
        Assert.Equal("Alice", rows[0][0]);
    }

    [Fact]
    public void SelectWithoutFrom()
    {
        var rows = _db.Execute("SELECT 42");
        Assert.Single(rows);
        Assert.Equal(42L, rows[0][0]);
    }

    [Fact]
    public void ArithmeticExpression()
    {
        var rows = _db.Execute("SELECT 10 + 5 * 2");
        Assert.Single(rows);
        Assert.Equal(20L, rows[0][0]);
    }

    [Fact]
    public void ArithmeticDivision()
    {
        var rows = _db.Execute("SELECT 10 / 3");
        Assert.Single(rows);
        Assert.Equal(3L, rows[0][0]); // integer division
    }

    [Fact]
    public void StringConcatenation()
    {
        var rows = _db.Execute("SELECT 'hello' || ' ' || 'world'");
        Assert.Single(rows);
        Assert.Equal("hello world", rows[0][0]);
    }

    [Fact]
    public void UnaryMinus()
    {
        var rows = _db.Execute("SELECT -5");
        Assert.Single(rows);
        Assert.Equal(-5L, rows[0][0]);
    }

    [Fact]
    public void AggregateCount()
    {
        var rows = _db.Execute("SELECT count(*) FROM users");
        Assert.Single(rows);
        Assert.Equal(3L, rows[0][0]);
    }

    [Fact]
    public void AggregateCountWithWhere()
    {
        var rows = _db.Execute("SELECT count(*) FROM users WHERE age > 25");
        Assert.Single(rows);
        Assert.Equal(2L, rows[0][0]);
    }

    [Fact]
    public void AggregateMax()
    {
        var rows = _db.Execute("SELECT max(age) FROM users");
        Assert.Single(rows);
        Assert.Equal(35L, rows[0][0]);
    }

    [Fact]
    public void AggregateMin()
    {
        var rows = _db.Execute("SELECT min(age) FROM users");
        Assert.Single(rows);
        Assert.Equal(25L, rows[0][0]);
    }

    [Fact]
    public void AggregateSum()
    {
        var rows = _db.Execute("SELECT sum(age) FROM users");
        Assert.Single(rows);
        Assert.Equal(90L, rows[0][0]); // 30+25+35
    }

    [Fact]
    public void ScalarFunctionUpper()
    {
        var rows = _db.Execute("SELECT upper(name) FROM users LIMIT 1");
        Assert.Single(rows);
        Assert.Equal("ALICE", rows[0][0]);
    }

    [Fact]
    public void ScalarFunctionLower()
    {
        var rows = _db.Execute("SELECT lower(name) FROM users LIMIT 1");
        Assert.Single(rows);
        Assert.Equal("alice", rows[0][0]);
    }

    [Fact]
    public void ScalarFunctionLength()
    {
        var rows = _db.Execute("SELECT length(name) FROM users WHERE name = 'Bob'");
        Assert.Single(rows);
        Assert.Equal(3L, rows[0][0]);
    }

    [Fact]
    public void ScalarFunctionTypeof()
    {
        var rows = _db.Execute("SELECT typeof(age) FROM users LIMIT 1");
        Assert.Single(rows);
        Assert.Equal("integer", rows[0][0]);
    }

    [Fact]
    public void SelectNull()
    {
        var rows = _db.Execute("SELECT NULL");
        Assert.Single(rows);
        Assert.Null(rows[0][0]);
    }

    [Fact]
    public void SelectFloat()
    {
        var rows = _db.Execute("SELECT 3.14");
        Assert.Single(rows);
        Assert.Equal(3.14, rows[0][0]);
    }

    [Fact]
    public void SelectString()
    {
        var rows = _db.Execute("SELECT 'hello'");
        Assert.Single(rows);
        Assert.Equal("hello", rows[0][0]);
    }

    [Fact]
    public void PrepareGivesColumnNames()
    {
        var stmt = _db.Prepare("SELECT name, age FROM users");
        Assert.Equal(2, stmt.ColumnCount);
        Assert.Equal("name", stmt.ColumnNames[0]);
        Assert.Equal("age", stmt.ColumnNames[1]);
    }

    [Fact]
    public void StatementStepReturnsRowThenDone()
    {
        var stmt = _db.Prepare("SELECT name FROM users LIMIT 1");
        Assert.Equal(SqliteResult.Row, stmt.Step());
        Assert.Equal("Alice", stmt.CurrentRow[0]);
        Assert.Equal(SqliteResult.Done, stmt.Step());
    }

    [Fact]
    public void ExplainShowsBytecode()
    {
        var stmt = _db.Prepare("SELECT name FROM users");
        var explain = stmt.Explain();
        Assert.Contains("OpenRead", explain);
        Assert.Contains("Rewind", explain);
        Assert.Contains("Column", explain);
        Assert.Contains("ResultRow", explain);
        Assert.Contains("Next", explain);
        Assert.Contains("Close", explain);
        Assert.Contains("Halt", explain);
    }

    [Fact]
    public void InvalidTableThrows()
    {
        Assert.Throws<SqliteException>(() => _db.Execute("SELECT * FROM nonexistent"));
    }

    [Fact]
    public void InvalidColumnThrows()
    {
        Assert.Throws<SqliteException>(() => _db.Execute("SELECT nosuchcol FROM users"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string FindSampleDb()
    {
        // Walk up from test output directory to find sample.db in the repo root
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidate = Path.Combine(dir, "sample.db");
            if (File.Exists(candidate)) return candidate;
            // Also check the sqlite repo root (parent of sqlite-cs)
            candidate = Path.Combine(dir, "..", "sample.db");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        // Direct path for CI/local
        string fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample.db"));
        if (File.Exists(fallback)) return fallback;

        throw new FileNotFoundException("Cannot find sample.db for testing.");
    }
}
