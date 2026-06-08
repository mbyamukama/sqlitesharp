using System.Text;

namespace SQLite.Engine.Compiler;

/// <summary>
/// Walks an AST and produces a readable indented text representation.
/// Useful for debugging and test assertions.
/// </summary>
public static class AstPrinter
{
    public static string Print(Stmt stmt)
    {
        var sb = new StringBuilder();
        PrintStmt(sb, stmt, 0);
        return sb.ToString().TrimEnd();
    }

    public static string Print(Expr expr)
    {
        var sb = new StringBuilder();
        PrintExpr(sb, expr, 0);
        return sb.ToString().TrimEnd();
    }

    private static void PrintStmt(StringBuilder sb, Stmt stmt, int indent)
    {
        switch (stmt)
        {
            case SelectStmt s:
                Line(sb, indent, "Select");
                if (s.Distinct) Line(sb, indent + 1, "DISTINCT");
                Line(sb, indent + 1, "Columns:");
                foreach (var col in s.Columns)
                {
                    PrintExpr(sb, col.Expression, indent + 2);
                    if (col.Alias != null) Line(sb, indent + 3, $"AS {col.Alias}");
                }
                if (s.From != null)
                {
                    var alias = s.From.Alias != null ? $" AS {s.From.Alias}" : "";
                    Line(sb, indent + 1, $"From: {s.From.TableName}{alias}");
                }
                if (s.Where != null)
                {
                    Line(sb, indent + 1, "Where:");
                    PrintExpr(sb, s.Where, indent + 2);
                }
                if (s.GroupBy != null)
                {
                    Line(sb, indent + 1, "GroupBy:");
                    foreach (var e in s.GroupBy) PrintExpr(sb, e, indent + 2);
                }
                if (s.Having != null)
                {
                    Line(sb, indent + 1, "Having:");
                    PrintExpr(sb, s.Having, indent + 2);
                }
                if (s.OrderBy != null)
                {
                    Line(sb, indent + 1, "OrderBy:");
                    foreach (var item in s.OrderBy)
                    {
                        PrintExpr(sb, item.Expression, indent + 2);
                        if (item.Descending) Line(sb, indent + 3, "DESC");
                    }
                }
                if (s.Limit != null)
                {
                    Line(sb, indent + 1, "Limit:");
                    PrintExpr(sb, s.Limit, indent + 2);
                }
                if (s.Offset != null)
                {
                    Line(sb, indent + 1, "Offset:");
                    PrintExpr(sb, s.Offset, indent + 2);
                }
                break;

            case InsertStmt s:
                Line(sb, indent, $"Insert into {s.TableName}");
                if (s.ColumnNames != null)
                    Line(sb, indent + 1, $"Columns: ({string.Join(", ", s.ColumnNames)})");
                for (int i = 0; i < s.ValueRows.Length; i++)
                {
                    Line(sb, indent + 1, $"Row {i}:");
                    foreach (var v in s.ValueRows[i])
                        PrintExpr(sb, v, indent + 2);
                }
                break;

            case UpdateStmt s:
                Line(sb, indent, $"Update {s.TableName}");
                Line(sb, indent + 1, "Set:");
                foreach (var set in s.SetClauses)
                {
                    Line(sb, indent + 2, $"{set.ColumnName} =");
                    PrintExpr(sb, set.Value, indent + 3);
                }
                if (s.Where != null)
                {
                    Line(sb, indent + 1, "Where:");
                    PrintExpr(sb, s.Where, indent + 2);
                }
                break;

            case DeleteStmt s:
                Line(sb, indent, $"Delete from {s.TableName}");
                if (s.Where != null)
                {
                    Line(sb, indent + 1, "Where:");
                    PrintExpr(sb, s.Where, indent + 2);
                }
                break;

            case CreateTableStmt s:
                var ifne = s.IfNotExists ? " IF NOT EXISTS" : "";
                Line(sb, indent, $"CreateTable{ifne} {s.TableName}");
                foreach (var col in s.Columns)
                {
                    var parts = new List<string> { col.Name };
                    if (col.TypeName != null) parts.Add(col.TypeName);
                    if (col.IsPrimaryKey) parts.Add("PK");
                    if (col.IsAutoincrement) parts.Add("AUTOINCREMENT");
                    if (col.IsNotNull) parts.Add("NOT NULL");
                    if (col.IsUnique) parts.Add("UNIQUE");
                    Line(sb, indent + 1, string.Join(" ", parts));
                }
                break;

            case DropTableStmt s:
                var ife = s.IfExists ? " IF EXISTS" : "";
                Line(sb, indent, $"DropTable{ife} {s.TableName}");
                break;
        }
    }

    private static void PrintExpr(StringBuilder sb, Expr expr, int indent)
    {
        switch (expr)
        {
            case LiteralExpr e:
                var val = e.Value switch
                {
                    null => "NULL",
                    string s => $"'{s}'",
                    _ => e.Value.ToString()
                };
                Line(sb, indent, $"Literal({val})");
                break;

            case ColumnRefExpr e:
                var qual = e.TableName != null ? $"{e.TableName}." : "";
                Line(sb, indent, $"Column({qual}{e.ColumnName})");
                break;

            case StarExpr e:
                var tbl = e.TableName != null ? $"{e.TableName}." : "";
                Line(sb, indent, $"{tbl}*");
                break;

            case BinaryExpr e:
                Line(sb, indent, $"BinaryOp({e.Operator})");
                PrintExpr(sb, e.Left, indent + 1);
                PrintExpr(sb, e.Right, indent + 1);
                break;

            case UnaryExpr e:
                Line(sb, indent, $"UnaryOp({e.Operator})");
                PrintExpr(sb, e.Operand, indent + 1);
                break;

            case FunctionCallExpr e:
                var dist = e.IsDistinct ? "DISTINCT " : "";
                Line(sb, indent, $"Func({dist}{e.FunctionName})");
                foreach (var arg in e.Arguments)
                    PrintExpr(sb, arg, indent + 1);
                break;

            case IsNullExpr e:
                Line(sb, indent, e.IsNot ? "IsNotNull" : "IsNull");
                PrintExpr(sb, e.Operand, indent + 1);
                break;

            case BetweenExpr e:
                Line(sb, indent, e.IsNot ? "NotBetween" : "Between");
                PrintExpr(sb, e.Operand, indent + 1);
                PrintExpr(sb, e.Low, indent + 1);
                PrintExpr(sb, e.High, indent + 1);
                break;

            case InExpr e:
                Line(sb, indent, e.IsNot ? "NotIn" : "In");
                PrintExpr(sb, e.Operand, indent + 1);
                foreach (var v in e.Values)
                    PrintExpr(sb, v, indent + 1);
                break;

            case LikeExpr e:
                Line(sb, indent, e.IsNot ? "NotLike" : "Like");
                PrintExpr(sb, e.Operand, indent + 1);
                PrintExpr(sb, e.Pattern, indent + 1);
                break;

            case CastExpr e:
                Line(sb, indent, $"Cast AS {e.TypeName}");
                PrintExpr(sb, e.Operand, indent + 1);
                break;

            case ParenExpr e:
                Line(sb, indent, "Paren");
                PrintExpr(sb, e.Inner, indent + 1);
                break;
        }
    }

    private static void Line(StringBuilder sb, int indent, string text)
    {
        sb.Append(' ', indent * 2);
        sb.AppendLine(text);
    }
}
