using SQLite.Engine;

namespace SQLite.Engine.Tests;

/// <summary>
/// Tests for Phase 8 — Math and JSON extension functions.
/// </summary>
public class FunctionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public FunctionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_cs_func_{Guid.NewGuid():N}.db");
        DatabaseFactory.CreateNew(_dbPath);
        _db = new Database(_dbPath);
        _db.Execute("CREATE TABLE t (x REAL, y REAL, name TEXT)");
        _db.Execute("INSERT INTO t VALUES (1.0, 2.0, 'alice')");
        _db.Execute("INSERT INTO t VALUES (0.0, 0.0, 'bob')");
        _db.Execute("INSERT INTO t VALUES (-1.0, 3.0, 'carol')");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ─── Math Functions ─────────────────────────────────────────────────────

    [Fact]
    public void Math_Pi()
    {
        var rows = _db.Execute("SELECT pi()");
        Assert.Single(rows);
        Assert.Equal(Math.PI, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Sqrt()
    {
        var rows = _db.Execute("SELECT sqrt(16.0)");
        Assert.Equal(4.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Sqrt_Negative_ReturnsNull()
    {
        var rows = _db.Execute("SELECT sqrt(-1.0)");
        Assert.Null(rows[0][0]);
    }

    [Fact]
    public void Math_Sin_Cos()
    {
        var rows = _db.Execute("SELECT sin(0.0), cos(0.0)");
        Assert.Equal(0.0, (double)rows[0][0]!, 10);
        Assert.Equal(1.0, (double)rows[0][1]!, 10);
    }

    [Fact]
    public void Math_Tan()
    {
        var rows = _db.Execute("SELECT tan(0.0)");
        Assert.Equal(0.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Asin_Acos_Atan()
    {
        var rows = _db.Execute("SELECT asin(1.0), acos(1.0), atan(1.0)");
        Assert.Equal(Math.PI / 2, (double)rows[0][0]!, 10);
        Assert.Equal(0.0, (double)rows[0][1]!, 10);
        Assert.Equal(Math.PI / 4, (double)rows[0][2]!, 10);
    }

    [Fact]
    public void Math_Atan2()
    {
        var rows = _db.Execute("SELECT atan2(1.0, 1.0)");
        Assert.Equal(Math.PI / 4, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Exp_Ln()
    {
        var rows = _db.Execute("SELECT exp(1.0), ln(1.0)");
        Assert.Equal(Math.E, (double)rows[0][0]!, 10);
        Assert.Equal(0.0, (double)rows[0][1]!, 10);
    }

    [Fact]
    public void Math_Log_OneArg_IsLog10()
    {
        var rows = _db.Execute("SELECT log(100.0)");
        Assert.Equal(2.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Log_TwoArgs_IsLogBase()
    {
        var rows = _db.Execute("SELECT log(2.0, 8.0)");
        Assert.Equal(3.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Log2()
    {
        var rows = _db.Execute("SELECT log2(8.0)");
        Assert.Equal(3.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Log10()
    {
        var rows = _db.Execute("SELECT log10(1000.0)");
        Assert.Equal(3.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Ceil_Floor_Trunc()
    {
        var rows = _db.Execute("SELECT ceil(2.3), floor(2.7), trunc(2.9)");
        Assert.Equal(3.0, (double)rows[0][0]!, 10);
        Assert.Equal(2.0, (double)rows[0][1]!, 10);
        Assert.Equal(2.0, (double)rows[0][2]!, 10);
    }

    [Fact]
    public void Math_Ceil_Negative()
    {
        var rows = _db.Execute("SELECT ceil(-2.3)");
        Assert.Equal(-2.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Pow()
    {
        var rows = _db.Execute("SELECT pow(2.0, 10.0)");
        Assert.Equal(1024.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Power_Alias()
    {
        var rows = _db.Execute("SELECT power(3.0, 3.0)");
        Assert.Equal(27.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Mod()
    {
        var rows = _db.Execute("SELECT mod(10.0, 3.0)");
        Assert.Equal(1.0, (double)rows[0][0]!, 10);
    }

    [Fact]
    public void Math_Sign()
    {
        var rows = _db.Execute("SELECT sign(-5.0), sign(0.0), sign(3.0)");
        Assert.Equal(-1L, rows[0][0]);
        Assert.Equal(0L, rows[0][1]);
        Assert.Equal(1L, rows[0][2]);
    }

    [Fact]
    public void Math_Degrees_Radians()
    {
        var rows = _db.Execute("SELECT degrees(pi()), radians(180.0)");
        Assert.Equal(180.0, (double)rows[0][0]!, 10);
        Assert.Equal(Math.PI, (double)rows[0][1]!, 10);
    }

    [Fact]
    public void Math_NullPropagation()
    {
        _db.Execute("CREATE TABLE n (v REAL)");
        _db.Execute("INSERT INTO n VALUES (NULL)");
        var rows = _db.Execute("SELECT sqrt(v), sin(v), log(v) FROM n");
        Assert.Null(rows[0][0]);
        Assert.Null(rows[0][1]);
        Assert.Null(rows[0][2]);
    }

    [Fact]
    public void Math_Sinh_Cosh_Tanh()
    {
        var rows = _db.Execute("SELECT sinh(0.0), cosh(0.0), tanh(0.0)");
        Assert.Equal(0.0, (double)rows[0][0]!, 10);
        Assert.Equal(1.0, (double)rows[0][1]!, 10);
        Assert.Equal(0.0, (double)rows[0][2]!, 10);
    }

    // ─── JSON Functions ─────────────────────────────────────────────────────

    [Fact]
    public void Json_Valid()
    {
        var rows = _db.Execute("SELECT json_valid('{\"a\":1}')");
        Assert.Equal(1L, rows[0][0]);
    }

    [Fact]
    public void Json_Valid_Invalid()
    {
        var rows = _db.Execute("SELECT json_valid('not json')");
        Assert.Equal(0L, rows[0][0]);
    }

    [Fact]
    public void Json_Minify()
    {
        var rows = _db.Execute("SELECT json('{  \"a\" :  1  }')");
        Assert.Equal("{\"a\":1}", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Array()
    {
        var rows = _db.Execute("SELECT json_array(1, 2, 3)");
        Assert.Equal("[1,2,3]", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Array_Mixed()
    {
        var rows = _db.Execute("SELECT json_array(1, 'two', 3.0)");
        string result = (string)rows[0][0]!;
        Assert.Contains("1", result);
        Assert.Contains("\"two\"", result);
        Assert.Contains("3", result);
    }

    [Fact]
    public void Json_Object()
    {
        var rows = _db.Execute("SELECT json_object('name', 'alice', 'age', 30)");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"name\"", result);
        Assert.Contains("\"alice\"", result);
        Assert.Contains("\"age\"", result);
        Assert.Contains("30", result);
    }

    [Fact]
    public void Json_Extract_Simple()
    {
        var rows = _db.Execute("SELECT json_extract('{\"a\":42}', '$.a')");
        Assert.Equal(42L, rows[0][0]);
    }

    [Fact]
    public void Json_Extract_Nested()
    {
        var rows = _db.Execute("SELECT json_extract('{\"x\":{\"y\":99}}', '$.x.y')");
        Assert.Equal(99L, rows[0][0]);
    }

    [Fact]
    public void Json_Extract_Array()
    {
        var rows = _db.Execute("SELECT json_extract('[10,20,30]', '$[1]')");
        Assert.Equal(20L, rows[0][0]);
    }

    [Fact]
    public void Json_Extract_String()
    {
        var rows = _db.Execute("SELECT json_extract('{\"name\":\"bob\"}', '$.name')");
        Assert.Equal("bob", rows[0][0]);
    }

    [Fact]
    public void Json_Array_Length()
    {
        var rows = _db.Execute("SELECT json_array_length('[1,2,3,4,5]')");
        Assert.Equal(5L, rows[0][0]);
    }

    [Fact]
    public void Json_Array_Length_AtPath()
    {
        var rows = _db.Execute("SELECT json_array_length('{\"items\":[1,2,3]}', '$.items')");
        Assert.Equal(3L, rows[0][0]);
    }

    [Fact]
    public void Json_Type_Object()
    {
        var rows = _db.Execute("SELECT json_type('{\"a\":1}')");
        Assert.Equal("object", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Type_Array()
    {
        var rows = _db.Execute("SELECT json_type('[1,2]')");
        Assert.Equal("array", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Type_Integer()
    {
        var rows = _db.Execute("SELECT json_type('{\"x\":42}', '$.x')");
        Assert.Equal("integer", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Type_Text()
    {
        var rows = _db.Execute("SELECT json_type('{\"x\":\"hi\"}', '$.x')");
        Assert.Equal("text", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Insert()
    {
        var rows = _db.Execute("SELECT json_insert('{\"a\":1}', '$.b', 2)");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"a\":1", result);
        Assert.Contains("\"b\":2", result);
    }

    [Fact]
    public void Json_Insert_NoOverwrite()
    {
        var rows = _db.Execute("SELECT json_insert('{\"a\":1}', '$.a', 99)");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"a\":1", result);
        Assert.DoesNotContain("99", result);
    }

    [Fact]
    public void Json_Replace()
    {
        var rows = _db.Execute("SELECT json_replace('{\"a\":1}', '$.a', 99)");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"a\":99", result);
    }

    [Fact]
    public void Json_Replace_NoInsert()
    {
        var rows = _db.Execute("SELECT json_replace('{\"a\":1}', '$.b', 2)");
        string result = (string)rows[0][0]!;
        Assert.DoesNotContain("\"b\"", result);
    }

    [Fact]
    public void Json_Set_Overwrites()
    {
        var rows = _db.Execute("SELECT json_set('{\"a\":1}', '$.a', 99)");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"a\":99", result);
    }

    [Fact]
    public void Json_Set_Inserts()
    {
        var rows = _db.Execute("SELECT json_set('{\"a\":1}', '$.b', 2)");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"b\":2", result);
    }

    [Fact]
    public void Json_Remove()
    {
        var rows = _db.Execute("SELECT json_remove('{\"a\":1,\"b\":2}', '$.a')");
        string result = (string)rows[0][0]!;
        Assert.DoesNotContain("\"a\"", result);
        Assert.Contains("\"b\":2", result);
    }

    [Fact]
    public void Json_Quote_String()
    {
        var rows = _db.Execute("SELECT json_quote('hello')");
        Assert.Equal("\"hello\"", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Quote_Number()
    {
        var rows = _db.Execute("SELECT json_quote(42)");
        Assert.Equal("42", (string)rows[0][0]!);
    }

    [Fact]
    public void Json_Patch()
    {
        var rows = _db.Execute("SELECT json_patch('{\"a\":1,\"b\":2}', '{\"b\":99,\"c\":3}')");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"a\":1", result);
        Assert.Contains("\"b\":99", result);
        Assert.Contains("\"c\":3", result);
    }

    [Fact]
    public void Json_Patch_RemovesNull()
    {
        var rows = _db.Execute("SELECT json_patch('{\"a\":1,\"b\":2}', '{\"b\":null}')");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"a\":1", result);
        Assert.DoesNotContain("\"b\"", result);
    }

    [Fact]
    public void Json_NullPropagation()
    {
        _db.Execute("CREATE TABLE jn (v TEXT)");
        _db.Execute("INSERT INTO jn VALUES (NULL)");
        var rows = _db.Execute("SELECT json_extract(v, '$.a'), json_valid(v) FROM jn");
        Assert.Null(rows[0][0]);
        Assert.Null(rows[0][1]);
    }

    [Fact]
    public void Json_GroupArray()
    {
        _db.Execute("CREATE TABLE ga (val INTEGER)");
        _db.Execute("INSERT INTO ga VALUES (10)");
        _db.Execute("INSERT INTO ga VALUES (20)");
        _db.Execute("INSERT INTO ga VALUES (30)");
        var rows = _db.Execute("SELECT json_group_array(val) FROM ga");
        string result = (string)rows[0][0]!;
        Assert.Contains("10", result);
        Assert.Contains("20", result);
        Assert.Contains("30", result);
    }

    [Fact]
    public void Json_GroupObject()
    {
        _db.Execute("CREATE TABLE go (k TEXT, v INTEGER)");
        _db.Execute("INSERT INTO go VALUES ('alice', 1)");
        _db.Execute("INSERT INTO go VALUES ('bob', 2)");
        _db.Execute("INSERT INTO go VALUES ('carol', 3)");
        var rows = _db.Execute("SELECT json_group_object(k, v) FROM go");
        string result = (string)rows[0][0]!;
        Assert.Contains("\"alice\"", result);
        Assert.Contains("\"bob\"", result);
        Assert.Contains("\"carol\"", result);
    }
}
