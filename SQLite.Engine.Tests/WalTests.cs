using SQLite.Engine;
using SQLite.Engine.IO;

namespace SQLite.Engine.Tests;

/// <summary>
/// Tests for Phase 7 — WAL (Write-Ahead Log) mode.
/// </summary>
public class WalTests : IDisposable
{
    private readonly string _testDir;

    public WalTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"sqlite_cs_wal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void EnableWalMode_CreatesWalFile()
    {
        string dbPath = CreateTestDb();

        using var db = new Database(dbPath);
        db.EnableWalMode();

        Assert.True(db.WalMode);
        Assert.True(File.Exists(dbPath + "-wal"));
    }

    [Fact]
    public void WalMode_InsertAndSelect()
    {
        string dbPath = CreateTestDb();

        using var db = new Database(dbPath);
        db.EnableWalMode();

        db.Execute("INSERT INTO t VALUES ('hello', 42)");
        var rows = db.Execute("SELECT name, value FROM t");

        Assert.Single(rows);
        Assert.Equal("hello", rows[0][0]);
        Assert.Equal(42L, rows[0][1]);
    }

    [Fact]
    public void WalMode_MultipleInserts()
    {
        string dbPath = CreateTestDb();

        using var db = new Database(dbPath);
        db.EnableWalMode();

        db.Execute("INSERT INTO t VALUES ('a', 1)");
        db.Execute("INSERT INTO t VALUES ('b', 2)");
        db.Execute("INSERT INTO t VALUES ('c', 3)");

        var rows = db.Execute("SELECT name, value FROM t");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void WalMode_PersistsAcrossReopen()
    {
        string dbPath = CreateTestDb();

        using (var db = new Database(dbPath))
        {
            db.EnableWalMode();
            db.Execute("INSERT INTO t VALUES ('persist', 99)");
        }

        // WAL file should still exist
        Assert.True(File.Exists(dbPath + "-wal"));

        // Reopen — should automatically detect WAL mode
        using (var db = new Database(dbPath))
        {
            Assert.True(db.WalMode);
            var rows = db.Execute("SELECT name, value FROM t");
            Assert.Single(rows);
            Assert.Equal("persist", rows[0][0]);
        }
    }

    [Fact]
    public void WalMode_DisableCheckpoints()
    {
        string dbPath = CreateTestDb();

        using var db = new Database(dbPath);
        db.EnableWalMode();

        db.Execute("INSERT INTO t VALUES ('ckpt', 7)");
        Assert.True(db.WalMode);

        // Disable WAL mode — should checkpoint and remove WAL file
        db.DisableWalMode();
        Assert.False(db.WalMode);
        Assert.False(File.Exists(dbPath + "-wal"));

        // Data should still be readable (now from main db file)
        var rows = db.Execute("SELECT name, value FROM t");
        Assert.Single(rows);
        Assert.Equal("ckpt", rows[0][0]);
    }

    [Fact]
    public void WalMode_UpdateAndDelete()
    {
        string dbPath = CreateTestDb();

        using var db = new Database(dbPath);
        db.EnableWalMode();

        db.Execute("INSERT INTO t VALUES ('x', 10)");
        db.Execute("INSERT INTO t VALUES ('y', 20)");
        db.Execute("UPDATE t SET value = 15 WHERE name = 'x'");
        db.Execute("DELETE FROM t WHERE name = 'y'");

        var rows = db.Execute("SELECT name, value FROM t");
        Assert.Single(rows);
        Assert.Equal("x", rows[0][0]);
        Assert.Equal(15L, rows[0][1]);
    }

    [Fact]
    public void WalMode_NoJournalFileCreated()
    {
        string dbPath = CreateTestDb();
        string journalPath = dbPath + "-journal";

        using var db = new Database(dbPath);
        db.EnableWalMode();
        db.Execute("INSERT INTO t VALUES ('no-journal', 1)");

        // In WAL mode, no rollback journal should be created
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void WalMode_CreateTableInWal()
    {
        string dbPath = CreateEmptyDb();

        using var db = new Database(dbPath);
        db.EnableWalMode();

        db.Execute("CREATE TABLE items (id INTEGER PRIMARY KEY, title TEXT)");
        db.Execute("INSERT INTO items (title) VALUES ('test item')");

        var rows = db.Execute("SELECT title FROM items");
        Assert.Single(rows);
        Assert.Equal("test item", rows[0][0]);
    }

    [Fact]
    public void WalMode_CheckpointPreservesData()
    {
        string dbPath = CreateTestDb();

        using (var db = new Database(dbPath))
        {
            db.EnableWalMode();
            db.Execute("INSERT INTO t VALUES ('before', 1)");
            db.Execute("INSERT INTO t VALUES ('after', 2)");

            // Checkpoint by disabling WAL
            db.DisableWalMode();
        }

        // Reopen in journal mode, data should be in main file
        using (var db = new Database(dbPath, readOnly: true))
        {
            Assert.False(db.WalMode);
            var rows = db.Execute("SELECT name, value FROM t");
            Assert.Equal(2, rows.Count);
        }
    }

    [Fact]
    public void Wal_DirectApi_WriteAndRead()
    {
        string dbPath = Path.Combine(_testDir, "direct.db");
        DatabaseFactory.CreateNew(dbPath);

        // Test the WAL class directly
        using var wal = new Wal(dbPath, 4096);
        wal.Open();

        // Write a frame
        byte[] pageData = new byte[4096];
        pageData[0] = 0x42; // marker
        wal.WriteFrame(1, pageData, isCommit: true, dbSizePages: 1);

        Assert.Equal(1, wal.FrameCount);

        // Read it back
        byte[]? read = wal.ReadPage(1);
        Assert.NotNull(read);
        Assert.Equal(0x42, read[0]);

        // Non-existent page returns null
        Assert.Null(wal.ReadPage(999));
    }

    [Fact]
    public void Wal_Checkpoint_TransfersToDb()
    {
        string dbPath = Path.Combine(_testDir, "ckpt.db");
        DatabaseFactory.CreateNew(dbPath);

        byte[] pageData = new byte[4096];
        pageData[100] = 0xAB; // marker at offset 100

        using (var wal = new Wal(dbPath, 4096))
        {
            wal.Open();
            wal.WriteFrame(1, pageData, isCommit: true, dbSizePages: 1);

            using var file = new VfsFile(dbPath);
            wal.Checkpoint(file);
        }

        // Read the database file directly and verify the checkpoint wrote the data
        byte[] raw = File.ReadAllBytes(dbPath);
        Assert.Equal(0xAB, raw[100]);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string CreateTestDb()
    {
        string dbPath = Path.Combine(_testDir, $"test_{Guid.NewGuid():N}.db");
        DatabaseFactory.CreateNew(dbPath);
        using var db = new Database(dbPath);
        db.Execute("CREATE TABLE t (name TEXT, value INTEGER)");
        return dbPath;
    }

    private string CreateEmptyDb()
    {
        string dbPath = Path.Combine(_testDir, $"test_{Guid.NewGuid():N}.db");
        DatabaseFactory.CreateNew(dbPath);
        return dbPath;
    }
}
