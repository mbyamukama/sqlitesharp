namespace SQLite.Engine;

/// <summary>
/// SQLite result codes mirroring the C SQLITE_* constants.
/// </summary>
public enum SqliteResult
{
    Ok = 0,
    Error = 1,
    Internal = 2,
    Perm = 3,
    Abort = 4,
    Busy = 5,
    Locked = 6,
    NoMem = 7,
    ReadOnly = 8,
    Interrupt = 9,
    IoErr = 10,
    Corrupt = 11,
    NotFound = 12,
    Full = 13,
    CantOpen = 14,
    Protocol = 15,
    Empty = 16,
    Schema = 17,
    TooBig = 18,
    Constraint = 19,
    Mismatch = 20,
    Misuse = 21,
    NoLfs = 22,
    Auth = 23,
    Format = 24,
    Range = 25,
    NotADb = 26,
    Notice = 27,
    Warning = 28,
    Row = 100,
    Done = 101,
}

/// <summary>
/// Exception type thrown by the SQLite engine.
/// </summary>
public class SqliteException : Exception
{
    public SqliteResult ResultCode { get; }

    public SqliteException(SqliteResult code, string message)
        : base(message)
    {
        ResultCode = code;
    }

    public SqliteException(SqliteResult code, string message, Exception innerException)
        : base(message, innerException)
    {
        ResultCode = code;
    }
}
