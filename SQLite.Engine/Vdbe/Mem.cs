namespace SQLite.Engine.Vdbe;

/// <summary>
/// Type tag for a VDBE memory cell value.
/// </summary>
public enum MemType : byte
{
    Null,
    Int64,
    Double,
    Text,
    Blob,
}

/// <summary>
/// A VDBE register value — the equivalent of sqlite3_value / Mem in the C code.
/// Uses a class with a tag discriminator rather than LayoutKind.Explicit
/// for simplicity and safety in pure managed C#.
/// </summary>
public sealed class Mem
{
    public MemType Type { get; private set; }
    public long IntValue { get; private set; }
    public double RealValue { get; private set; }
    public string? TextValue { get; private set; }
    public byte[]? BlobValue { get; private set; }

    /// <summary>Extra state for aggregate accumulators.</summary>
    public long AggCount { get; set; }
    public object? AggState { get; set; }

    public static Mem MakeNull() => new() { Type = MemType.Null };
    public static Mem MakeInt(long value) => new() { Type = MemType.Int64, IntValue = value };
    public static Mem MakeDouble(double value) => new() { Type = MemType.Double, RealValue = value };
    public static Mem MakeText(string value) => new() { Type = MemType.Text, TextValue = value };
    public static Mem MakeBlob(byte[] value) => new() { Type = MemType.Blob, BlobValue = value };

    public void SetNull() { Type = MemType.Null; IntValue = 0; RealValue = 0; TextValue = null; BlobValue = null; }
    public void SetInt(long v) { Type = MemType.Int64; IntValue = v; }
    public void SetDouble(double v) { Type = MemType.Double; RealValue = v; }
    public void SetText(string v) { Type = MemType.Text; TextValue = v; }
    public void SetBlob(byte[] v) { Type = MemType.Blob; BlobValue = v; }

    /// <summary>
    /// Copy the value from another Mem cell.
    /// </summary>
    public void CopyFrom(Mem other)
    {
        Type = other.Type;
        IntValue = other.IntValue;
        RealValue = other.RealValue;
        TextValue = other.TextValue;
        BlobValue = other.BlobValue;
    }

    /// <summary>
    /// Get this cell's value as a boxed object (for output / comparison).
    /// </summary>
    public object? ToObject() => Type switch
    {
        MemType.Null => null,
        MemType.Int64 => IntValue,
        MemType.Double => RealValue,
        MemType.Text => TextValue,
        MemType.Blob => BlobValue,
        _ => null,
    };

    /// <summary>
    /// Set this cell from a boxed value (as returned by Cell.ReadValue).
    /// </summary>
    public void SetFromObject(object? value)
    {
        switch (value)
        {
            case null:
                SetNull();
                break;
            case long l:
                SetInt(l);
                break;
            case double d:
                SetDouble(d);
                break;
            case string s:
                SetText(s);
                break;
            case byte[] b:
                SetBlob(b);
                break;
            default:
                SetText(value.ToString() ?? "");
                break;
        }
    }

    /// <summary>
    /// Coerce this value to an integer (for comparisons and arithmetic).
    /// </summary>
    public long ToInt64() => Type switch
    {
        MemType.Int64 => IntValue,
        MemType.Double => (long)RealValue,
        MemType.Text => long.TryParse(TextValue, out var v) ? v : 0,
        _ => 0,
    };

    /// <summary>
    /// Coerce this value to a double.
    /// </summary>
    public double ToDouble() => Type switch
    {
        MemType.Double => RealValue,
        MemType.Int64 => IntValue,
        MemType.Text => double.TryParse(TextValue, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0,
        _ => 0.0,
    };

    /// <summary>
    /// Coerce to string for concatenation/display.
    /// </summary>
    public string ToText() => Type switch
    {
        MemType.Text => TextValue ?? "",
        MemType.Int64 => IntValue.ToString(),
        MemType.Double => RealValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MemType.Null => "",
        MemType.Blob => $"BLOB({BlobValue?.Length ?? 0})",
        _ => "",
    };

    /// <summary>
    /// Compare two Mem values using SQLite's type affinity rules.
    /// Returns negative, zero, or positive.
    /// </summary>
    public static int Compare(Mem a, Mem b)
    {
        // NULL sorts first
        if (a.Type == MemType.Null && b.Type == MemType.Null) return 0;
        if (a.Type == MemType.Null) return -1;
        if (b.Type == MemType.Null) return 1;

        // Both numeric
        if ((a.Type == MemType.Int64 || a.Type == MemType.Double) &&
            (b.Type == MemType.Int64 || b.Type == MemType.Double))
        {
            double da = a.ToDouble();
            double db = b.ToDouble();
            return da.CompareTo(db);
        }

        // Both text
        if (a.Type == MemType.Text && b.Type == MemType.Text)
            return string.Compare(a.TextValue, b.TextValue, StringComparison.Ordinal);

        // Both blob
        if (a.Type == MemType.Blob && b.Type == MemType.Blob)
            return CompareBlobs(a.BlobValue, b.BlobValue);

        // Type order: NULL < INT/REAL < TEXT < BLOB
        return TypeOrder(a.Type).CompareTo(TypeOrder(b.Type));
    }

    private static int TypeOrder(MemType t) => t switch
    {
        MemType.Null => 0,
        MemType.Int64 or MemType.Double => 1,
        MemType.Text => 2,
        MemType.Blob => 3,
        _ => 4,
    };

    private static int CompareBlobs(byte[]? a, byte[]? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool IsTrue() => Type switch
    {
        MemType.Null => false,
        MemType.Int64 => IntValue != 0,
        MemType.Double => RealValue != 0.0,
        MemType.Text => !string.IsNullOrEmpty(TextValue),
        MemType.Blob => BlobValue != null && BlobValue.Length > 0,
        _ => false,
    };
}
