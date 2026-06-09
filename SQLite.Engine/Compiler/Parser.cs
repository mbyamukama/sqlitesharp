namespace SQLite.Engine.Compiler;

/// <summary>
/// Recursive-descent SQL parser. Consumes a token array and produces AST nodes.
/// Supports SELECT, INSERT, UPDATE, DELETE, CREATE TABLE, DROP TABLE.
/// </summary>
public sealed class Parser
{
    private readonly Token[] _tokens;
    private int _pos;

    public Parser(Token[] tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    /// <summary>
    /// Convenience: tokenize + parse in one call.
    /// </summary>
    public static Stmt Parse(string sql)
    {
        var tokenizer = new Tokenizer(sql);
        var tokens = tokenizer.Tokenize();
        var parser = new Parser(tokens);
        return parser.ParseStatement();
    }

    /// <summary>
    /// Parse a single statement from the token stream.
    /// </summary>
    public Stmt ParseStatement()
    {
        var stmt = Current.Type switch
        {
            TokenType.Select => (Stmt)ParseSelect(),
            TokenType.Insert => ParseInsert(),
            TokenType.Update => ParseUpdate(),
            TokenType.Delete => ParseDelete(),
            TokenType.Create => ParseCreate(),
            TokenType.Drop => ParseDrop(),
            _ => throw Error($"Expected statement, got {Current.Type}")
        };

        // Consume optional trailing semicolon
        if (Current.Type == TokenType.Semicolon)
            Advance();

        return stmt;
    }

    // ─── SELECT ─────────────────────────────────────────────────────────────

    private SelectStmt ParseSelect()
    {
        Expect(TokenType.Select);

        bool distinct = false;
        if (Current.Type == TokenType.Distinct)
        {
            distinct = true;
            Advance();
        }
        else if (Current.Type == TokenType.All)
        {
            Advance();
        }

        var columns = ParseResultColumns();

        TableRef? from = null;
        JoinClause[]? joins = null;
        if (Current.Type == TokenType.From)
        {
            Advance();
            from = ParseTableRef();

            // Parse JOIN clauses
            var joinList = new List<JoinClause>();
            while (IsJoinKeyword())
            {
                joinList.Add(ParseJoinClause());
            }
            if (joinList.Count > 0)
                joins = joinList.ToArray();
        }

        Expr? where = null;
        if (Current.Type == TokenType.Where)
        {
            Advance();
            where = ParseExpr();
        }

        Expr[]? groupBy = null;
        if (Current.Type == TokenType.Group)
        {
            Advance();
            Expect(TokenType.By);
            groupBy = ParseExprList();
        }

        Expr? having = null;
        if (Current.Type == TokenType.Having)
        {
            Advance();
            having = ParseExpr();
        }

        OrderByItem[]? orderBy = null;
        if (Current.Type == TokenType.Order)
        {
            Advance();
            Expect(TokenType.By);
            orderBy = ParseOrderByList();
        }

        Expr? limit = null;
        Expr? offset = null;
        if (Current.Type == TokenType.Limit)
        {
            Advance();
            limit = ParseExpr();
            if (Current.Type == TokenType.Offset)
            {
                Advance();
                offset = ParseExpr();
            }
            else if (Current.Type == TokenType.Comma)
            {
                // LIMIT offset, count — SQLite alternate syntax
                Advance();
                offset = limit;
                limit = ParseExpr();
            }
        }

        return new SelectStmt
        {
            Distinct = distinct,
            Columns = columns,
            From = from,
            Joins = joins,
            Where = where,
            GroupBy = groupBy,
            Having = having,
            OrderBy = orderBy,
            Limit = limit,
            Offset = offset,
        };
    }

    private ResultColumn[] ParseResultColumns()
    {
        var cols = new List<ResultColumn>();
        cols.Add(ParseResultColumn());
        while (Current.Type == TokenType.Comma)
        {
            Advance();
            cols.Add(ParseResultColumn());
        }
        return cols.ToArray();
    }

    private ResultColumn ParseResultColumn()
    {
        // table.* or *
        if (Current.Type == TokenType.Star)
        {
            Advance();
            return new ResultColumn(new StarExpr());
        }

        // Could be table.* — check for ident.star
        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Dot && Peek(2).Type == TokenType.Star)
        {
            string table = Current.Lexeme;
            Advance(); // ident
            Advance(); // dot
            Advance(); // star
            return new ResultColumn(new StarExpr(table));
        }

        var expr = ParseExpr();
        string? alias = null;
        if (Current.Type == TokenType.As)
        {
            Advance();
            alias = ExpectIdentifier();
        }
        else if (Current.Type == TokenType.Identifier)
        {
            // Implicit alias (no AS keyword)
            alias = Current.Lexeme;
            Advance();
        }
        return new ResultColumn(expr, alias);
    }

    private TableRef ParseTableRef()
    {
        string name = ExpectIdentifier();
        string? alias = null;
        if (Current.Type == TokenType.As)
        {
            Advance();
            alias = ExpectIdentifier();
        }
        else if (Current.Type == TokenType.Identifier)
        {
            alias = Current.Lexeme;
            Advance();
        }
        return new TableRef(name, alias);
    }

    private bool IsJoinKeyword()
    {
        return Current.Type == TokenType.Join ||
               Current.Type == TokenType.Inner ||
               Current.Type == TokenType.Left ||
               Current.Type == TokenType.Cross;
    }

    private JoinClause ParseJoinClause()
    {
        var joinType = JoinClause.JoinType.Inner;

        if (Current.Type == TokenType.Inner)
        {
            Advance();
            joinType = JoinClause.JoinType.Inner;
        }
        else if (Current.Type == TokenType.Left)
        {
            Advance();
            joinType = JoinClause.JoinType.Left;
            if (Current.Type == TokenType.Outer)
                Advance(); // optional OUTER
        }
        else if (Current.Type == TokenType.Cross)
        {
            Advance();
            joinType = JoinClause.JoinType.Cross;
        }

        Expect(TokenType.Join);
        var table = ParseTableRef();

        Expr? on = null;
        if (Current.Type == TokenType.On)
        {
            Advance();
            on = ParseExpr();
        }

        return new JoinClause(joinType, table, on);
    }

    private OrderByItem[] ParseOrderByList()
    {
        var items = new List<OrderByItem>();
        items.Add(ParseOrderByItem());
        while (Current.Type == TokenType.Comma)
        {
            Advance();
            items.Add(ParseOrderByItem());
        }
        return items.ToArray();
    }

    private OrderByItem ParseOrderByItem()
    {
        var expr = ParseExpr();
        bool desc = false;
        if (Current.Type == TokenType.Asc)
            Advance();
        else if (Current.Type == TokenType.Desc)
        {
            desc = true;
            Advance();
        }
        return new OrderByItem(expr, desc);
    }

    // ─── INSERT ─────────────────────────────────────────────────────────────

    private InsertStmt ParseInsert()
    {
        Expect(TokenType.Insert);
        Expect(TokenType.Into);
        string tableName = ExpectIdentifier();

        string[]? columnNames = null;
        if (Current.Type == TokenType.LeftParen)
        {
            Advance();
            var cols = new List<string>();
            cols.Add(ExpectIdentifier());
            while (Current.Type == TokenType.Comma)
            {
                Advance();
                cols.Add(ExpectIdentifier());
            }
            Expect(TokenType.RightParen);
            columnNames = cols.ToArray();
        }

        Expect(TokenType.Values);

        var rows = new List<Expr[]>();
        rows.Add(ParseValueRow());
        while (Current.Type == TokenType.Comma)
        {
            Advance();
            rows.Add(ParseValueRow());
        }

        return new InsertStmt
        {
            TableName = tableName,
            ColumnNames = columnNames,
            ValueRows = rows.ToArray(),
        };
    }

    private Expr[] ParseValueRow()
    {
        Expect(TokenType.LeftParen);
        var values = ParseExprList();
        Expect(TokenType.RightParen);
        return values;
    }

    // ─── UPDATE ─────────────────────────────────────────────────────────────

    private UpdateStmt ParseUpdate()
    {
        Expect(TokenType.Update);
        string tableName = ExpectIdentifier();
        Expect(TokenType.Set);

        var sets = new List<SetClause>();
        sets.Add(ParseSetClause());
        while (Current.Type == TokenType.Comma)
        {
            Advance();
            sets.Add(ParseSetClause());
        }

        Expr? where = null;
        if (Current.Type == TokenType.Where)
        {
            Advance();
            where = ParseExpr();
        }

        return new UpdateStmt
        {
            TableName = tableName,
            SetClauses = sets.ToArray(),
            Where = where,
        };
    }

    private SetClause ParseSetClause()
    {
        string col = ExpectIdentifier();
        Expect(TokenType.Eq);
        var value = ParseExpr();
        return new SetClause(col, value);
    }

    // ─── DELETE ─────────────────────────────────────────────────────────────

    private DeleteStmt ParseDelete()
    {
        Expect(TokenType.Delete);
        Expect(TokenType.From);
        string tableName = ExpectIdentifier();

        Expr? where = null;
        if (Current.Type == TokenType.Where)
        {
            Advance();
            where = ParseExpr();
        }

        return new DeleteStmt { TableName = tableName, Where = where };
    }

    // ─── CREATE TABLE ───────────────────────────────────────────────────────

    private CreateTableStmt ParseCreate()
    {
        Expect(TokenType.Create);
        Expect(TokenType.Table);

        bool ifNotExists = false;
        if (Current.Type == TokenType.If)
        {
            Advance();
            Expect(TokenType.Not);
            Expect(TokenType.Exists);
            ifNotExists = true;
        }

        string tableName = ExpectIdentifier();
        Expect(TokenType.LeftParen);

        var columns = new List<ColumnDef>();
        columns.Add(ParseColumnDef());
        while (Current.Type == TokenType.Comma)
        {
            Advance();
            // Could be a table constraint — skip for now if not an identifier
            if (Current.Type == TokenType.Primary || Current.Type == TokenType.Unique ||
                Current.Type == TokenType.Check || Current.Type == TokenType.Foreign ||
                Current.Type == TokenType.Constraint)
            {
                SkipTableConstraint();
            }
            else
            {
                columns.Add(ParseColumnDef());
            }
        }

        Expect(TokenType.RightParen);

        return new CreateTableStmt
        {
            TableName = tableName,
            IfNotExists = ifNotExists,
            Columns = columns.ToArray(),
        };
    }

    private ColumnDef ParseColumnDef()
    {
        string name = ExpectIdentifier();
        string? typeName = null;
        bool isPK = false;
        bool isAutoincrement = false;
        bool isNotNull = false;
        bool isUnique = false;
        Expr? defaultValue = null;

        // Optional type name (may be multi-word like VARCHAR(100))
        if (Current.Type == TokenType.Identifier || IsTypeKeyword(Current.Type))
        {
            typeName = ParseTypeName();
        }

        // Column constraints
        while (true)
        {
            if (Current.Type == TokenType.Primary)
            {
                Advance();
                Expect(TokenType.Key);
                isPK = true;
                if (Current.Type == TokenType.Autoincrement)
                {
                    isAutoincrement = true;
                    Advance();
                }
            }
            else if (Current.Type == TokenType.Not)
            {
                Advance();
                Expect(TokenType.Null);
                isNotNull = true;
            }
            else if (Current.Type == TokenType.Unique)
            {
                isUnique = true;
                Advance();
            }
            else if (Current.Type == TokenType.Default)
            {
                Advance();
                if (Current.Type == TokenType.LeftParen)
                {
                    Advance();
                    defaultValue = ParseExpr();
                    Expect(TokenType.RightParen);
                }
                else
                {
                    defaultValue = ParsePrimary();
                }
            }
            else if (Current.Type == TokenType.Check)
            {
                // Skip CHECK(expr)
                Advance();
                Expect(TokenType.LeftParen);
                int depth = 1;
                while (depth > 0 && Current.Type != TokenType.Eof)
                {
                    if (Current.Type == TokenType.LeftParen) depth++;
                    if (Current.Type == TokenType.RightParen) depth--;
                    if (depth > 0) Advance();
                }
                Expect(TokenType.RightParen);
            }
            else if (Current.Type == TokenType.References)
            {
                // Skip REFERENCES table(col)
                Advance();
                ExpectIdentifier(); // table name
                if (Current.Type == TokenType.LeftParen)
                {
                    Advance();
                    while (Current.Type != TokenType.RightParen && Current.Type != TokenType.Eof)
                        Advance();
                    Expect(TokenType.RightParen);
                }
            }
            else if (Current.Type == TokenType.Collate)
            {
                Advance();
                ExpectIdentifier(); // collation name
            }
            else
            {
                break;
            }
        }

        return new ColumnDef
        {
            Name = name,
            TypeName = typeName,
            IsPrimaryKey = isPK,
            IsAutoincrement = isAutoincrement,
            IsNotNull = isNotNull,
            IsUnique = isUnique,
            DefaultValue = defaultValue,
        };
    }

    private string ParseTypeName()
    {
        // Consume type identifier(s), possibly with (N) or (N,M)
        var parts = new List<string>();
        parts.Add(Current.Lexeme);
        Advance();

        // Multi-word types like "DOUBLE PRECISION" or "VARYING CHARACTER"
        while (Current.Type == TokenType.Identifier || IsTypeKeyword(Current.Type))
        {
            // Don't consume column constraint keywords
            if (Current.Type == TokenType.Primary || Current.Type == TokenType.Not ||
                Current.Type == TokenType.Unique || Current.Type == TokenType.Default ||
                Current.Type == TokenType.Check || Current.Type == TokenType.References)
                break;
            parts.Add(Current.Lexeme);
            Advance();
        }

        // Optional (N) or (N,M)
        if (Current.Type == TokenType.LeftParen)
        {
            parts.Add("(");
            Advance();
            while (Current.Type != TokenType.RightParen && Current.Type != TokenType.Eof)
            {
                parts.Add(Current.Lexeme);
                Advance();
            }
            parts.Add(")");
            if (Current.Type == TokenType.RightParen) Advance();
        }

        return string.Join(" ", parts);
    }

    private void SkipTableConstraint()
    {
        // Skip until comma or closing paren at depth 0
        int depth = 0;
        while (Current.Type != TokenType.Eof)
        {
            if (Current.Type == TokenType.LeftParen) depth++;
            else if (Current.Type == TokenType.RightParen)
            {
                if (depth == 0) break;
                depth--;
            }
            else if (Current.Type == TokenType.Comma && depth == 0)
            {
                break;
            }
            Advance();
        }
    }

    private static bool IsTypeKeyword(TokenType t)
    {
        // Some SQL type names happen to be keywords
        return t == TokenType.Integer || t == TokenType.Null;
    }

    // ─── DROP TABLE ─────────────────────────────────────────────────────────

    private DropTableStmt ParseDrop()
    {
        Expect(TokenType.Drop);
        Expect(TokenType.Table);

        bool ifExists = false;
        if (Current.Type == TokenType.If)
        {
            Advance();
            Expect(TokenType.Exists);
            ifExists = true;
        }

        string tableName = ExpectIdentifier();

        return new DropTableStmt { TableName = tableName, IfExists = ifExists };
    }

    // ─── Expression parsing (precedence climbing) ───────────────────────────

    private Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Current.Type == TokenType.Or)
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryExpr(left, TokenType.Or, right);
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (Current.Type == TokenType.And)
        {
            Advance();
            var right = ParseNot();
            left = new BinaryExpr(left, TokenType.And, right);
        }
        return left;
    }

    private Expr ParseNot()
    {
        if (Current.Type == TokenType.Not)
        {
            Advance();
            var operand = ParseNot();
            return new UnaryExpr(TokenType.Not, operand);
        }
        return ParseComparison();
    }

    private Expr ParseComparison()
    {
        var left = ParseAddition();

        // IS [NOT] NULL
        if (Current.Type == TokenType.Is)
        {
            Advance();
            bool isNot = false;
            if (Current.Type == TokenType.Not)
            {
                isNot = true;
                Advance();
            }
            Expect(TokenType.Null);
            return new IsNullExpr(left, isNot);
        }

        // [NOT] BETWEEN low AND high
        bool notPrefix = false;
        if (Current.Type == TokenType.Not)
        {
            // Peek ahead for BETWEEN, IN, LIKE
            if (Peek(1).Type == TokenType.Between || Peek(1).Type == TokenType.In || Peek(1).Type == TokenType.Like)
            {
                notPrefix = true;
                Advance();
            }
        }

        if (Current.Type == TokenType.Between)
        {
            Advance();
            var low = ParseAddition();
            Expect(TokenType.And);
            var high = ParseAddition();
            return new BetweenExpr(left, low, high, notPrefix);
        }

        if (Current.Type == TokenType.In)
        {
            Advance();
            Expect(TokenType.LeftParen);
            var values = ParseExprList();
            Expect(TokenType.RightParen);
            return new InExpr(left, values, notPrefix);
        }

        if (Current.Type == TokenType.Like || Current.Type == TokenType.Glob)
        {
            Advance();
            var pattern = ParseAddition();
            return new LikeExpr(left, pattern, notPrefix);
        }

        // Standard comparisons: =, !=, <>, <, >, <=, >=
        if (IsComparisonOp(Current.Type))
        {
            var op = Current.Type;
            Advance();
            var right = ParseAddition();
            return new BinaryExpr(left, op, right);
        }

        return left;
    }

    private Expr ParseAddition()
    {
        var left = ParseMultiplication();
        while (Current.Type == TokenType.Plus || Current.Type == TokenType.Minus || Current.Type == TokenType.Concat)
        {
            var op = Current.Type;
            Advance();
            var right = ParseMultiplication();
            left = new BinaryExpr(left, op, right);
        }
        return left;
    }

    private Expr ParseMultiplication()
    {
        var left = ParseUnary();
        while (Current.Type == TokenType.Star || Current.Type == TokenType.Slash || Current.Type == TokenType.Percent)
        {
            var op = Current.Type;
            Advance();
            var right = ParseUnary();
            left = new BinaryExpr(left, op, right);
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Current.Type == TokenType.Minus || Current.Type == TokenType.Plus)
        {
            var op = Current.Type;
            Advance();
            var operand = ParseUnary();
            return new UnaryExpr(op, operand);
        }
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var token = Current;

        switch (token.Type)
        {
            case TokenType.Integer:
                Advance();
                return new LiteralExpr(long.Parse(token.Lexeme), TokenType.Integer);

            case TokenType.Float:
                Advance();
                return new LiteralExpr(double.Parse(token.Lexeme, System.Globalization.CultureInfo.InvariantCulture), TokenType.Float);

            case TokenType.String:
                Advance();
                return new LiteralExpr(token.Lexeme, TokenType.String);

            case TokenType.Blob:
                Advance();
                return new LiteralExpr(token.Lexeme, TokenType.Blob);

            case TokenType.Null:
                Advance();
                return new LiteralExpr(null, TokenType.Null);

            case TokenType.Star:
                Advance();
                return new StarExpr();

            case TokenType.LeftParen:
                Advance();
                var inner = ParseExpr();
                Expect(TokenType.RightParen);
                return new ParenExpr(inner);

            case TokenType.Cast:
                return ParseCast();

            case TokenType.Identifier:
                return ParseIdentifierExpr();

            default:
                throw Error($"Expected expression, got {token.Type} '{token.Lexeme}'");
        }
    }

    private Expr ParseIdentifierExpr()
    {
        string name = Current.Lexeme;
        Advance();

        // Function call: name(...)
        if (Current.Type == TokenType.LeftParen)
        {
            Advance();
            bool isDistinct = false;
            if (Current.Type == TokenType.Distinct)
            {
                isDistinct = true;
                Advance();
            }

            Expr[] args;
            if (Current.Type == TokenType.RightParen)
            {
                args = [];
            }
            else if (Current.Type == TokenType.Star)
            {
                // e.g. count(*)
                args = [new StarExpr()];
                Advance();
            }
            else
            {
                args = ParseExprList();
            }
            Expect(TokenType.RightParen);
            return new FunctionCallExpr(name, args, isDistinct);
        }

        // Qualified column: table.column or table.*
        if (Current.Type == TokenType.Dot)
        {
            Advance();
            if (Current.Type == TokenType.Star)
            {
                Advance();
                return new StarExpr(name);
            }
            string colName = ExpectIdentifier();
            return new ColumnRefExpr(colName, name);
        }

        return new ColumnRefExpr(name);
    }

    private CastExpr ParseCast()
    {
        Expect(TokenType.Cast);
        Expect(TokenType.LeftParen);
        var expr = ParseExpr();
        Expect(TokenType.As);
        string typeName = ParseTypeName();
        Expect(TokenType.RightParen);
        return new CastExpr(expr, typeName);
    }

    private Expr[] ParseExprList()
    {
        var list = new List<Expr>();
        list.Add(ParseExpr());
        while (Current.Type == TokenType.Comma)
        {
            Advance();
            list.Add(ParseExpr());
        }
        return list.ToArray();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Token Current => _pos < _tokens.Length ? _tokens[_pos] : new Token(TokenType.Eof, "", 0, 0);

    private Token Peek(int offset)
    {
        int idx = _pos + offset;
        return idx < _tokens.Length ? _tokens[idx] : new Token(TokenType.Eof, "", 0, 0);
    }

    private void Advance() => _pos++;

    private void Expect(TokenType type)
    {
        if (Current.Type != type)
            throw Error($"Expected {type}, got {Current.Type} '{Current.Lexeme}'");
        Advance();
    }

    private string ExpectIdentifier()
    {
        if (Current.Type != TokenType.Identifier)
            throw Error($"Expected identifier, got {Current.Type} '{Current.Lexeme}'");
        string name = Current.Lexeme;
        Advance();
        return name;
    }

    private static bool IsComparisonOp(TokenType t) =>
        t == TokenType.Eq || t == TokenType.Neq || t == TokenType.Lt ||
        t == TokenType.Gt || t == TokenType.Lte || t == TokenType.Gte;

    private SqliteException Error(string message)
    {
        var tok = Current;
        return new SqliteException(SqliteResult.Error, $"Parse error at line {tok.Line}, col {tok.Column}: {message}");
    }
}
