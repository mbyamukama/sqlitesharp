using SQLite.Engine.Compiler;

namespace SQLite.Engine.Tests;

public class TokenizerTests
{
    private static Token[] Tokenize(string sql) => new Tokenizer(sql).Tokenize();

    [Fact]
    public void SimpleSelect()
    {
        var tokens = Tokenize("SELECT * FROM users");
        Assert.Equal(TokenType.Select, tokens[0].Type);
        Assert.Equal(TokenType.Star, tokens[1].Type);
        Assert.Equal(TokenType.From, tokens[2].Type);
        Assert.Equal(TokenType.Identifier, tokens[3].Type);
        Assert.Equal("users", tokens[3].Lexeme);
        Assert.Equal(TokenType.Eof, tokens[4].Type);
    }

    [Fact]
    public void WhereWithComparison()
    {
        var tokens = Tokenize("WHERE age >= 30");
        Assert.Equal(TokenType.Where, tokens[0].Type);
        Assert.Equal(TokenType.Identifier, tokens[1].Type);
        Assert.Equal("age", tokens[1].Lexeme);
        Assert.Equal(TokenType.Gte, tokens[2].Type);
        Assert.Equal(TokenType.Integer, tokens[3].Type);
        Assert.Equal("30", tokens[3].Lexeme);
    }

    [Fact]
    public void StringLiteral()
    {
        var tokens = Tokenize("'hello world'");
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("hello world", tokens[0].Lexeme);
    }

    [Fact]
    public void EscapedStringLiteral()
    {
        var tokens = Tokenize("'it''s fine'");
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("it's fine", tokens[0].Lexeme);
    }

    [Fact]
    public void LineComment()
    {
        var tokens = Tokenize("-- comment\nSELECT 1");
        Assert.Equal(TokenType.Select, tokens[0].Type);
        Assert.Equal(TokenType.Integer, tokens[1].Type);
        Assert.Equal("1", tokens[1].Lexeme);
    }

    [Fact]
    public void BlockComment()
    {
        var tokens = Tokenize("/* multi\nline */ SELECT 2");
        Assert.Equal(TokenType.Select, tokens[0].Type);
        Assert.Equal(TokenType.Integer, tokens[1].Type);
        Assert.Equal("2", tokens[1].Lexeme);
    }

    [Fact]
    public void QuotedIdentifier()
    {
        var tokens = Tokenize("\"quoted col\"");
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("quoted col", tokens[0].Lexeme);
    }

    [Fact]
    public void BacktickIdentifier()
    {
        var tokens = Tokenize("`my table`");
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("my table", tokens[0].Lexeme);
    }

    [Fact]
    public void BracketedIdentifier()
    {
        var tokens = Tokenize("[Column Name]");
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("Column Name", tokens[0].Lexeme);
    }

    [Fact]
    public void FloatLiteral()
    {
        var tokens = Tokenize("3.14");
        Assert.Equal(TokenType.Float, tokens[0].Type);
        Assert.Equal("3.14", tokens[0].Lexeme);
    }

    [Fact]
    public void FloatWithExponent()
    {
        var tokens = Tokenize("1.5e10");
        Assert.Equal(TokenType.Float, tokens[0].Type);
        Assert.Equal("1.5e10", tokens[0].Lexeme);
    }

    [Fact]
    public void BlobLiteral()
    {
        var tokens = Tokenize("X'48454C4C4F'");
        Assert.Equal(TokenType.Blob, tokens[0].Type);
        Assert.Equal("48454C4C4F", tokens[0].Lexeme);
    }

    [Fact]
    public void AllOperators()
    {
        var tokens = Tokenize("+ - * / % || = != <> < > <= >=");
        Assert.Equal(TokenType.Plus, tokens[0].Type);
        Assert.Equal(TokenType.Minus, tokens[1].Type);
        Assert.Equal(TokenType.Star, tokens[2].Type);
        Assert.Equal(TokenType.Slash, tokens[3].Type);
        Assert.Equal(TokenType.Percent, tokens[4].Type);
        Assert.Equal(TokenType.Concat, tokens[5].Type);
        Assert.Equal(TokenType.Eq, tokens[6].Type);
        Assert.Equal(TokenType.Neq, tokens[7].Type);
        Assert.Equal(TokenType.Neq, tokens[8].Type);
        Assert.Equal(TokenType.Lt, tokens[9].Type);
        Assert.Equal(TokenType.Gt, tokens[10].Type);
        Assert.Equal(TokenType.Lte, tokens[11].Type);
        Assert.Equal(TokenType.Gte, tokens[12].Type);
    }

    [Fact]
    public void Punctuation()
    {
        var tokens = Tokenize("( ) , ; .");
        Assert.Equal(TokenType.LeftParen, tokens[0].Type);
        Assert.Equal(TokenType.RightParen, tokens[1].Type);
        Assert.Equal(TokenType.Comma, tokens[2].Type);
        Assert.Equal(TokenType.Semicolon, tokens[3].Type);
        Assert.Equal(TokenType.Dot, tokens[4].Type);
    }

    [Fact]
    public void LineAndColumnTracking()
    {
        var tokens = Tokenize("SELECT\n  42");
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(1, tokens[0].Column);
        Assert.Equal(2, tokens[1].Line);
        Assert.Equal(3, tokens[1].Column);
    }

    [Fact]
    public void NullKeyword()
    {
        var tokens = Tokenize("NULL");
        Assert.Equal(TokenType.Null, tokens[0].Type);
    }

    [Fact]
    public void UnterminatedString()
    {
        var tokens = Tokenize("'oops");
        Assert.Equal(TokenType.Error, tokens[0].Type);
    }
}
