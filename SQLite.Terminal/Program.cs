using SQLite.Engine;
using SQLite.Engine.Compiler;
using SQLite.Terminal;

// ─── Special modes (backward-compatible with earlier phases) ────────────────

if (args.Length >= 2 && args[0] == "--parse")
{
    string sql = string.Join(" ", args[1..]);
    try
    {
        var stmt = Parser.Parse(sql);
        Console.WriteLine(AstPrinter.Print(stmt));
    }
    catch (SqliteException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
    return 0;
}

if (args.Length >= 3 && args[0] == "--exec")
{
    string execDb = args[1];
    string sql = string.Join(" ", args[2..]);
    if (!File.Exists(execDb))
    {
        DatabaseFactory.CreateNew(execDb);
    }
    var shell = new Shell();
    shell.Open(execDb);
    shell.ExecuteSql(sql);
    return 0;
}

// ─── Interactive shell ──────────────────────────────────────────────────────

var mainShell = new Shell();

if (args.Length >= 1)
{
    // First argument is the database path
    mainShell.Open(args[0]);

    // If additional arguments, treat as SQL to execute non-interactively
    if (args.Length >= 2)
    {
        string sql = string.Join(" ", args[1..]);
        mainShell.ExecuteSql(sql);
        return 0;
    }
}

// Enter interactive REPL
mainShell.RunInteractive();
return 0;
