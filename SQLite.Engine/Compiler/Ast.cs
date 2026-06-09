namespace SQLite.Engine.Compiler;

// ─── Base types ─────────────────────────────────────────────────────────────

/// <summary>Base class for all SQL statements.</summary>
public abstract class Stmt { }

/// <summary>Base class for all SQL expressions.</summary>
public abstract class Expr { }

// ─── Expressions ────────────────────────────────────────────────────────────

/// <summary>A literal value: integer, float, string, blob, or NULL.</summary>
public sealed class LiteralExpr : Expr
{
    public object? Value { get; }
    public TokenType LiteralType { get; }

    public LiteralExpr(object? value, TokenType literalType)
    {
        Value = value;
        LiteralType = literalType;
    }
}

/// <summary>A column reference, optionally qualified by table name: [table.]column</summary>
public sealed class ColumnRefExpr : Expr
{
    public string? TableName { get; }
    public string ColumnName { get; }

    public ColumnRefExpr(string columnName, string? tableName = null)
    {
        ColumnName = columnName;
        TableName = tableName;
    }
}

/// <summary>The * wildcard in SELECT, optionally table-qualified: table.*</summary>
public sealed class StarExpr : Expr
{
    public string? TableName { get; }

    public StarExpr(string? tableName = null)
    {
        TableName = tableName;
    }
}

/// <summary>A binary operation: left op right</summary>
public sealed class BinaryExpr : Expr
{
    public Expr Left { get; }
    public TokenType Operator { get; }
    public Expr Right { get; }

    public BinaryExpr(Expr left, TokenType op, Expr right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }
}

/// <summary>A unary operation: op expr (e.g. -x, NOT x)</summary>
public sealed class UnaryExpr : Expr
{
    public TokenType Operator { get; }
    public Expr Operand { get; }

    public UnaryExpr(TokenType op, Expr operand)
    {
        Operator = op;
        Operand = operand;
    }
}

/// <summary>A function call: name(args...)</summary>
public sealed class FunctionCallExpr : Expr
{
    public string FunctionName { get; }
    public Expr[] Arguments { get; }
    public bool IsDistinct { get; }

    public FunctionCallExpr(string name, Expr[] args, bool isDistinct = false)
    {
        FunctionName = name;
        Arguments = args;
        IsDistinct = isDistinct;
    }
}

/// <summary>expr IS NULL / expr IS NOT NULL</summary>
public sealed class IsNullExpr : Expr
{
    public Expr Operand { get; }
    public bool IsNot { get; }

    public IsNullExpr(Expr operand, bool isNot)
    {
        Operand = operand;
        IsNot = isNot;
    }
}

/// <summary>expr BETWEEN low AND high</summary>
public sealed class BetweenExpr : Expr
{
    public Expr Operand { get; }
    public Expr Low { get; }
    public Expr High { get; }
    public bool IsNot { get; }

    public BetweenExpr(Expr operand, Expr low, Expr high, bool isNot = false)
    {
        Operand = operand;
        Low = low;
        High = high;
        IsNot = isNot;
    }
}

/// <summary>expr IN (values...)</summary>
public sealed class InExpr : Expr
{
    public Expr Operand { get; }
    public Expr[] Values { get; }
    public bool IsNot { get; }

    public InExpr(Expr operand, Expr[] values, bool isNot = false)
    {
        Operand = operand;
        Values = values;
        IsNot = isNot;
    }
}

/// <summary>expr LIKE pattern</summary>
public sealed class LikeExpr : Expr
{
    public Expr Operand { get; }
    public Expr Pattern { get; }
    public bool IsNot { get; }

    public LikeExpr(Expr operand, Expr pattern, bool isNot = false)
    {
        Operand = operand;
        Pattern = pattern;
        IsNot = isNot;
    }
}

/// <summary>CAST(expr AS type)</summary>
public sealed class CastExpr : Expr
{
    public Expr Operand { get; }
    public string TypeName { get; }

    public CastExpr(Expr operand, string typeName)
    {
        Operand = operand;
        TypeName = typeName;
    }
}

/// <summary>A parenthesized expression (for clarity in the AST).</summary>
public sealed class ParenExpr : Expr
{
    public Expr Inner { get; }

    public ParenExpr(Expr inner)
    {
        Inner = inner;
    }
}

// ─── SELECT ─────────────────────────────────────────────────────────────────

/// <summary>A single item in the SELECT result list, with optional alias.</summary>
public sealed class ResultColumn
{
    public Expr Expression { get; }
    public string? Alias { get; }

    public ResultColumn(Expr expression, string? alias = null)
    {
        Expression = expression;
        Alias = alias;
    }
}

/// <summary>ORDER BY clause entry.</summary>
public sealed class OrderByItem
{
    public Expr Expression { get; }
    public bool Descending { get; }

    public OrderByItem(Expr expression, bool descending = false)
    {
        Expression = expression;
        Descending = descending;
    }
}

/// <summary>A table reference in FROM (simple name for now).</summary>
public sealed class TableRef
{
    public string TableName { get; }
    public string? Alias { get; }

    public TableRef(string tableName, string? alias = null)
    {
        TableName = tableName;
        Alias = alias;
    }
}

/// <summary>A JOIN clause: [INNER|LEFT|CROSS] JOIN table ON condition</summary>
public sealed class JoinClause
{
    public enum JoinType { Inner, Left, Cross }

    public JoinType Type { get; }
    public TableRef Table { get; }
    public Expr? On { get; }

    public JoinClause(JoinType type, TableRef table, Expr? on)
    {
        Type = type;
        Table = table;
        On = on;
    }
}

/// <summary>SELECT statement.</summary>
public sealed class SelectStmt : Stmt
{
    public bool Distinct { get; init; }
    public ResultColumn[] Columns { get; init; } = [];
    public TableRef? From { get; init; }
    public JoinClause[]? Joins { get; init; }
    public Expr? Where { get; init; }
    public Expr[]? GroupBy { get; init; }
    public Expr? Having { get; init; }
    public OrderByItem[]? OrderBy { get; init; }
    public Expr? Limit { get; init; }
    public Expr? Offset { get; init; }
}

// ─── INSERT ─────────────────────────────────────────────────────────────────

/// <summary>INSERT INTO table (columns) VALUES (values), ...</summary>
public sealed class InsertStmt : Stmt
{
    public string TableName { get; init; } = "";
    public string[]? ColumnNames { get; init; }
    public Expr[][] ValueRows { get; init; } = [];
}

// ─── UPDATE ─────────────────────────────────────────────────────────────────

/// <summary>A single SET assignment: column = expr</summary>
public sealed class SetClause
{
    public string ColumnName { get; }
    public Expr Value { get; }

    public SetClause(string columnName, Expr value)
    {
        ColumnName = columnName;
        Value = value;
    }
}

/// <summary>UPDATE table SET ... WHERE ...</summary>
public sealed class UpdateStmt : Stmt
{
    public string TableName { get; init; } = "";
    public SetClause[] SetClauses { get; init; } = [];
    public Expr? Where { get; init; }
}

// ─── DELETE ─────────────────────────────────────────────────────────────────

/// <summary>DELETE FROM table WHERE ...</summary>
public sealed class DeleteStmt : Stmt
{
    public string TableName { get; init; } = "";
    public Expr? Where { get; init; }
}

// ─── CREATE TABLE ───────────────────────────────────────────────────────────

/// <summary>A column definition in a CREATE TABLE statement.</summary>
public sealed class ColumnDef
{
    public string Name { get; init; } = "";
    public string? TypeName { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsAutoincrement { get; init; }
    public bool IsNotNull { get; init; }
    public bool IsUnique { get; init; }
    public Expr? DefaultValue { get; init; }
}

/// <summary>CREATE TABLE statement.</summary>
public sealed class CreateTableStmt : Stmt
{
    public string TableName { get; init; } = "";
    public bool IfNotExists { get; init; }
    public ColumnDef[] Columns { get; init; } = [];
}

// ─── DROP TABLE ─────────────────────────────────────────────────────────────

/// <summary>DROP TABLE [IF EXISTS] name</summary>
public sealed class DropTableStmt : Stmt
{
    public string TableName { get; init; } = "";
    public bool IfExists { get; init; }
}
