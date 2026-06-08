namespace SQLite.Engine.Compiler;

/// <summary>
/// SQL tokenizer — scans a SQL string into a sequence of tokens.
/// Handles keywords, identifiers (plain, "quoted", `backtick`, [bracketed]),
/// string literals, numeric literals, blob literals, operators, and comments.
/// </summary>
public sealed class Tokenizer
{
    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = TokenType.Select,
        ["FROM"] = TokenType.From,
        ["WHERE"] = TokenType.Where,
        ["AND"] = TokenType.And,
        ["OR"] = TokenType.Or,
        ["NOT"] = TokenType.Not,
        ["IN"] = TokenType.In,
        ["IS"] = TokenType.Is,
        ["LIKE"] = TokenType.Like,
        ["BETWEEN"] = TokenType.Between,
        ["AS"] = TokenType.As,
        ["ON"] = TokenType.On,
        ["JOIN"] = TokenType.Join,
        ["LEFT"] = TokenType.Left,
        ["INNER"] = TokenType.Inner,
        ["CROSS"] = TokenType.Cross,
        ["OUTER"] = TokenType.Outer,
        ["INSERT"] = TokenType.Insert,
        ["INTO"] = TokenType.Into,
        ["VALUES"] = TokenType.Values,
        ["UPDATE"] = TokenType.Update,
        ["SET"] = TokenType.Set,
        ["DELETE"] = TokenType.Delete,
        ["CREATE"] = TokenType.Create,
        ["TABLE"] = TokenType.Table,
        ["DROP"] = TokenType.Drop,
        ["IF"] = TokenType.If,
        ["EXISTS"] = TokenType.Exists,
        ["PRIMARY"] = TokenType.Primary,
        ["KEY"] = TokenType.Key,
        ["AUTOINCREMENT"] = TokenType.Autoincrement,
        ["UNIQUE"] = TokenType.Unique,
        ["INDEX"] = TokenType.Index,
        ["ORDER"] = TokenType.Order,
        ["BY"] = TokenType.By,
        ["ASC"] = TokenType.Asc,
        ["DESC"] = TokenType.Desc,
        ["LIMIT"] = TokenType.Limit,
        ["OFFSET"] = TokenType.Offset,
        ["GROUP"] = TokenType.Group,
        ["HAVING"] = TokenType.Having,
        ["DISTINCT"] = TokenType.Distinct,
        ["ALL"] = TokenType.All,
        ["UNION"] = TokenType.Union,
        ["EXCEPT"] = TokenType.Except,
        ["INTERSECT"] = TokenType.Intersect,
        ["CASE"] = TokenType.Case,
        ["WHEN"] = TokenType.When,
        ["THEN"] = TokenType.Then,
        ["ELSE"] = TokenType.Else,
        ["END"] = TokenType.End,
        ["CAST"] = TokenType.Cast,
        ["COLLATE"] = TokenType.Collate,
        ["DEFAULT"] = TokenType.Default,
        ["CHECK"] = TokenType.Check,
        ["FOREIGN"] = TokenType.Foreign,
        ["REFERENCES"] = TokenType.References,
        ["CONSTRAINT"] = TokenType.Constraint,
        ["NULL"] = TokenType.Null,
        ["GLOB"] = TokenType.Glob,
        ["ESCAPE"] = TokenType.Escape,
    };

    private readonly string _source;
    private int _pos;
    private int _line;
    private int _col;

    public Tokenizer(string source)
    {
        _source = source;
        _pos = 0;
        _line = 1;
        _col = 1;
    }

    /// <summary>
    /// Tokenize the entire input and return the token array (terminated by EOF).
    /// </summary>
    public Token[] Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Type == TokenType.Eof || token.Type == TokenType.Error)
                break;
        }
        return tokens.ToArray();
    }

    private Token NextToken()
    {
        SkipWhitespaceAndComments();

        if (_pos >= _source.Length)
            return new Token(TokenType.Eof, "", _line, _col);

        int startLine = _line;
        int startCol = _col;
        char c = _source[_pos];

        // String literal
        if (c == '\'')
            return ReadString(startLine, startCol);

        // Blob literal X'...'
        if ((c == 'x' || c == 'X') && Peek(1) == '\'')
            return ReadBlob(startLine, startCol);

        // Numeric literal
        if (char.IsDigit(c) || (c == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
            return ReadNumber(startLine, startCol);

        // Quoted identifier "..."
        if (c == '"')
            return ReadQuotedIdentifier('"', startLine, startCol);

        // Backtick identifier `...`
        if (c == '`')
            return ReadQuotedIdentifier('`', startLine, startCol);

        // Bracketed identifier [...]
        if (c == '[')
            return ReadBracketedIdentifier(startLine, startCol);

        // Identifier or keyword
        if (IsIdentStart(c))
            return ReadIdentifierOrKeyword(startLine, startCol);

        // Operators and punctuation
        return ReadOperator(startLine, startCol);
    }

    private Token ReadString(int startLine, int startCol)
    {
        Advance(); // skip opening '
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\'')
            {
                Advance();
                // Doubled quote = escaped single quote
                if (_pos < _source.Length && _source[_pos] == '\'')
                {
                    sb.Append('\'');
                    Advance();
                }
                else
                {
                    return new Token(TokenType.String, sb.ToString(), startLine, startCol);
                }
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
        return new Token(TokenType.Error, "Unterminated string literal", startLine, startCol);
    }

    private Token ReadBlob(int startLine, int startCol)
    {
        Advance(); // skip X
        Advance(); // skip '
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && _source[_pos] != '\'')
        {
            sb.Append(_source[_pos]);
            Advance();
        }
        if (_pos < _source.Length)
            Advance(); // skip closing '
        else
            return new Token(TokenType.Error, "Unterminated blob literal", startLine, startCol);

        return new Token(TokenType.Blob, sb.ToString(), startLine, startCol);
    }

    private Token ReadNumber(int startLine, int startCol)
    {
        int start = _pos;
        bool isFloat = false;

        // Integer part
        while (_pos < _source.Length && char.IsDigit(_source[_pos]))
            Advance();

        // Fractional part
        if (_pos < _source.Length && _source[_pos] == '.' &&
            (_pos + 1 >= _source.Length || _source[_pos + 1] != '.'))
        {
            isFloat = true;
            Advance(); // skip .
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                Advance();
        }

        // Exponent
        if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
        {
            isFloat = true;
            Advance();
            if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                Advance();
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                Advance();
        }

        string lexeme = _source[start.._pos];
        return new Token(isFloat ? TokenType.Float : TokenType.Integer, lexeme, startLine, startCol);
    }

    private Token ReadQuotedIdentifier(char quote, int startLine, int startCol)
    {
        Advance(); // skip opening quote
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == quote)
            {
                Advance();
                // Doubled quote = escaped
                if (_pos < _source.Length && _source[_pos] == quote)
                {
                    sb.Append(quote);
                    Advance();
                }
                else
                {
                    return new Token(TokenType.Identifier, sb.ToString(), startLine, startCol);
                }
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
        return new Token(TokenType.Error, "Unterminated quoted identifier", startLine, startCol);
    }

    private Token ReadBracketedIdentifier(int startLine, int startCol)
    {
        Advance(); // skip [
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && _source[_pos] != ']')
        {
            sb.Append(_source[_pos]);
            Advance();
        }
        if (_pos < _source.Length)
            Advance(); // skip ]
        else
            return new Token(TokenType.Error, "Unterminated bracketed identifier", startLine, startCol);

        return new Token(TokenType.Identifier, sb.ToString(), startLine, startCol);
    }

    private Token ReadIdentifierOrKeyword(int startLine, int startCol)
    {
        int start = _pos;
        while (_pos < _source.Length && IsIdentChar(_source[_pos]))
            Advance();

        string lexeme = _source[start.._pos];

        if (Keywords.TryGetValue(lexeme, out var kwType))
            return new Token(kwType, lexeme, startLine, startCol);

        return new Token(TokenType.Identifier, lexeme, startLine, startCol);
    }

    private Token ReadOperator(int startLine, int startCol)
    {
        char c = _source[_pos];
        Advance();

        switch (c)
        {
            case '+': return new Token(TokenType.Plus, "+", startLine, startCol);
            case '-': return new Token(TokenType.Minus, "-", startLine, startCol);
            case '*': return new Token(TokenType.Star, "*", startLine, startCol);
            case '/': return new Token(TokenType.Slash, "/", startLine, startCol);
            case '%': return new Token(TokenType.Percent, "%", startLine, startCol);
            case '(': return new Token(TokenType.LeftParen, "(", startLine, startCol);
            case ')': return new Token(TokenType.RightParen, ")", startLine, startCol);
            case ',': return new Token(TokenType.Comma, ",", startLine, startCol);
            case ';': return new Token(TokenType.Semicolon, ";", startLine, startCol);
            case '.': return new Token(TokenType.Dot, ".", startLine, startCol);
            case '=': return new Token(TokenType.Eq, "=", startLine, startCol);
            case '<':
                if (_pos < _source.Length)
                {
                    if (_source[_pos] == '=') { Advance(); return new Token(TokenType.Lte, "<=", startLine, startCol); }
                    if (_source[_pos] == '>') { Advance(); return new Token(TokenType.Neq, "<>", startLine, startCol); }
                }
                return new Token(TokenType.Lt, "<", startLine, startCol);
            case '>':
                if (_pos < _source.Length && _source[_pos] == '=')
                { Advance(); return new Token(TokenType.Gte, ">=", startLine, startCol); }
                return new Token(TokenType.Gt, ">", startLine, startCol);
            case '!':
                if (_pos < _source.Length && _source[_pos] == '=')
                { Advance(); return new Token(TokenType.Neq, "!=", startLine, startCol); }
                return new Token(TokenType.Error, $"Unexpected character '!'", startLine, startCol);
            case '|':
                if (_pos < _source.Length && _source[_pos] == '|')
                { Advance(); return new Token(TokenType.Concat, "||", startLine, startCol); }
                return new Token(TokenType.Error, $"Unexpected character '|'", startLine, startCol);
            default:
                return new Token(TokenType.Error, $"Unexpected character '{c}'", startLine, startCol);
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            // Line comment: --
            if (c == '-' && Peek(1) == '-')
            {
                while (_pos < _source.Length && _source[_pos] != '\n')
                    Advance();
                continue;
            }

            // Block comment: /* ... */
            if (c == '/' && Peek(1) == '*')
            {
                Advance(); Advance(); // skip /*
                while (_pos < _source.Length)
                {
                    if (_source[_pos] == '*' && Peek(1) == '/')
                    {
                        Advance(); Advance(); // skip */
                        break;
                    }
                    Advance();
                }
                continue;
            }

            break;
        }
    }

    private void Advance()
    {
        if (_pos < _source.Length)
        {
            if (_source[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }

    private char Peek(int offset)
    {
        int idx = _pos + offset;
        return idx < _source.Length ? _source[idx] : '\0';
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
