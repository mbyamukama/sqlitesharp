namespace SQLite.Engine.Compiler;

/// <summary>
/// All token types produced by the SQL tokenizer.
/// </summary>
public enum TokenType
{
    // Literals
    Integer,
    Float,
    String,
    Blob,
    Null,

    // Identifiers
    Identifier,

    // Keywords
    Select,
    From,
    Where,
    And,
    Or,
    Not,
    In,
    Is,
    Like,
    Between,
    As,
    On,
    Join,
    Left,
    Inner,
    Cross,
    Outer,
    Insert,
    Into,
    Values,
    Update,
    Set,
    Delete,
    Create,
    Table,
    Drop,
    If,
    Exists,
    Primary,
    Key,
    Autoincrement,
    Unique,
    Index,
    Order,
    By,
    Asc,
    Desc,
    Limit,
    Offset,
    Group,
    Having,
    Distinct,
    All,
    Union,
    Except,
    Intersect,
    Case,
    When,
    Then,
    Else,
    End,
    Cast,
    Collate,
    Default,
    Check,
    Foreign,
    References,
    Constraint,
    NotNull,     // "NOT NULL" as a combined token in column defs
    Glob,
    Escape,

    // Operators
    Plus,        // +
    Minus,       // -
    Star,        // *
    Slash,       // /
    Percent,     // %
    Concat,      // ||

    // Comparison
    Eq,          // =
    Neq,         // != or <>
    Lt,          // <
    Gt,          // >
    Lte,         // <=
    Gte,         // >=

    // Punctuation
    LeftParen,   // (
    RightParen,  // )
    Comma,       // ,
    Semicolon,   // ;
    Dot,         // .

    // Special
    Eof,
    Error,
}

/// <summary>
/// A single token from the SQL input.
/// </summary>
public readonly struct Token
{
    public TokenType Type { get; }
    public string Lexeme { get; }
    public int Line { get; }
    public int Column { get; }

    public Token(TokenType type, string lexeme, int line, int column)
    {
        Type = type;
        Lexeme = lexeme;
        Line = line;
        Column = column;
    }

    public override string ToString() => Type switch
    {
        TokenType.Integer or TokenType.Float or TokenType.String or TokenType.Identifier
            => $"{Type}({Lexeme})",
        _ => Type.ToString(),
    };
}
