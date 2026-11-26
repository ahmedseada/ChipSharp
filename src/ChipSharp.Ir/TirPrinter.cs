using System.Globalization;
using System.Text;

namespace ChipSharp.Ir;

/// <summary>
/// Writes TIR as stable, readable text for tests and dumps. Lines end with "\n" on every platform.
/// <code>
/// function i32 Program.Square(i32 %n)
/// entry:
///   %0 = mul i32 %n, %n
///   return %0
/// </code>
/// Temporaries are numbered %0, %1, ... per function, in block order; parameters print as %name,
/// slots as $name, blocks by name. A value that does not belong to the function prints as %?.
/// </summary>
public static class TirPrinter
{
    public static string Print(TirModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var text = new StringBuilder();
        text.Append("module ").Append(module.Name).Append('\n');
        if (module.EntryPoint is { } entryPoint)
        {
            text.Append("entrypoint ").Append(entryPoint.Name).Append('\n');
        }

        foreach (var function in module.Functions)
        {
            text.Append('\n');
            Write(text, function);
        }

        return text.ToString();
    }

    public static string Print(TirFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        var text = new StringBuilder();
        Write(text, function);
        return text.ToString();
    }

    /// <summary>One instruction without indentation, e.g. "%2 = add i32 %0, %1", numbered as in its function.</summary>
    public static string Print(TirInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return Print(instruction, new TirNumbering(instruction.Block?.Function));
    }

    internal static string Print(TirInstruction instruction, TirNumbering names)
    {
        var body = instruction switch
        {
            TirConst c => $"const {c.Type} {ConstantText(c)}",
            TirBinary b => $"{Name(b.Op)} {b.Type} {names.Of(b.Left)}, {names.Of(b.Right)}",
            TirCompare c => $"{Name(c.Op)} {c.Left.Type} {names.Of(c.Left)}, {names.Of(c.Right)}",
            TirConvert c => $"{(c.IsUnsigned ? "uconvert" : "convert")} {c.Value.Type} {names.Of(c.Value)} to {c.Type}",
            TirLoad l => $"load {l.Type} {l.Slot}",
            TirStore s => $"store {s.Slot}, {names.Of(s.Value)}",
            TirCall c => $"call {c.Type} {c.Callee.Name}({string.Join(", ", c.Arguments.Select(names.Of))})",
            TirReturn { Value: null } => "return",
            TirReturn r => $"return {names.Of(r.Value!)}",
            TirJump j => $"jump {j.Target.Name}",
            TirBranch b => $"branch {names.Of(b.Condition)}, {b.WhenTrue.Name}, {b.WhenFalse.Name}",
            _ => instruction.GetType().Name,
        };

        return instruction.HasResult ? $"{names.Of(instruction)} = {body}" : body;
    }

    internal static string Name(TirBinaryOp op) => op switch
    {
        TirBinaryOp.Add => "add",
        TirBinaryOp.Sub => "sub",
        TirBinaryOp.Mul => "mul",
        TirBinaryOp.Div => "div",
        TirBinaryOp.DivUnsigned => "udiv",
        TirBinaryOp.Rem => "rem",
        TirBinaryOp.RemUnsigned => "urem",
        TirBinaryOp.And => "and",
        TirBinaryOp.Or => "or",
        TirBinaryOp.Xor => "xor",
        TirBinaryOp.Shl => "shl",
        TirBinaryOp.Shr => "shr",
        TirBinaryOp.ShrUnsigned => "ushr",
        _ => op.ToString(),
    };

    internal static string Name(TirCompareOp op) => op switch
    {
        TirCompareOp.Eq => "eq",
        TirCompareOp.Ne => "ne",
        TirCompareOp.Lt => "lt",
        TirCompareOp.Le => "le",
        TirCompareOp.Gt => "gt",
        TirCompareOp.Ge => "ge",
        TirCompareOp.LtUnsigned => "ult",
        TirCompareOp.LeUnsigned => "ule",
        TirCompareOp.GtUnsigned => "ugt",
        TirCompareOp.GeUnsigned => "uge",
        _ => op.ToString(),
    };

    private static void Write(StringBuilder text, TirFunction function)
    {
        var names = new TirNumbering(function);
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Type} {p}"));
        if (function.Kind == TirFunctionKind.External)
        {
            text.Append("external ");
        }

        text.Append($"function {function.ReturnType} {function.Name}({parameters})\n");
        foreach (var slot in function.Slots)
        {
            text.Append($"  {slot}: {slot.Type}\n");
        }

        foreach (var block in function.Blocks)
        {
            text.Append(block.Name).Append(":\n");
            foreach (var instruction in block.Instructions)
            {
                text.Append("  ").Append(Print(instruction, names)).Append('\n');
            }
        }
    }

    private static string ConstantText(TirConst c) => c.Type.Kind switch
    {
        TirTypeKind.Bool => c.IntegerValue != 0 ? "true" : "false",
        TirTypeKind.F32 => ((float)c.FloatValue).ToString("R", CultureInfo.InvariantCulture),
        TirTypeKind.F64 => c.FloatValue.ToString("R", CultureInfo.InvariantCulture),
        _ => c.IntegerValue.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The printed names of one function's values: %0, %1, ... for temporaries in block order, %name for
/// parameters, %? for anything that is not in the function (so broken TIR still prints).
/// </summary>
internal sealed class TirNumbering
{
    private readonly Dictionary<TirInstruction, int> _numbers = [];

    public TirNumbering(TirFunction? function)
    {
        foreach (var instruction in function?.Blocks.SelectMany(b => b.Instructions) ?? [])
        {
            if (instruction.HasResult)
            {
                _numbers[instruction] = _numbers.Count;
            }
        }
    }

    public string Of(TirValue value) => value switch
    {
        TirParameter parameter => parameter.ToString(),
        TirInstruction instruction when _numbers.TryGetValue(instruction, out var number) =>
            string.Create(CultureInfo.InvariantCulture, $"%{number}"),
        _ => "%?",
    };
}
