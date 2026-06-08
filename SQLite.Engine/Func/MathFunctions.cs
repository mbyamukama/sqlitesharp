using SQLite.Engine.Vdbe;

namespace SQLite.Engine.Func;

/// <summary>
/// Math scalar functions matching SQLite's math extension:
/// acos, acosh, asin, asinh, atan, atan2, atanh, ceil/ceiling,
/// cos, cosh, degrees, exp, floor, ln, log, log2, log10,
/// mod, pi, pow/power, radians, sign, sin, sinh, sqrt, tan, tanh, trunc.
/// </summary>
public static class MathFunctions
{
    /// <summary>
    /// Execute a math function. Returns true if the function was recognized, false otherwise.
    /// </summary>
    public static bool Execute(string name, Mem[] registers, int destReg, int firstArg, int argCount)
    {
        switch (name)
        {
            case "acos":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Acos);
            case "acosh":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Acosh);
            case "asin":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Asin);
            case "asinh":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Asinh);
            case "atan":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Atan);
            case "atanh":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Atanh);
            case "atan2":
                return BinaryReal(registers, destReg, firstArg, argCount, Math.Atan2);
            case "ceil":
            case "ceiling":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Ceiling);
            case "cos":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Cos);
            case "cosh":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Cosh);
            case "degrees":
                return UnaryReal(registers, destReg, firstArg, argCount, r => r * (180.0 / Math.PI));
            case "exp":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Exp);
            case "floor":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Floor);
            case "ln":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Log);
            case "log":
                if (argCount == 1)
                    return UnaryReal(registers, destReg, firstArg, argCount, Math.Log10);
                else if (argCount == 2)
                    return BinaryReal(registers, destReg, firstArg, argCount, (b, x) => Math.Log(x, b));
                registers[destReg].SetNull();
                return true;
            case "log2":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Log2);
            case "log10":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Log10);
            case "mod":
                return BinaryReal(registers, destReg, firstArg, argCount, (a, b) => b != 0 ? a % b : double.NaN);
            case "pi":
                registers[destReg].SetDouble(Math.PI);
                return true;
            case "pow":
            case "power":
                return BinaryReal(registers, destReg, firstArg, argCount, Math.Pow);
            case "radians":
                return UnaryReal(registers, destReg, firstArg, argCount, r => r * (Math.PI / 180.0));
            case "sign":
                if (argCount < 1) { registers[destReg].SetNull(); return true; }
                var sv = registers[firstArg];
                if (sv.Type == MemType.Null) { registers[destReg].SetNull(); return true; }
                registers[destReg].SetInt(Math.Sign(sv.ToDouble()));
                return true;
            case "sin":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Sin);
            case "sinh":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Sinh);
            case "sqrt":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Sqrt);
            case "tan":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Tan);
            case "tanh":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Tanh);
            case "trunc":
                return UnaryReal(registers, destReg, firstArg, argCount, Math.Truncate);
            default:
                return false;
        }
    }

    private static bool UnaryReal(Mem[] registers, int destReg, int firstArg, int argCount, Func<double, double> op)
    {
        if (argCount < 1) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }
        double result = op(v.ToDouble());
        if (double.IsNaN(result) || double.IsInfinity(result))
            registers[destReg].SetNull();
        else
            registers[destReg].SetDouble(result);
        return true;
    }

    private static bool BinaryReal(Mem[] registers, int destReg, int firstArg, int argCount, Func<double, double, double> op)
    {
        if (argCount < 2) { registers[destReg].SetNull(); return true; }
        var a = registers[firstArg];
        var b = registers[firstArg + 1];
        if (a.Type == MemType.Null || b.Type == MemType.Null) { registers[destReg].SetNull(); return true; }
        double result = op(a.ToDouble(), b.ToDouble());
        if (double.IsNaN(result) || double.IsInfinity(result))
            registers[destReg].SetNull();
        else
            registers[destReg].SetDouble(result);
        return true;
    }
}
