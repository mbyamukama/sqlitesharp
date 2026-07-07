using System.Diagnostics;
using SQLite.Engine;

namespace SQLite.Engine.Tests;

/// <summary>
/// Tests for LEFT OUTER JOIN correctness, including cross-engine validation
/// against the official sqlite3 binary to confirm binary-compatible output.
///
/// Schema:
///   departments(dept_id INTEGER PK, dept_name TEXT)  — 4 rows, one with no employees
///   employees(emp_id INTEGER PK, name TEXT, dept_id INTEGER, salary INTEGER) — 5 rows
/// </summary>
public class OuterJoinTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string? _sqlite3Exe;

    public OuterJoinTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"sqlite_cs_outer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "join_test.db");

        // Locate sqlite3.exe — try several known locations
        foreach (var candidate in new[]
        {
            @"C:\msys64\ucrt64\bin\sqlite3.exe",
            @"C:\AndroidSDK\platform-tools\sqlite3.exe",
            @"d:\code\open\sqlite\sqlite3.exe",
        })
        {
            if (File.Exists(candidate)) { _sqlite3Exe = candidate; break; }
        }

        // Build the database using our engine
        DatabaseFactory.CreateNew(_dbPath);
        using var db = new Database(_dbPath, readOnly: false);

        db.Execute("CREATE TABLE departments (dept_id INTEGER PRIMARY KEY, dept_name TEXT NOT NULL)");
        db.Execute("INSERT INTO departments VALUES (1, 'Engineering')");
        db.Execute("INSERT INTO departments VALUES (2, 'Marketing')");
        db.Execute("INSERT INTO departments VALUES (3, 'HR')");
        db.Execute("INSERT INTO departments VALUES (4, 'Legal')");   // intentionally no employees

        db.Execute("CREATE TABLE employees (emp_id INTEGER PRIMARY KEY, name TEXT NOT NULL, dept_id INTEGER, salary INTEGER)");
        db.Execute("INSERT INTO employees VALUES (1, 'Alice', 1, 90000)");
        db.Execute("INSERT INTO employees VALUES (2, 'Bob',   1, 85000)");
        db.Execute("INSERT INTO employees VALUES (3, 'Carol', 2, 75000)");
        db.Execute("INSERT INTO employees VALUES (4, 'Dave',  3, 70000)");
        db.Execute("INSERT INTO employees VALUES (5, 'Eve',   1, 95000)");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // ─── Basic correctness ──────────────────────────────────────────────────

    [Fact]
    public void LeftJoin_UnmatchedLeftRow_AppearsWithNulls()
    {
        using var db = new Database(_dbPath, readOnly: true);
        var rows = db.Execute("""
            SELECT d.dept_id, d.dept_name, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            ORDER BY d.dept_id, e.emp_id
            """);

        // 3 Engineering + 1 Marketing + 1 HR + 1 Legal(null) = 6
        Assert.Equal(6, rows.Count);

        // Legal row must have NULLs for employee columns
        var legal = rows[5];
        Assert.Equal(4L,      legal[0]);   // dept_id
        Assert.Equal("Legal", legal[1]);   // dept_name
        Assert.Null(legal[2]);             // e.name
        Assert.Null(legal[3]);             // e.salary
    }

    [Fact]
    public void LeftJoin_vs_InnerJoin_RowCount()
    {
        using var db = new Database(_dbPath, readOnly: true);

        var inner = db.Execute("""
            SELECT d.dept_name, e.name
            FROM departments d
            INNER JOIN employees e ON d.dept_id = e.dept_id
            """);

        var left = db.Execute("""
            SELECT d.dept_name, e.name
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            """);

        Assert.Equal(5, inner.Count);   // only matched rows
        Assert.Equal(6, left.Count);    // +1 null row for Legal
    }

    [Fact]
    public void LeftJoin_WhereOnLeftColumn_FiltersAndPreservesNullRow()
    {
        using var db = new Database(_dbPath, readOnly: true);
        var rows = db.Execute("""
            SELECT d.dept_name, e.name
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            WHERE d.dept_id >= 3
            ORDER BY d.dept_id
            """);

        // dept 3 = HR/Dave, dept 4 = Legal/NULL
        Assert.Equal(2, rows.Count);
        Assert.Equal("HR",    rows[0][0]); Assert.Equal("Dave", rows[0][1]);
        Assert.Equal("Legal", rows[1][0]); Assert.Null(rows[1][1]);
    }

    [Fact]
    public void LeftJoin_WhereOnRightColumn_ExcludesNullRows()
    {
        using var db = new Database(_dbPath, readOnly: true);
        var rows = db.Execute("""
            SELECT d.dept_name, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            WHERE e.salary > 88000
            ORDER BY d.dept_name, e.name
            """);

        // NULL > 88000 is NULL = falsy, so Legal and low-salary rows excluded
        Assert.Equal(2, rows.Count);
        var names = rows.Select(r => (string)r[1]!).ToHashSet();
        Assert.Contains("Alice", names);
        Assert.Contains("Eve",   names);
        Assert.All(rows, r => Assert.True((long)r[2]! > 88000));
    }

    [Fact]
    public void LeftJoin_MultipleColumnsAndNullCheck()
    {
        using var db = new Database(_dbPath, readOnly: true);
        var rows = db.Execute("""
            SELECT d.dept_id, d.dept_name, e.emp_id, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            ORDER BY d.dept_id, e.emp_id
            """);

        Assert.Equal(6, rows.Count);

        // First Engineering employee: Alice
        Assert.Equal(1L,           rows[0][0]);
        Assert.Equal("Engineering",rows[0][1]);
        Assert.Equal(1L,           rows[0][2]);
        Assert.Equal("Alice",      rows[0][3]);
        Assert.Equal(90000L,       rows[0][4]);

        // Legal null-row (last)
        Assert.Equal(4L,      rows[5][0]);
        Assert.Equal("Legal", rows[5][1]);
        Assert.Null(rows[5][2]);
        Assert.Null(rows[5][3]);
        Assert.Null(rows[5][4]);
    }

    // ─── Cross-engine validation ─────────────────────────────────────────────

    /// <summary>
    /// Runs a query against both the C# engine and the official sqlite3 binary,
    /// then asserts row-by-row equality.
    /// </summary>
    private void AssertMatchesSqlite3(string sql)
    {
        if (_sqlite3Exe == null)
            throw new Exception("sqlite3.exe not found — cannot cross-validate.");

        // Our engine
        List<object?[]> ourRows;
        using (var db = new Database(_dbPath, readOnly: true))
            ourRows = db.Execute(sql);

        // Official sqlite3 (pipe-separated, NULL printed as empty)
        string rawOutput = RunSqlite3(_sqlite3Exe, _dbPath, sql);
        var officialRows = ParseSqlite3PipeOutput(rawOutput);

        Assert.Equal(officialRows.Count, ourRows.Count);

        for (int i = 0; i < officialRows.Count; i++)
        {
            var off = officialRows[i];
            var our = ourRows[i];
            Assert.Equal(off.Length, our.Length);
            for (int j = 0; j < off.Length; j++)
            {
                string offVal = off[j];        // sqlite3 gives strings
                string ourVal = our[j] == null ? "" : our[j]!.ToString()!;
                Assert.Equal(offVal, ourVal);
            }
        }
    }

    [Fact]
    public void CrossValidate_LeftJoin_AllRows()
    {
        AssertMatchesSqlite3("""
            SELECT d.dept_id, d.dept_name, e.emp_id, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            ORDER BY d.dept_id, e.emp_id
            """);
    }

    [Fact]
    public void CrossValidate_InnerJoin_AllRows()
    {
        AssertMatchesSqlite3("""
            SELECT d.dept_id, d.dept_name, e.emp_id, e.name, e.salary
            FROM departments d
            INNER JOIN employees e ON d.dept_id = e.dept_id
            ORDER BY d.dept_id, e.emp_id
            """);
    }

    [Fact]
    public void CrossValidate_LeftJoin_WhereOnLeftColumn()
    {
        AssertMatchesSqlite3("""
            SELECT d.dept_name, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            WHERE d.dept_id >= 3
            ORDER BY d.dept_id, e.emp_id
            """);
    }

    [Fact]
    public void CrossValidate_LeftJoin_WhereOnRightColumn()
    {
        AssertMatchesSqlite3("""
            SELECT d.dept_name, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id
            WHERE e.salary > 88000
            ORDER BY d.dept_name, e.name
            """);
    }

    [Fact]
    public void CrossValidate_LeftJoin_ComplexOnCondition()
    {
        // ON condition with extra inequality (salary filter applied at JOIN time, not WHERE)
        AssertMatchesSqlite3("""
            SELECT d.dept_name, e.name, e.salary
            FROM departments d
            LEFT JOIN employees e ON d.dept_id = e.dept_id AND e.salary >= 85000
            ORDER BY d.dept_id, e.emp_id
            """);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string RunSqlite3(string exe, string dbPath, string sql)
    {
        // Write SQL to a temp file to avoid shell-quoting issues with multiline queries
        string sqlFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(sqlFile, sql);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { dbPath },
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.WriteLine(sql);
            proc.StandardInput.Close();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        finally
        {
            try { File.Delete(sqlFile); } catch { }
        }
    }

    /// <summary>
    /// Parses sqlite3 default pipe-separated output into rows of string values.
    /// Empty field = NULL (sqlite3 prints nothing for NULL).
    /// </summary>
    private static List<string[]> ParseSqlite3PipeOutput(string output)
    {
        var result = new List<string[]>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrEmpty(trimmed))
                result.Add(trimmed.Split('|'));
        }
        return result;
    }
}
