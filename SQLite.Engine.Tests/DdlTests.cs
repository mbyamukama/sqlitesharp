using SQLite.Engine;

namespace SQLite.Engine.Tests;

/// <summary>
/// Tests for Phase 5 — DDL (CREATE TABLE, DROP TABLE).
/// </summary>
public class DdlTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sqlite3Exe;

    public DdlTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"sqlite_cs_ddl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _sqlite3Exe = @"d:\code\open\sqlite\sqlite3.exe";
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateNewDatabase_FromScratch()
    {
        string dbPath = Path.Combine(_testDir, "new.db");
        DatabaseFactory.CreateNew(dbPath);

        Assert.True(File.Exists(dbPath));

        using var db = new Database(dbPath, readOnly: true);
        Assert.Equal(4096, db.Header.PageSize);
        Assert.Equal(1, db.Header.PageCount);
        Assert.Empty(db.GetTableNames());
    }

    [Fact]
    public void CreateTable_Basic()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");

        var tables = db.GetTableNames().ToList();
        Assert.Single(tables);
        Assert.Equal("users", tables[0]);

        // Verify schema entry has correct root page and SQL
        var entry = db.Schema.First(s => s.Name == "users");
        Assert.Equal("table", entry.Type);
        Assert.True(entry.RootPage >= 2, $"Expected rootPage >= 2, got {entry.RootPage}");
        Assert.Contains("CREATE TABLE", entry.Sql);
    }

    [Fact]
    public void CreateTable_ThenInsertAndSelect()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE items (name TEXT, price INTEGER)");
        db.Execute("INSERT INTO items VALUES ('Widget', 10)");
        db.Execute("INSERT INTO items VALUES ('Gadget', 25)");

        var rows = db.Execute("SELECT name, price FROM items");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Widget", rows[0][0]);
        Assert.Equal(10L, rows[0][1]);
        Assert.Equal("Gadget", rows[1][0]);
        Assert.Equal(25L, rows[1][1]);
    }

    [Fact]
    public void CreateTable_PersistsAcrossReopen()
    {
        string dbPath = CreateEmptyDb();

        using (var db = new Database(dbPath))
        {
            db.Execute("CREATE TABLE t (x INTEGER)");
            db.Execute("INSERT INTO t VALUES (42)");
        }

        using (var db = new Database(dbPath, readOnly: true))
        {
            var tables = db.GetTableNames().ToList();
            Assert.Contains("t", tables);

            var rows = db.Execute("SELECT x FROM t");
            Assert.Single(rows);
            Assert.Equal(42L, rows[0][0]);
        }
    }

    [Fact]
    public void CreateTable_MultipleTables()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE a (x INTEGER)");
        db.Execute("CREATE TABLE b (y TEXT)");
        db.Execute("CREATE TABLE c (z REAL)");

        var tables = db.GetTableNames().ToList();
        Assert.Equal(3, tables.Count);
        Assert.Contains("a", tables);
        Assert.Contains("b", tables);
        Assert.Contains("c", tables);
    }

    [Fact]
    public void CreateTable_IfNotExists_NoError()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE t (x INTEGER)");
        // Should not throw
        db.Execute("CREATE TABLE IF NOT EXISTS t (x INTEGER)");

        var tables = db.GetTableNames().ToList();
        Assert.Single(tables);
    }

    [Fact]
    public void CreateTable_AlreadyExists_Throws()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE t (x INTEGER)");
        Assert.Throws<SqliteException>(() => db.Execute("CREATE TABLE t (x INTEGER)"));
    }

    [Fact]
    public void DropTable_Basic()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE t (x INTEGER)");
        Assert.Single(db.GetTableNames().ToList());

        db.Execute("DROP TABLE t");
        Assert.Empty(db.GetTableNames().ToList());
    }

    [Fact]
    public void DropTable_IfExists_NoError()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        // Should not throw
        db.Execute("DROP TABLE IF EXISTS nonexistent");
    }

    [Fact]
    public void DropTable_NotExists_Throws()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        Assert.Throws<SqliteException>(() => db.Execute("DROP TABLE nonexistent"));
    }

    [Fact]
    public void CrossEngine_CSharpCreates_CReads()
    {
        string dbPath = CreateEmptyDb();

        using (var db = new Database(dbPath))
        {
            db.Execute("CREATE TABLE demo (id INTEGER PRIMARY KEY, msg TEXT)");
            db.Execute("INSERT INTO demo (msg) VALUES ('hello from csharp')");
            db.Execute("INSERT INTO demo (msg) VALUES ('second row')");
        }

        // Verify with C sqlite3.exe
        string output = RunSqlite3(dbPath, "SELECT msg FROM demo;");
        Assert.Contains("hello from csharp", output);
        Assert.Contains("second row", output);
    }

    [Fact]
    public void CrossEngine_CSharpCreates_CReadsSchema()
    {
        string dbPath = CreateEmptyDb();

        using (var db = new Database(dbPath))
        {
            db.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER)");
        }

        string output = RunSqlite3(dbPath, ".schema");
        Assert.Contains("CREATE TABLE users", output);
        Assert.Contains("name TEXT NOT NULL", output);
    }

    [Fact]
    public void FullWorkflow_CreateInsertSelectDrop()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);

        // Create
        db.Execute("CREATE TABLE orders (product TEXT, qty INTEGER, price REAL)");

        // Insert
        db.Execute("INSERT INTO orders VALUES ('Laptop', 2, 999.99)");
        db.Execute("INSERT INTO orders VALUES ('Mouse', 5, 29.99)");

        // Select
        var rows = db.Execute("SELECT product, qty FROM orders WHERE qty > 3");
        Assert.Single(rows);
        Assert.Equal("Mouse", rows[0][0]);
        Assert.Equal(5L, rows[0][1]);

        // Drop
        db.Execute("DROP TABLE orders");
        Assert.Empty(db.GetTableNames().ToList());
    }

    [Fact]
    public void CreateTable_WithNotNull_StoredInSchema()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");

        var entry = db.Schema.First(s => s.Type == "table" && s.Name == "t");
        Assert.Contains("NOT NULL", entry.Sql);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string CreateEmptyDb()
    {
        string dbPath = Path.Combine(_testDir, $"test_{Guid.NewGuid():N}.db");
        DatabaseFactory.CreateNew(dbPath);
        return dbPath;
    }

    private string RunSqlite3(string dbPath, string sql)
    {
        if (!File.Exists(_sqlite3Exe))
            throw new FileNotFoundException($"sqlite3.exe not found at {_sqlite3Exe}");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _sqlite3Exe,
            Arguments = $"\"{dbPath}\" \"{sql}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return output;
    }
}
