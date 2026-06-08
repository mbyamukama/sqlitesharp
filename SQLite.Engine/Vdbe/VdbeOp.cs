namespace SQLite.Engine.Vdbe;

/// <summary>
/// A single VDBE instruction.
/// P1, P2, P3 are integer operands; P4 is an optional object operand (string, double, etc.).
/// </summary>
public sealed class VdbeOp
{
    public OpCode Opcode { get; init; }
    public int P1 { get; init; }
    public int P2 { get; init; }
    public int P3 { get; init; }
    public object? P4 { get; init; }

    public override string ToString()
    {
        var p4Str = P4 != null ? $" P4={P4}" : "";
        return $"{Opcode,-14} P1={P1} P2={P2} P3={P3}{p4Str}";
    }
}
