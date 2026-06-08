using SQLite.Engine.Compiler;

namespace SQLite.Engine.Tests;

public class ParserTests
{
    [Fact]
    public void SelectStar()
    {
        var stmt = Parser.Parse("SELECT * FROM users") as SelectStmt;
        Assert.NotNull(stmt);
        Assert.Single(stmt.Columns);
        Assert.IsType<StarExpr>(stmt.Columns[0].Expression);
        Assert.Equal("users", stmt.From!.TableName);
    }

    [Fact]
    public void SelectWithWhereAndOrderBy()
    {
        var stmt = Parser.Parse("SELECT name, age FROM users WHERE age > 25 ORDER BY name LIMIT 10") as SelectStmt;
        Assert.NotNull(stmt);
        Assert.Equal(2, stmt.Columns.Length);
        Assert.IsType<ColumnRefExpr>(stmt.Columns[0].Expression);
        Assert.Equal("name", ((ColumnRefExpr)stmt.Columns[0].Expression).ColumnName);

        // WHERE
        Assert.IsType<BinaryExpr>(stmt.Where);
        var where = (BinaryExpr)stmt.Where!;
        Assert.Equal(TokenType.Gt, where.Operator);
        Assert.Equal("age", ((ColumnRefExpr)where.Left).ColumnName);
        Assert.Equal(25L, ((LiteralExpr)where.Right).Value);

        // ORDER BY
        Assert.NotNull(stmt.OrderBy);
        Assert.Single(stmt.OrderBy);
        Assert.Equal("name", ((ColumnRefExpr)stmt.OrderBy[0].Expression).ColumnName);
        Assert.False(stmt.OrderBy[0].Descending);

        // LIMIT
        Assert.NotNull(stmt.Limit);
        Assert.Equal(10L, ((LiteralExpr)stmt.Limit).Value);
    }

    [Fact]
    public void SelectDistinctWithAlias()
    {
        var stmt = Parser.Parse("SELECT DISTINCT name AS n FROM users") as SelectStmt;
        Assert.NotNull(stmt);
        Assert.True(stmt.Distinct);
        Assert.Equal("n", stmt.Columns[0].Alias);
    }

    [Fact]
    public void SelectWithFunctionCall()
    {
        var stmt = Parser.Parse("SELECT count(*) FROM users") as SelectStmt;
        Assert.NotNull(stmt);
        var func = Assert.IsType<FunctionCallExpr>(stmt.Columns[0].Expression);
        Assert.Equal("count", func.FunctionName);
        Assert.Single(func.Arguments);
        Assert.IsType<StarExpr>(func.Arguments[0]);
    }

    [Fact]
    public void SelectWithGroupByHaving()
    {
        var stmt = Parser.Parse("SELECT age, count(*) FROM users GROUP BY age HAVING count(*) > 1") as SelectStmt;
        Assert.NotNull(stmt);
        Assert.NotNull(stmt.GroupBy);
        Assert.Single(stmt.GroupBy);
        Assert.NotNull(stmt.Having);
    }

    [Fact]
    public void InsertStatement()
    {
        var stmt = Parser.Parse("INSERT INTO users (name, age) VALUES ('Dave', 40)") as InsertStmt;
        Assert.NotNull(stmt);
        Assert.Equal("users", stmt.TableName);
        Assert.Equal(new[] { "name", "age" }, stmt.ColumnNames);
        Assert.Single(stmt.ValueRows);
        Assert.Equal(2, stmt.ValueRows[0].Length);
        Assert.Equal("Dave", ((LiteralExpr)stmt.ValueRows[0][0]).Value);
        Assert.Equal(40L, ((LiteralExpr)stmt.ValueRows[0][1]).Value);
    }

    [Fact]
    public void InsertMultipleRows()
    {
        var stmt = Parser.Parse("INSERT INTO t VALUES (1, 'a'), (2, 'b')") as InsertStmt;
        Assert.NotNull(stmt);
        Assert.Null(stmt.ColumnNames);
        Assert.Equal(2, stmt.ValueRows.Length);
    }

    [Fact]
    public void UpdateStatement()
    {
        var stmt = Parser.Parse("UPDATE users SET age = 31 WHERE name = 'Alice'") as UpdateStmt;
        Assert.NotNull(stmt);
        Assert.Equal("users", stmt.TableName);
        Assert.Single(stmt.SetClauses);
        Assert.Equal("age", stmt.SetClauses[0].ColumnName);
        Assert.Equal(31L, ((LiteralExpr)stmt.SetClauses[0].Value).Value);

        var where = Assert.IsType<BinaryExpr>(stmt.Where);
        Assert.Equal(TokenType.Eq, where.Operator);
        Assert.Equal("Alice", ((LiteralExpr)where.Right).Value);
    }

    [Fact]
    public void DeleteStatement()
    {
        var stmt = Parser.Parse("DELETE FROM users WHERE id = 3") as DeleteStmt;
        Assert.NotNull(stmt);
        Assert.Equal("users", stmt.TableName);
        var where = Assert.IsType<BinaryExpr>(stmt.Where);
        Assert.Equal(TokenType.Eq, where.Operator);
        Assert.Equal(3L, ((LiteralExpr)where.Right).Value);
    }

    [Fact]
    public void CreateTable()
    {
        var stmt = Parser.Parse("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER)") as CreateTableStmt;
        Assert.NotNull(stmt);
        Assert.Equal("t", stmt.TableName);
        Assert.False(stmt.IfNotExists);
        Assert.Equal(3, stmt.Columns.Length);

        Assert.Equal("id", stmt.Columns[0].Name);
        Assert.Equal("INTEGER", stmt.Columns[0].TypeName);
        Assert.True(stmt.Columns[0].IsPrimaryKey);

        Assert.Equal("name", stmt.Columns[1].Name);
        Assert.Equal("TEXT", stmt.Columns[1].TypeName);
        Assert.True(stmt.Columns[1].IsNotNull);

        Assert.Equal("age", stmt.Columns[2].Name);
        Assert.Equal("INTEGER", stmt.Columns[2].TypeName);
    }

    [Fact]
    public void CreateTableIfNotExists()
    {
        var stmt = Parser.Parse("CREATE TABLE IF NOT EXISTS t (id INTEGER PRIMARY KEY)") as CreateTableStmt;
        Assert.NotNull(stmt);
        Assert.True(stmt.IfNotExists);
    }

    [Fact]
    public void DropTable()
    {
        var stmt = Parser.Parse("DROP TABLE users") as DropTableStmt;
        Assert.NotNull(stmt);
        Assert.Equal("users", stmt.TableName);
        Assert.False(stmt.IfExists);
    }

    [Fact]
    public void DropTableIfExists()
    {
        var stmt = Parser.Parse("DROP TABLE IF EXISTS temp") as DropTableStmt;
        Assert.NotNull(stmt);
        Assert.Equal("temp", stmt.TableName);
        Assert.True(stmt.IfExists);
    }

    [Fact]
    public void ExpressionPrecedence()
    {
        // 1 + 2 * 3 should parse as 1 + (2 * 3)
        var stmt = Parser.Parse("SELECT 1 + 2 * 3") as SelectStmt;
        Assert.NotNull(stmt);
        var expr = Assert.IsType<BinaryExpr>(stmt.Columns[0].Expression);
        Assert.Equal(TokenType.Plus, expr.Operator);
        Assert.Equal(1L, ((LiteralExpr)expr.Left).Value);
        var right = Assert.IsType<BinaryExpr>(expr.Right);
        Assert.Equal(TokenType.Star, right.Operator);
    }

    [Fact]
    public void IsNullExpression()
    {
        var stmt = Parser.Parse("SELECT * FROM t WHERE x IS NULL") as SelectStmt;
        Assert.NotNull(stmt);
        var isNull = Assert.IsType<IsNullExpr>(stmt.Where);
        Assert.False(isNull.IsNot);
    }

    [Fact]
    public void IsNotNullExpression()
    {
        var stmt = Parser.Parse("SELECT * FROM t WHERE x IS NOT NULL") as SelectStmt;
        Assert.NotNull(stmt);
        var isNull = Assert.IsType<IsNullExpr>(stmt.Where);
        Assert.True(isNull.IsNot);
    }

    [Fact]
    public void BetweenExpression()
    {
        var stmt = Parser.Parse("SELECT * FROM t WHERE x BETWEEN 1 AND 10") as SelectStmt;
        Assert.NotNull(stmt);
        var between = Assert.IsType<BetweenExpr>(stmt.Where);
        Assert.False(between.IsNot);
        Assert.Equal(1L, ((LiteralExpr)between.Low).Value);
        Assert.Equal(10L, ((LiteralExpr)between.High).Value);
    }

    [Fact]
    public void InExpression()
    {
        var stmt = Parser.Parse("SELECT * FROM t WHERE x IN (1, 2, 3)") as SelectStmt;
        Assert.NotNull(stmt);
        var inExpr = Assert.IsType<InExpr>(stmt.Where);
        Assert.False(inExpr.IsNot);
        Assert.Equal(3, inExpr.Values.Length);
    }

    [Fact]
    public void LikeExpression()
    {
        var stmt = Parser.Parse("SELECT * FROM t WHERE name LIKE '%test%'") as SelectStmt;
        Assert.NotNull(stmt);
        var like = Assert.IsType<LikeExpr>(stmt.Where);
        Assert.False(like.IsNot);
        Assert.Equal("%test%", ((LiteralExpr)like.Pattern).Value);
    }

    [Fact]
    public void NotLikeExpression()
    {
        var stmt = Parser.Parse("SELECT * FROM t WHERE name NOT LIKE 'x%'") as SelectStmt;
        Assert.NotNull(stmt);
        var like = Assert.IsType<LikeExpr>(stmt.Where);
        Assert.True(like.IsNot);
    }

    [Fact]
    public void QualifiedColumnRef()
    {
        var stmt = Parser.Parse("SELECT t.name FROM t") as SelectStmt;
        Assert.NotNull(stmt);
        var col = Assert.IsType<ColumnRefExpr>(stmt.Columns[0].Expression);
        Assert.Equal("t", col.TableName);
        Assert.Equal("name", col.ColumnName);
    }

    [Fact]
    public void AstPrinterProducesOutput()
    {
        var stmt = Parser.Parse("SELECT name, age FROM users WHERE age > 25");
        var output = AstPrinter.Print(stmt);
        Assert.Contains("Select", output);
        Assert.Contains("Column(name)", output);
        Assert.Contains("Column(age)", output);
        Assert.Contains("From: users", output);
        Assert.Contains("BinaryOp(Gt)", output);
    }

    [Fact]
    public void MalformedSqlThrowsWithLocation()
    {
        var ex = Assert.Throws<SqliteException>(() => Parser.Parse("SELECT FROM"));
        Assert.Contains("line", ex.Message);
        Assert.Contains("col", ex.Message);
    }

    [Fact]
    public void UnaryMinus()
    {
        var stmt = Parser.Parse("SELECT -1") as SelectStmt;
        Assert.NotNull(stmt);
        var unary = Assert.IsType<UnaryExpr>(stmt.Columns[0].Expression);
        Assert.Equal(TokenType.Minus, unary.Operator);
        Assert.Equal(1L, ((LiteralExpr)unary.Operand).Value);
    }

    [Fact]
    public void UpdateMultipleSetClauses()
    {
        var stmt = Parser.Parse("UPDATE t SET a = 1, b = 2") as UpdateStmt;
        Assert.NotNull(stmt);
        Assert.Equal(2, stmt.SetClauses.Length);
        Assert.Equal("a", stmt.SetClauses[0].ColumnName);
        Assert.Equal("b", stmt.SetClauses[1].ColumnName);
    }

    [Fact]
    public void SelectWithoutFrom()
    {
        var stmt = Parser.Parse("SELECT 1 + 1") as SelectStmt;
        Assert.NotNull(stmt);
        Assert.Null(stmt.From);
        Assert.IsType<BinaryExpr>(stmt.Columns[0].Expression);
    }
}
