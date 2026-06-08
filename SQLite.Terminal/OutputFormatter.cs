namespace SQLite.Terminal;

/// <summary>
/// Output display modes for the SQL shell.
/// </summary>
public enum OutputMode
{
    Table,
    Column,
    Csv,
    Json,
    Line,
}

/// <summary>
/// Formats query results for display in various modes.
/// </summary>
public static class OutputFormatter
{
    public static void PrintResults(string[] columnNames, List<object?[]> rows, OutputMode mode, TextWriter output)
    {
        switch (mode)
        {
            case OutputMode.Table:
                PrintTable(columnNames, rows, output);
                break;
            case OutputMode.Column:
                PrintColumn(columnNames, rows, output);
                break;
            case OutputMode.Csv:
                PrintCsv(columnNames, rows, output);
                break;
            case OutputMode.Json:
                PrintJson(columnNames, rows, output);
                break;
            case OutputMode.Line:
                PrintLine(columnNames, rows, output);
                break;
        }
    }

    private static void PrintTable(string[] columnNames, List<object?[]> rows, TextWriter output)
    {
        int colCount = columnNames.Length;
        int[] widths = new int[colCount];

        // Compute column widths
        for (int i = 0; i < colCount; i++)
            widths[i] = columnNames[i].Length;

        foreach (var row in rows)
        {
            for (int i = 0; i < colCount && i < row.Length; i++)
            {
                int len = FormatValue(row[i]).Length;
                if (len > widths[i]) widths[i] = len;
            }
        }

        // Cap widths at 40
        for (int i = 0; i < colCount; i++)
            widths[i] = Math.Min(widths[i], 40);

        // Top border
        output.Write("┌");
        for (int i = 0; i < colCount; i++)
        {
            output.Write(new string('─', widths[i] + 2));
            output.Write(i < colCount - 1 ? "┬" : "┐");
        }
        output.WriteLine();

        // Header
        output.Write("│");
        for (int i = 0; i < colCount; i++)
        {
            output.Write(' ');
            output.Write(columnNames[i].PadRight(widths[i]));
            output.Write(" │");
        }
        output.WriteLine();

        // Header separator
        output.Write("├");
        for (int i = 0; i < colCount; i++)
        {
            output.Write(new string('─', widths[i] + 2));
            output.Write(i < colCount - 1 ? "┼" : "┤");
        }
        output.WriteLine();

        // Rows
        foreach (var row in rows)
        {
            output.Write("│");
            for (int i = 0; i < colCount; i++)
            {
                string val = i < row.Length ? FormatValue(row[i]) : "";
                if (val.Length > widths[i])
                    val = val[..widths[i]];
                output.Write(' ');
                output.Write(val.PadRight(widths[i]));
                output.Write(" │");
            }
            output.WriteLine();
        }

        // Bottom border
        output.Write("└");
        for (int i = 0; i < colCount; i++)
        {
            output.Write(new string('─', widths[i] + 2));
            output.Write(i < colCount - 1 ? "┴" : "┘");
        }
        output.WriteLine();
    }

    private static void PrintColumn(string[] columnNames, List<object?[]> rows, TextWriter output)
    {
        int colCount = columnNames.Length;
        int[] widths = new int[colCount];

        for (int i = 0; i < colCount; i++)
            widths[i] = columnNames[i].Length;

        foreach (var row in rows)
        {
            for (int i = 0; i < colCount && i < row.Length; i++)
            {
                int len = FormatValue(row[i]).Length;
                if (len > widths[i]) widths[i] = len;
            }
        }

        // Header
        for (int i = 0; i < colCount; i++)
        {
            if (i > 0) output.Write("  ");
            output.Write(columnNames[i].PadRight(widths[i]));
        }
        output.WriteLine();

        // Separator
        for (int i = 0; i < colCount; i++)
        {
            if (i > 0) output.Write("  ");
            output.Write(new string('-', widths[i]));
        }
        output.WriteLine();

        // Rows
        foreach (var row in rows)
        {
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) output.Write("  ");
                string val = i < row.Length ? FormatValue(row[i]) : "";
                output.Write(val.PadRight(widths[i]));
            }
            output.WriteLine();
        }
    }

    private static void PrintCsv(string[] columnNames, List<object?[]> rows, TextWriter output)
    {
        output.WriteLine(string.Join(",", columnNames.Select(CsvEscape)));
        foreach (var row in rows)
        {
            output.WriteLine(string.Join(",",
                Enumerable.Range(0, columnNames.Length)
                    .Select(i => i < row.Length ? CsvEscape(FormatValue(row[i])) : "")));
        }
    }

    private static void PrintJson(string[] columnNames, List<object?[]> rows, TextWriter output)
    {
        output.WriteLine("[");
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            output.Write("  {");
            for (int i = 0; i < columnNames.Length; i++)
            {
                if (i > 0) output.Write(", ");
                output.Write($"\"{JsonEscape(columnNames[i])}\": ");
                object? val = i < row.Length ? row[i] : null;
                output.Write(JsonValue(val));
            }
            output.Write("}");
            if (r < rows.Count - 1) output.Write(",");
            output.WriteLine();
        }
        output.WriteLine("]");
    }

    private static void PrintLine(string[] columnNames, List<object?[]> rows, TextWriter output)
    {
        int maxNameLen = columnNames.Max(n => n.Length);
        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) output.WriteLine();
            var row = rows[r];
            for (int i = 0; i < columnNames.Length; i++)
            {
                string val = i < row.Length ? FormatValue(row[i]) : "";
                output.WriteLine($"{columnNames[i].PadRight(maxNameLen)} = {val}");
            }
        }
    }

    public static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        byte[] blob => $"X'{Convert.ToHexString(blob)}'",
        string s => s,
        long l => l.ToString(),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string JsonValue(object? val) => val switch
    {
        null => "null",
        long l => l.ToString(),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => $"\"{JsonEscape(s)}\"",
        byte[] blob => $"\"{Convert.ToHexString(blob)}\"",
        _ => $"\"{JsonEscape(val.ToString() ?? "")}\"",
    };
}
