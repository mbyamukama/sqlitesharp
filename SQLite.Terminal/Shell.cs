using SQLite.Engine;

namespace SQLite.Terminal;

/// <summary>
/// Interactive SQL shell with dot-commands.
/// Equivalent to the C sqlite3.exe command-line shell.
/// </summary>
public sealed class Shell
{
    private Database? _db;
    private string? _dbPath;
    private OutputMode _mode = OutputMode.Column;
    private TextWriter _output;
    private bool _headers = true;
    private bool _running = true;

    public Shell(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    /// <summary>
    /// Open a database. If path is null or ":memory:", no file is opened.
    /// </summary>
    public void Open(string? path)
    {
        _db?.Dispose();
        _db = null;
        _dbPath = path;

        if (string.IsNullOrEmpty(path) || path == ":memory:")
        {
            _dbPath = null;
            return;
        }

        if (!File.Exists(path))
        {
            // Create a new database
            DatabaseFactory.CreateNew(path);
        }

        _db = new Database(path);
    }

    /// <summary>
    /// Run the interactive REPL loop.
    /// </summary>
    public void RunInteractive()
    {
        PrintBanner();

        var buffer = new System.Text.StringBuilder();
        bool multiLine = false;

        while (_running)
        {
            string prompt = multiLine ? "   ...> " : "sqlite> ";
            Console.Write(prompt);
            string? line = Console.ReadLine();

            if (line == null)
            {
                // EOF (Ctrl+D / Ctrl+Z)
                _running = false;
                break;
            }

            // Dot-commands are single-line, start with '.'
            if (!multiLine && line.TrimStart().StartsWith('.'))
            {
                ExecuteDotCommand(line.Trim());
                continue;
            }

            buffer.Append(line);
            buffer.Append('\n');

            // Check if statement is complete (ends with semicolon)
            string sql = buffer.ToString().Trim();
            if (sql.EndsWith(';'))
            {
                ExecuteSql(sql);
                buffer.Clear();
                multiLine = false;
            }
            else if (sql.Length > 0)
            {
                multiLine = true;
            }
            else
            {
                buffer.Clear();
                multiLine = false;
            }
        }
    }

    /// <summary>
    /// Execute a single SQL statement (non-interactive).
    /// </summary>
    public void ExecuteSql(string sql)
    {
        if (_db == null)
        {
            _output.WriteLine("Error: no database is open. Use .open <filename>");
            return;
        }

        try
        {
            var stmt = _db.Prepare(sql);

            if (stmt.ColumnCount > 0)
            {
                // SELECT — collect and display rows
                var rows = new List<object?[]>();
                while (stmt.Step() == SqliteResult.Row)
                {
                    rows.Add(stmt.CurrentRow.ToArray());
                }

                if (rows.Count > 0 || _headers)
                {
                    var columnNames = stmt.ColumnNames.ToArray();
                    OutputFormatter.PrintResults(columnNames, rows, _mode, _output);
                }
            }
            else
            {
                // DML/DDL — just execute
                while (stmt.Step() == SqliteResult.Row) { }
            }
        }
        catch (SqliteException ex)
        {
            _output.WriteLine($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute a dot-command.
    /// </summary>
    public void ExecuteDotCommand(string line)
    {
        var parts = SplitCommand(line);
        if (parts.Length == 0) return;

        string cmd = parts[0].ToLowerInvariant();

        switch (cmd)
        {
            case ".quit" or ".exit" or ".q":
                _running = false;
                break;

            case ".open":
                if (parts.Length < 2)
                    _output.WriteLine("Usage: .open FILENAME");
                else
                    Open(parts[1]);
                break;

            case ".tables":
                if (_db == null) { _output.WriteLine("Error: no database open."); break; }
                foreach (var name in _db.GetTableNames())
                    _output.Write($"{name}  ");
                _output.WriteLine();
                break;

            case ".schema":
                if (_db == null) { _output.WriteLine("Error: no database open."); break; }
                string? filter = parts.Length > 1 ? parts[1] : null;
                foreach (var entry in _db.Schema)
                {
                    if (filter != null && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(entry.Sql))
                        _output.WriteLine($"{entry.Sql};");
                }
                break;

            case ".mode":
                if (parts.Length < 2)
                {
                    _output.WriteLine($"current mode: {_mode.ToString().ToLower()}");
                    _output.WriteLine("available: table, column, csv, json, line");
                }
                else
                {
                    _mode = parts[1].ToLowerInvariant() switch
                    {
                        "table" => OutputMode.Table,
                        "column" => OutputMode.Column,
                        "csv" => OutputMode.Csv,
                        "json" => OutputMode.Json,
                        "line" => OutputMode.Line,
                        _ => _mode,
                    };
                }
                break;

            case ".headers":
                if (parts.Length >= 2)
                    _headers = parts[1].ToLowerInvariant() is "on" or "yes" or "1" or "true";
                else
                    _output.WriteLine($"headers: {(_headers ? "on" : "off")}");
                break;

            case ".databases" or ".db":
                _output.WriteLine($"main: {_dbPath ?? "(none)"}");
                break;

            case ".help":
                PrintHelp();
                break;

            case ".dump":
                if (_db == null) { _output.WriteLine("Error: no database open."); break; }
                DumpDatabase();
                break;

            default:
                _output.WriteLine($"Unknown command: {cmd}. Try .help");
                break;
        }
    }

    private void DumpDatabase()
    {
        _output.WriteLine("BEGIN TRANSACTION;");
        foreach (var entry in _db!.Schema)
        {
            if (entry.Type == "table" && !string.IsNullOrEmpty(entry.Sql))
            {
                _output.WriteLine($"{entry.Sql};");

                // Dump data
                try
                {
                    var stmt = _db.Prepare($"SELECT * FROM {entry.Name}");
                    while (stmt.Step() == SqliteResult.Row)
                    {
                        var values = stmt.CurrentRow
                            .Select(v => v switch
                            {
                                null => "NULL",
                                string s => $"'{s.Replace("'", "''")}'",
                                long l => l.ToString(),
                                double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                byte[] b => $"X'{Convert.ToHexString(b)}'",
                                _ => v.ToString() ?? "NULL"
                            });
                        _output.WriteLine($"INSERT INTO {entry.Name} VALUES({string.Join(",", values)});");
                    }
                }
                catch { /* skip tables we can't read */ }
            }
        }
        _output.WriteLine("COMMIT;");
    }

    private void PrintBanner()
    {
        _output.WriteLine("SQLite C# Shell (Phase 6)");
        _output.WriteLine("Enter \".help\" for usage hints.");
        if (_dbPath != null)
            _output.WriteLine($"Connected to: {_dbPath}");
        else
            _output.WriteLine("No database open. Use .open FILENAME or pass a path as argument.");
        _output.WriteLine();
    }

    private void PrintHelp()
    {
        _output.WriteLine(".open FILENAME     Open a database file (creates if not exists)");
        _output.WriteLine(".tables            List all tables");
        _output.WriteLine(".schema [TABLE]    Show CREATE statements");
        _output.WriteLine(".mode MODE         Set output mode: table, column, csv, json, line");
        _output.WriteLine(".headers on|off    Toggle column headers");
        _output.WriteLine(".databases         Show open database");
        _output.WriteLine(".dump              Dump database as SQL");
        _output.WriteLine(".quit              Exit the shell");
        _output.WriteLine(".help              Show this help");
    }

    private static string[] SplitCommand(string line)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            if (line[i] == '"' || line[i] == '\'')
            {
                char quote = line[i];
                i++;
                int start = i;
                while (i < line.Length && line[i] != quote) i++;
                parts.Add(line[start..i]);
                if (i < line.Length) i++;
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                parts.Add(line[start..i]);
            }
        }
        return parts.ToArray();
    }
}
