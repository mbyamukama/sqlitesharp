# Session Context — SQLite Pure C# Port

## What This Project Is

A pure C# re-implementation of the SQLite database engine, living in `d:\code\open\sqlite\sqlite-cs\`.
The reference C source is in the parent directory `d:\code\open\sqlite\src\`.
The C-built `sqlite3.exe` (v3.54.0) is at `d:\code\open\sqlite\sqlite3.exe` — used for cross-engine validation in tests.

## Solution Layout

```
d:\code\open\sqlite\sqlite-cs\
├── sqlite-cs.sln                    (.NET 10, net10.0)
├── REPORT.html                      (progress report — open in browser)
├── SQLite.Engine\                   (class library — the database engine)
│   ├── IO\Pager.cs                  (page cache, transactions, journal, WAL integration)
│   ├── IO\VfsFile.cs                (FileStream wrapper)
│   ├── IO\Wal.cs                    (Write-Ahead Log: header, frames, checksum, checkpoint)
│   ├── BTree\BTree.cs               (open cursor by root page)
│   ├── BTree\BTreeCursor.cs         (read-only B-tree traversal)
│   ├── BTree\BTreeWriter.cs         (insert, delete, page split)
│   ├── BTree\Cell.cs                (varint codec, record build/parse, serial types)
│   ├── BTree\MemPage.cs             (parse B-tree page headers)
│   ├── Compiler\Token.cs            (TokenType enum + Token struct)
│   ├── Compiler\Tokenizer.cs        (SQL lexer)
│   ├── Compiler\Ast.cs              (all AST node types)
│   ├── Compiler\Parser.cs           (recursive-descent SQL parser)
│   ├── Compiler\AstPrinter.cs       (AST → readable text)
│   ├── Compiler\CodeGen.cs          (AST → VDBE bytecode for SELECT/INSERT/UPDATE/DELETE/CREATE/DROP)
│   ├── Func\MathFunctions.cs        (30 math scalar functions: trig, log, pow, etc.)
│   ├── Func\JsonFunctions.cs        (14 JSON scalars + 2 JSON aggregates)
│   ├── Vdbe\Opcodes.cs              (all opcode constants)
│   ├── Vdbe\VdbeOp.cs               (instruction struct)
│   ├── Vdbe\Mem.cs                  (tagged value: Null/Int64/Double/Text/Blob)
│   ├── Vdbe\Vdbe.cs                 (execution engine — Step() loop)
│   ├── Database.cs                  (connection: Prepare, Execute, schema management)
│   ├── DatabaseFactory.cs           (create new empty .db files from scratch)
│   ├── Statement.cs                 (prepared statement: Step/ColumnNames)
│   └── SqliteException.cs           (result codes + exception)
├── SQLite.Terminal\                  (console app — interactive SQL shell)
│   ├── Program.cs                   (entry point: REPL, --exec, --parse modes)
│   ├── Shell.cs                     (dot-commands, REPL loop)
│   └── OutputFormatter.cs           (table/column/csv/json/line output modes)
└── SQLite.Engine.Tests\             (xunit test project)
    ├── TokenizerTests.cs            (18 tests)
    ├── ParserTests.cs               (25 tests)
    ├── VdbeTests.cs                 (32 tests)
    ├── WriteTests.cs                (13 tests)
    ├── DdlTests.cs                  (14 tests)
    ├── ShellTests.cs                (15 tests)
    ├── WalTests.cs                  (11 tests)
    └── FunctionTests.cs             (51 tests)
```

## Completed Phases (8 of 8)

| Phase | What | Status |
|-------|------|--------|
| 1 | File format reader (pager, B-tree, varint, cell parsing) | ✅ |
| 2 | Tokenizer + recursive-descent parser → AST | ✅ |
| 3 | VDBE + CodeGen for SELECT (register-based VM) | ✅ |
| 4 | Write path: INSERT/UPDATE/DELETE, rollback journal, page split | ✅ |
| 5 | DDL: CREATE TABLE, DROP TABLE, DatabaseFactory.CreateNew() | ✅ |
| 6 | SQL Terminal: REPL shell with dot-commands + 5 output modes | ✅ |
| 7 | WAL mode: write-ahead log, frames, checkpoint | ✅ |
| 8 | Extensions: Math functions (30), JSON functions (16) | ✅ |

## Current State

- **179 tests passing** (`dotnet test` from `sqlite-cs\`)
- All phases build cleanly with `dotnet build`
- Shell: `dotnet run --project SQLite.Terminal -- mydb.db`
- Cross-engine validated: files written by C# are readable by `d:\code\open\sqlite\sqlite3.exe`

## Key Commands

```bash
cd d:\code\open\sqlite\sqlite-cs
dotnet build                           # build all
dotnet test                            # run all 128 tests
dotnet test --filter "WalTests"        # run specific test class
dotnet run --project SQLite.Terminal -- ../sample.db "SELECT * FROM users"
dotnet run --project SQLite.Terminal -- --parse "SELECT 1+2"
dotnet run --project SQLite.Terminal -- --exec ../sample.db "SELECT name FROM users"
```

## What's Next (Beyond Phase 8 — Future Work)

The original 8-phase plan is complete. Potential extensions:
- **FTS5 full-text search** — inverted index, tokenizer, MATCH syntax (large sub-project)
- **R-Tree spatial index** — virtual table for bounding-box queries (large sub-project)
- **Indexes** — CREATE INDEX, index-assisted WHERE scans, query planner
- **Subqueries** — IN (SELECT ...), scalar subqueries, EXISTS
- **JOIN** — INNER/LEFT/CROSS JOIN with nested-loop execution
- **Views** — CREATE VIEW, expandable in FROM clause
- **Triggers** — BEFORE/AFTER INSERT/UPDATE/DELETE
- **ALTER TABLE** — ADD COLUMN, RENAME
- **VACUUM** — rebuild database file to reclaim space
- **Concurrent readers** — WAL shared-memory index for true reader/writer concurrency

## Important Notes

- The shell is Git Bash (MINGW64) not CMD — use bash syntax for shell commands
- .NET 10 SDK (10.0.300) is installed
- The project follows the `AGENTS.md` guidance — this is exploration/porting work, not contributing back to SQLite
- `sample.db` at repo root has a `users` table with 3 rows (Alice/30, Bob/25, Carol/35)
