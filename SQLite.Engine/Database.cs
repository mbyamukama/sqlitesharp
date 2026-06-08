using System.Text.RegularExpressions;
using SQLite.Engine.BTree;
using SQLite.Engine.Compiler;
using SQLite.Engine.IO;
using SQLite.Engine.Vdbe;

namespace SQLite.Engine;

/// <summary>
/// A row from the sqlite_schema table.
/// </summary>
public sealed class SchemaEntry
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string TableName { get; init; } = "";
    public int RootPage { get; init; }
    public string Sql { get; init; } = "";
}

/// <summary>
/// Top-level database connection. Opens a .db file, reads the schema,
/// and provides access to tables via B-tree cursors.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly Pager _pager;
    private readonly List<SchemaEntry> _schema = new();
    private bool _disposed;

    public DatabaseHeader Header => _pager.Header;
    public IReadOnlyList<SchemaEntry> Schema => _schema;

    public Database(string dbPath, bool readOnly = false)
    {
        _pager = new Pager(dbPath, readOnly);
        LoadSchema();
    }

    /// <summary>
    /// Get table names in the database.
    /// </summary>
    public IEnumerable<string> GetTableNames()
    {
        return _schema
            .Where(e => e.Type == "table")
            .Select(e => e.Name);
    }

    /// <summary>
    /// Get a B-tree cursor for the named table.
    /// </summary>
    public BTreeCursor OpenTable(string tableName)
    {
        var entry = _schema.FirstOrDefault(e => e.Type == "table" &&
            string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            throw new SqliteException(SqliteResult.Error, $"Table '{tableName}' not found.");
        }

        var btree = new BTree.BTree(_pager, entry.RootPage);
        return btree.OpenCursor();
    }

    /// <summary>
    /// Read all rows from a table as arrays of values.
    /// </summary>
    public IEnumerable<BTreeRecord> ReadTable(string tableName)
    {
        var cursor = OpenTable(tableName);
        cursor.MoveToFirst();
        while (!cursor.Eof)
        {
            yield return cursor.GetRecord();
            cursor.Next();
        }
    }

    private void LoadSchema()
    {
        ReloadSchema();
    }

    private void ReloadSchema()
    {
        _schema.Clear();
        // sqlite_schema is always on page 1
        var btree = new BTree.BTree(_pager, 1);
        var cursor = btree.OpenCursor();
        cursor.MoveToFirst();

        while (!cursor.Eof)
        {
            var record = cursor.GetRecord();
            if (record.Values.Length >= 5)
            {
                _schema.Add(new SchemaEntry
                {
                    Type = record.Values[0]?.ToString() ?? "",
                    Name = record.Values[1]?.ToString() ?? "",
                    TableName = record.Values[2]?.ToString() ?? "",
                    RootPage = record.Values[3] is long rp ? (int)rp : 0,
                    Sql = record.Values[4]?.ToString() ?? "",
                });
            }
            cursor.Next();
        }
    }

    /// <summary>
    /// Prepare a SQL statement for execution. Returns a Statement that can be stepped.
    /// </summary>
    public Statement Prepare(string sql)
    {
        var stmt = Parser.Parse(sql);
        var schemaInfo = BuildSchemaInfo();
        var codeGen = new CodeGen(schemaInfo);
        var (program, regCount, cursorCount) = codeGen.Compile(stmt);
        var vm = new Vdbe.Vdbe(program, regCount, cursorCount, _pager);
        vm.OnSchemaChange = ReloadSchema;
        var columnNames = ResolveColumnNames(stmt);
        return new Statement(vm, columnNames);
    }

    /// <summary>
    /// Execute a SQL query and return all result rows.
    /// </summary>
    public List<object?[]> Execute(string sql)
    {
        var statement = Prepare(sql);
        var rows = new List<object?[]>();
        while (statement.Step() == SqliteResult.Row)
        {
            rows.Add(statement.CurrentRow.ToArray());
        }
        return rows;
    }

    private CodeGen.SchemaInfo BuildSchemaInfo()
    {
        var info = new CodeGen.SchemaInfo();
        foreach (var entry in _schema.Where(e => e.Type == "table"))
        {
            var columns = ParseColumnNames(entry.Sql);
            info.AddTable(new CodeGen.TableInfo
            {
                Name = entry.Name,
                RootPage = entry.RootPage,
                ColumnNames = columns,
            });
        }
        return info;
    }

    private static string[] ParseColumnNames(string createSql)
    {
        // Extract column names from CREATE TABLE sql
        // Quick approach: find content between first ( and last ), then split on commas
        if (string.IsNullOrEmpty(createSql))
            return [];

        int parenStart = createSql.IndexOf('(');
        int parenEnd = createSql.LastIndexOf(')');
        if (parenStart < 0 || parenEnd < 0 || parenEnd <= parenStart)
            return [];

        string body = createSql[(parenStart + 1)..parenEnd];

        // Split by commas that are not inside parentheses
        var columns = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') depth--;
            else if (body[i] == ',' && depth == 0)
            {
                columns.Add(body[start..i].Trim());
                start = i + 1;
            }
        }
        columns.Add(body[start..].Trim());

        // Extract first word of each column def (the column name)
        var names = new List<string>();
        foreach (var colDef in columns)
        {
            if (string.IsNullOrWhiteSpace(colDef)) continue;
            // Skip table constraints (PRIMARY, UNIQUE, CHECK, FOREIGN, CONSTRAINT)
            var trimmed = colDef.TrimStart();
            if (trimmed.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
                continue;

            // Column name is the first token (could be quoted)
            string name = ExtractFirstToken(trimmed);
            names.Add(name);
        }

        return names.ToArray();
    }

    private static string ExtractFirstToken(string s)
    {
        s = s.TrimStart();
        if (s.Length == 0) return "";

        // Quoted: "...", `...`, [...]
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 0 ? s[1..end] : s[1..];
        }
        if (s[0] == '`')
        {
            int end = s.IndexOf('`', 1);
            return end > 0 ? s[1..end] : s[1..];
        }
        if (s[0] == '[')
        {
            int end = s.IndexOf(']', 1);
            return end > 0 ? s[1..end] : s[1..];
        }

        // Unquoted: read until whitespace or punctuation
        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ',')
            i++;
        return s[..i];
    }

    private string[] ResolveColumnNames(Stmt stmt)
    {
        if (stmt is not SelectStmt select)
            return [];

        var names = new List<string>();
        foreach (var col in select.Columns)
        {
            if (col.Alias != null)
                names.Add(col.Alias);
            else if (col.Expression is ColumnRefExpr cref)
                names.Add(cref.ColumnName);
            else if (col.Expression is StarExpr star)
            {
                // Expand * to actual column names if we have a FROM table
                if (select.From != null)
                {
                    var entry = _schema.FirstOrDefault(e => e.Type == "table" &&
                        string.Equals(e.Name, select.From.TableName, StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        var colNames = ParseColumnNames(entry.Sql);
                        names.AddRange(colNames);
                        continue;
                    }
                }
                names.Add("*");
            }
            else if (col.Expression is FunctionCallExpr func)
                names.Add($"{func.FunctionName}(...)");
            else
                names.Add($"col{names.Count}");
        }
        return names.ToArray();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _pager.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Enable WAL journal mode. Writes go to the WAL file instead of a rollback journal.
    /// </summary>
    public void EnableWalMode() => _pager.EnableWalMode();

    /// <summary>
    /// Disable WAL mode — checkpoints the WAL and switches back to rollback journal mode.
    /// </summary>
    public void DisableWalMode()
    {
        _pager.DisableWalMode();
        ReloadSchema();
    }

    /// <summary>
    /// Whether the database is currently in WAL mode.
    /// </summary>
    public bool WalMode => _pager.WalMode;
}
