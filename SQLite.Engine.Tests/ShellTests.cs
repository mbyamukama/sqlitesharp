using SQLite.Engine;
using SQLite.Terminal;

namespace SQLite.Engine.Tests;

/// <summary>
/// Tests for Phase 6 — SQL Terminal (Shell, OutputFormatter, dot-commands).
/// </summary>
public class ShellTests : IDisposable
{
    private readonly string _testDir;

    public ShellTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"sqlite_cs_shell_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void Shell_Open_CreatesNewDb()
    {
        string dbPath = Path.Combine(_testDir, "new.db");
        var shell = CreateShell();
        shell.Open(dbPath);
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public void Shell_ExecuteSql_CreateAndInsert()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE t (name TEXT, value INTEGER);");
        shell.ExecuteSql("INSERT INTO t VALUES ('hello', 42);");
        shell.ExecuteSql("SELECT name, value FROM t;");

        string result = output.ToString();
        Assert.Contains("hello", result);
        Assert.Contains("42", result);
    }

    [Fact]
    public void Shell_DotTables()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE alpha (x INTEGER);");
        shell.ExecuteSql("CREATE TABLE beta (y TEXT);");
        shell.ExecuteDotCommand(".tables");

        string result = output.ToString();
        Assert.Contains("alpha", result);
        Assert.Contains("beta", result);
    }

    [Fact]
    public void Shell_DotSchema()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        shell.ExecuteDotCommand(".schema");

        string result = output.ToString();
        Assert.Contains("CREATE TABLE users", result);
        Assert.Contains("NOT NULL", result);
    }

    [Fact]
    public void Shell_DotSchema_WithFilter()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE orders (id INTEGER);");
        shell.ExecuteSql("CREATE TABLE items (id INTEGER);");
        shell.ExecuteDotCommand(".schema items");

        string result = output.ToString();
        Assert.Contains("CREATE TABLE items", result);
        Assert.DoesNotContain("CREATE TABLE orders", result);
    }

    [Fact]
    public void OutputFormatter_TableMode()
    {
        var output = new StringWriter();
        string[] columns = ["name", "age"];
        var rows = new List<object?[]>
        {
            new object?[] { "Alice", 30L },
            new object?[] { "Bob", 25L },
        };

        OutputFormatter.PrintResults(columns, rows, OutputMode.Table, output);
        string result = output.ToString();

        Assert.Contains("┌", result);
        Assert.Contains("│", result);
        Assert.Contains("└", result);
        Assert.Contains("Alice", result);
        Assert.Contains("Bob", result);
        Assert.Contains("name", result);
        Assert.Contains("age", result);
    }

    [Fact]
    public void OutputFormatter_ColumnMode()
    {
        var output = new StringWriter();
        string[] columns = ["name", "age"];
        var rows = new List<object?[]>
        {
            new object?[] { "Alice", 30L },
        };

        OutputFormatter.PrintResults(columns, rows, OutputMode.Column, output);
        string result = output.ToString();

        Assert.Contains("name", result);
        Assert.Contains("age", result);
        Assert.Contains("----", result);
        Assert.Contains("Alice", result);
        Assert.Contains("30", result);
    }

    [Fact]
    public void OutputFormatter_CsvMode()
    {
        var output = new StringWriter();
        string[] columns = ["name", "value"];
        var rows = new List<object?[]>
        {
            new object?[] { "hello", 42L },
            new object?[] { "world", 99L },
        };

        OutputFormatter.PrintResults(columns, rows, OutputMode.Csv, output);
        string result = output.ToString();

        Assert.Contains("name,value", result);
        Assert.Contains("hello,42", result);
        Assert.Contains("world,99", result);
    }

    [Fact]
    public void OutputFormatter_JsonMode()
    {
        var output = new StringWriter();
        string[] columns = ["name", "count"];
        var rows = new List<object?[]>
        {
            new object?[] { "test", 5L },
        };

        OutputFormatter.PrintResults(columns, rows, OutputMode.Json, output);
        string result = output.ToString();

        Assert.Contains("[", result);
        Assert.Contains("\"name\": \"test\"", result);
        Assert.Contains("\"count\": 5", result);
        Assert.Contains("]", result);
    }

    [Fact]
    public void OutputFormatter_LineMode()
    {
        var output = new StringWriter();
        string[] columns = ["name", "age"];
        var rows = new List<object?[]>
        {
            new object?[] { "Alice", 30L },
        };

        OutputFormatter.PrintResults(columns, rows, OutputMode.Line, output);
        string result = output.ToString();

        Assert.Contains("name = Alice", result);
        Assert.Contains("age", result);
        Assert.Contains("30", result);
    }

    [Fact]
    public void OutputFormatter_NullValues()
    {
        var output = new StringWriter();
        string[] columns = ["col"];
        var rows = new List<object?[]> { new object?[] { null } };

        OutputFormatter.PrintResults(columns, rows, OutputMode.Column, output);
        Assert.Contains("NULL", output.ToString());
    }

    [Fact]
    public void Shell_DotMode_Changes()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE t (x INTEGER);");
        shell.ExecuteSql("INSERT INTO t VALUES (1);");

        shell.ExecuteDotCommand(".mode csv");
        shell.ExecuteSql("SELECT x FROM t;");

        string result = output.ToString();
        // CSV doesn't have box chars
        Assert.DoesNotContain("│", result);
        Assert.Contains("x", result);
        Assert.Contains("1", result);
    }

    [Fact]
    public void Shell_DotDump()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE t (name TEXT);");
        shell.ExecuteSql("INSERT INTO t VALUES ('hello');");
        shell.ExecuteDotCommand(".dump");

        string result = output.ToString();
        Assert.Contains("BEGIN TRANSACTION;", result);
        Assert.Contains("CREATE TABLE t", result);
        Assert.Contains("INSERT INTO t VALUES('hello');", result);
        Assert.Contains("COMMIT;", result);
    }

    [Fact]
    public void Shell_ErrorHandling()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("SELECT * FROM nonexistent;");
        string result = output.ToString();
        Assert.Contains("Error:", result);
    }

    [Fact]
    public void Shell_MultiStatement_Workflow()
    {
        var (shell, output) = CreateShellWithOutput();
        string dbPath = CreateEmptyDb();
        shell.Open(dbPath);

        shell.ExecuteSql("CREATE TABLE products (name TEXT, price REAL);");
        shell.ExecuteSql("INSERT INTO products VALUES ('Laptop', 999.99);");
        shell.ExecuteSql("INSERT INTO products VALUES ('Mouse', 29.50);");
        shell.ExecuteDotCommand(".mode table");
        shell.ExecuteSql("SELECT name, price FROM products;");

        string result = output.ToString();
        Assert.Contains("Laptop", result);
        Assert.Contains("Mouse", result);
        Assert.Contains("┌", result); // table mode
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string CreateEmptyDb()
    {
        string dbPath = Path.Combine(_testDir, $"test_{Guid.NewGuid():N}.db");
        DatabaseFactory.CreateNew(dbPath);
        return dbPath;
    }

    private Shell CreateShell() => new Shell(TextWriter.Null);

    private (Shell shell, StringWriter output) CreateShellWithOutput()
    {
        var output = new StringWriter();
        var shell = new Shell(output);
        return (shell, output);
    }
}
