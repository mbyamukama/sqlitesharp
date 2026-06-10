# SQLite# — A Pure C# SQLite Engine

A from-scratch implementation of the SQLite database engine in pure managed C#. No native interop, no P/Invoke, no wrappers — just C# reading and writing the SQLite file format directly.

Files created by this engine are binary-compatible with the official SQLite (validated against `sqlite3.exe` v3.54.0).

## Features

- **Full read/write** — SELECT, INSERT, UPDATE, DELETE
- **DDL** — CREATE TABLE, DROP TABLE, DatabaseFactory for new databases
- **B-Tree storage** — page cache, cell parsing, page splitting, varint codec
- **VDBE** — register-based virtual machine compiling SQL → bytecode → results
- **Transactions** — rollback journal with atomic commit/rollback
- **WAL mode** — write-ahead logging with checkpoint support
- **JOIN** — INNER JOIN with nested-loop execution
- **46 built-in functions:**
  - Scalar: abs, length, upper, lower, typeof
  - Math (30): sin, cos, tan, asin, acos, atan, atan2, sinh, cosh, tanh, exp, ln, log, log2, log10, sqrt, pow, ceil, floor, trunc, sign, degrees, radians, pi, mod, and more
  - JSON (16): json, json_array, json_extract, json_insert, json_replace, json_set, json_remove, json_object, json_type, json_valid, json_quote, json_patch, json_array_length, json_group_array, json_group_object
  - Aggregates: count, sum, total, min, max, avg, json_group_array, json_group_object
- **SQL Terminal** — interactive REPL with dot-commands and 5 output modes (table, column, csv, json, line)

## Requirements

- .NET 10 SDK

## Quick Start

```bash
# Build
dotnet build

# Run tests
dotnet test

# Launch the SQL shell
dotnet run --project SQLite.Terminal -- mydb.db

# Execute a query directly
dotnet run --project SQLite.Terminal -- --exec mydb.db "SELECT * FROM users"

# Parse SQL and show AST
dotnet run --project SQLite.Terminal -- --parse "SELECT name, age FROM users WHERE age > 25"
```

## Project Structure

```
├── SQLite.Engine/              Core database engine (class library)
│   ├── IO/
│   │   ├── Pager.cs            Page cache, transactions, journal
│   │   ├── VfsFile.cs          FileStream wrapper with locking
│   │   └── Wal.cs              Write-ahead log
│   ├── BTree/
│   │   ├── BTree.cs            Open cursor by root page
│   │   ├── BTreeCursor.cs      Read-only B-tree traversal
│   │   ├── BTreeWriter.cs      Insert, delete, page split
│   │   ├── Cell.cs             Varint codec, record build/parse
│   │   └── MemPage.cs          Page header parsing
│   ├── Compiler/
│   │   ├── Token.cs            Token types
│   │   ├── Tokenizer.cs        SQL lexer
│   │   ├── Ast.cs              AST node types
│   │   ├── Parser.cs           Recursive-descent parser
│   │   ├── AstPrinter.cs       AST → readable text
│   │   └── CodeGen.cs          AST → VDBE bytecode
│   ├── Vdbe/
│   │   ├── Opcodes.cs          Opcode constants
│   │   ├── VdbeOp.cs           Instruction struct
│   │   ├── Mem.cs              Tagged value (Null/Int64/Double/Text/Blob)
│   │   └── Vdbe.cs             Execution engine
│   ├── Func/
│   │   ├── MathFunctions.cs    30 math scalar functions
│   │   └── JsonFunctions.cs    14 JSON scalars + 2 JSON aggregates
│   ├── Database.cs             Connection, Prepare, Execute
│   ├── DatabaseFactory.cs      Create new .db files from scratch
│   ├── Statement.cs            Prepared statement
│   └── SqliteException.cs      Error codes
├── SQLite.Terminal/            Interactive SQL shell
│   ├── Program.cs              Entry point (REPL, --exec, --parse)
│   ├── Shell.cs                Dot-commands, REPL loop
│   └── OutputFormatter.cs      Table/column/csv/json/line modes
└── SQLite.Engine.Tests/        xUnit tests (179 tests)
```

## Architecture

```
SQL text
    │
    ▼  Tokenizer
Token[]
    │
    ▼  Parser (recursive-descent)
AST
    │
    ▼  CodeGen
VdbeOp[] bytecode
    │
    ▼  VDBE execution loop
Result rows  ←── BTreeCursor ←── Pager ←── FileStream
```

## SQL Support

| Category | Supported |
|----------|-----------|
| SELECT | columns, *, expressions, aliases, WHERE, ORDER BY, LIMIT, DISTINCT |
| JOIN | INNER JOIN ... ON |
| Aggregates | COUNT, SUM, AVG, MIN, MAX, TOTAL with GROUP BY / HAVING |
| INSERT | single and multi-row VALUES |
| UPDATE | SET with WHERE filter |
| DELETE | with WHERE filter |
| DDL | CREATE TABLE, DROP TABLE, IF NOT EXISTS / IF EXISTS |
| Expressions | arithmetic, comparisons, AND/OR/NOT, IS NULL, BETWEEN, IN, LIKE, CAST, functions |

## Shell Dot-Commands

```
.open FILENAME     Open a database file (creates if not exists)
.tables            List all tables
.schema [TABLE]    Show CREATE statements
.mode MODE         Set output: table, column, csv, json, line
.headers on|off    Toggle column headers
.databases         Show attached databases
.dump              Full SQL backup
.quit              Exit
```

## License

This is an educational/exploration project — a clean-room reimplementation for learning purposes.
