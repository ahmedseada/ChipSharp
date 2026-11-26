using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// Generic math on a number, in an instance of generic code (see <see cref="ConstrainedCalls"/>): a static member of
/// one of System.Numerics' interfaces (<c>INumber&lt;T&gt;</c>, <c>IAdditionOperators&lt;T, T, T&gt;</c>, ...), called
/// as <c>constrained. T call</c>, is the IL C# writes for that operation on the number itself, so it computes exactly what
/// .NET's own operator does:
/// <list type="bullet">
/// <item>the operators <c>+ - * / % ++ -- &amp; | ^ ~ &lt;&lt; &gt;&gt; &gt;&gt;&gt;</c> and unary <c>- +</c>, with the
/// result of sbyte, byte, short and ushort cut back to its own bits and a shift by its own width at most, as .NET's
/// operators of these types do; unsigned division, remainder, shifts and comparisons on unsigned types;</item>
/// <item>the comparisons <c>== != &lt; &lt;= &gt; &gt;=</c>, on floats false when either side is NaN (but <c>!=</c>, true);</item>
/// <item>the constants <c>Zero</c>, <c>One</c>, <c>AdditiveIdentity</c>, <c>MultiplicativeIdentity</c>,
/// <c>MinValue</c>, <c>MaxValue</c>, <c>NegativeOne</c> and <c>AllBitsSet</c>;</item>
/// <item>in a checked context (<c>op_CheckedAddition</c>, ...), on a float the same operator (checked changes nothing on
/// floats), and on whole numbers the division (which checks what it checks anyway); the other checked operators of
/// whole numbers stay what they are, which the whole-program pass reports as checked arithmetic.</item>
/// </list>
/// Any other member (<c>INumber&lt;T&gt;.Max</c>, <c>CreateChecked</c>, ...) stays a call the whole-program pass reports
/// as a library method that is not available.
/// </summary>
internal static class GenericMath
{
    /// <summary>The instructions the call of <paramref name="member"/> on a number of kind <paramref name="kind"/> becomes; null when unknown.</summary>
    public static IReadOnlyList<(IlOpCode, IlOperand?)>? Replacement(IlPrimitiveKind kind, IlMethodReference member)
    {
        if (member is not { HasThis: false, Definition: null, GenericArguments.Count: 0 }
            || member.DeclaringType is not IlGenericInstanceType { Definition: IlNamedType { Namespace: "System.Numerics" } }
            || !IsNumber(kind))
        {
            return null;
        }

        var name = member.Name;
        var isFloat = kind is IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64;
        if (name.StartsWith("op_Checked", StringComparison.Ordinal))
        {
            // checked changes nothing on floats; on whole numbers only division is the same, the rest is checked arithmetic
            name = isFloat || (name == "op_CheckedDivision" && Bits(kind) >= 32) ? "op_" + name["op_Checked".Length..] : "";
        }

        var unsigned = IsUnsigned(kind);
        var integer = !isFloat;
        return name switch
        {
            "op_Addition" => [(IlOpCode.Add, null), .. Truncate(kind)],
            "op_Subtraction" => [(IlOpCode.Sub, null), .. Truncate(kind)],
            "op_Multiply" => [(IlOpCode.Mul, null), .. Truncate(kind)],
            "op_Division" => [(unsigned ? IlOpCode.Div_Un : IlOpCode.Div, null), .. Truncate(kind)],
            "op_Modulus" => [(unsigned ? IlOpCode.Rem_Un : IlOpCode.Rem, null), .. Truncate(kind)],
            "op_UnaryNegation" => [(IlOpCode.Neg, null), .. Truncate(kind)],
            "op_UnaryPlus" => [],
            "op_Increment" => [.. Constant(kind, 1), (IlOpCode.Add, null), .. Truncate(kind)],
            "op_Decrement" => [.. Constant(kind, 1), (IlOpCode.Sub, null), .. Truncate(kind)],
            "op_BitwiseAnd" when integer => [(IlOpCode.And, null)],
            "op_BitwiseOr" when integer => [(IlOpCode.Or, null)],
            "op_ExclusiveOr" when integer => [(IlOpCode.Xor, null)],
            "op_OnesComplement" when integer => [(IlOpCode.Not, null), .. Truncate(kind)],
            "op_LeftShift" when Bits(kind) > 0 => [.. ShiftMask(kind), (IlOpCode.Shl, null), .. Truncate(kind)],
            "op_RightShift" when Bits(kind) > 0 =>
                [.. ShiftMask(kind), (unsigned ? IlOpCode.Shr_Un : IlOpCode.Shr, null)],
            "op_UnsignedRightShift" when Bits(kind) >= 32 || (unsigned && Bits(kind) > 0) =>
                [.. ShiftMask(kind), (IlOpCode.Shr_Un, null)],
            "op_Equality" => [(IlOpCode.Ceq, null)],
            "op_Inequality" => [(IlOpCode.Ceq, null), (IlOpCode.Ldc_I4_0, null), (IlOpCode.Ceq, null)],
            "op_LessThan" => [(unsigned ? IlOpCode.Clt_Un : IlOpCode.Clt, null)],
            "op_GreaterThan" => [(unsigned ? IlOpCode.Cgt_Un : IlOpCode.Cgt, null)],

            // not greater: on floats unordered greater (true for NaN), so that NaN gives false, as C# writes it
            "op_LessThanOrEqual" =>
                [(unsigned || isFloat ? IlOpCode.Cgt_Un : IlOpCode.Cgt, null), (IlOpCode.Ldc_I4_0, null), (IlOpCode.Ceq, null)],
            "op_GreaterThanOrEqual" =>
                [(unsigned || isFloat ? IlOpCode.Clt_Un : IlOpCode.Clt, null), (IlOpCode.Ldc_I4_0, null), (IlOpCode.Ceq, null)],
            "get_Zero" or "get_AdditiveIdentity" => Constant(kind, 0),
            "get_One" or "get_MultiplicativeIdentity" => Constant(kind, 1),
            "get_NegativeOne" when !unsigned => Constant(kind, -1),
            "get_AllBitsSet" when integer => AllBitsSet(kind),
            "get_MinValue" => MinValue(kind),
            "get_MaxValue" => MaxValue(kind),
            _ => null,
        };
    }

    /// <summary>True for the numbers generic math works on here: the whole numbers, nint, nuint, float and double.</summary>
    private static bool IsNumber(IlPrimitiveKind kind) => kind is >= IlPrimitiveKind.Int8 and <= IlPrimitiveKind.NativeUInt;

    private static bool IsUnsigned(IlPrimitiveKind kind) => kind is IlPrimitiveKind.UInt8 or IlPrimitiveKind.UInt16
        or IlPrimitiveKind.UInt32 or IlPrimitiveKind.UInt64 or IlPrimitiveKind.NativeUInt;

    /// <summary>The width of a whole number in bits; 0 for nint and nuint (it depends on the target) and the floats.</summary>
    private static int Bits(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 => 8,
        IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16 => 16,
        IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32 => 32,
        IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 => 64,
        _ => 0,
    };

    /// <summary>A small whole number's result cut back to its bits, as its operators do; nothing for the others.</summary>
    private static (IlOpCode, IlOperand?)[] Truncate(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Int8 => [(IlOpCode.Conv_I1, null)],
        IlPrimitiveKind.UInt8 => [(IlOpCode.Conv_U1, null)],
        IlPrimitiveKind.Int16 => [(IlOpCode.Conv_I2, null)],
        IlPrimitiveKind.UInt16 => [(IlOpCode.Conv_U2, null)],
        _ => [],
    };

    /// <summary>The shift amount (on top of the stack) masked to the width, as .NET's shift operators mask it.</summary>
    private static (IlOpCode, IlOperand?)[] ShiftMask(IlPrimitiveKind kind) =>
        [(IlOpCode.Ldc_I4_S, new IlInt32Operand(Bits(kind) - 1)), (IlOpCode.And, null)];

    /// <summary>The constant <paramref name="value"/> as a number of kind <paramref name="kind"/>.</summary>
    private static (IlOpCode, IlOperand?)[] Constant(IlPrimitiveKind kind, int value) => kind switch
    {
        IlPrimitiveKind.Float32 => [(IlOpCode.Ldc_R4, new IlFloat32Operand(value))],
        IlPrimitiveKind.Float64 => [(IlOpCode.Ldc_R8, new IlFloat64Operand(value))],
        IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 => [(IlOpCode.Ldc_I8, new IlInt64Operand(value))],
        IlPrimitiveKind.NativeInt => [(IlOpCode.Ldc_I4, new IlInt32Operand(value)), (IlOpCode.Conv_I, null)],
        IlPrimitiveKind.NativeUInt => [(IlOpCode.Ldc_I4, new IlInt32Operand(value)), (IlOpCode.Conv_U, null)],
        _ => [(IlOpCode.Ldc_I4, new IlInt32Operand(value))],
    };

    private static (IlOpCode, IlOperand?)[] AllBitsSet(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.UInt8 => Constant(kind, byte.MaxValue),
        IlPrimitiveKind.UInt16 => Constant(kind, ushort.MaxValue),
        IlPrimitiveKind.NativeUInt => [(IlOpCode.Ldc_I4_M1, null), (IlOpCode.Conv_I, null)],
        _ => Constant(kind, -1),
    };

    private static (IlOpCode, IlOperand?)[]? MinValue(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Int8 => Constant(kind, sbyte.MinValue),
        IlPrimitiveKind.Int16 => Constant(kind, short.MinValue),
        IlPrimitiveKind.Int32 => Constant(kind, int.MinValue),
        IlPrimitiveKind.Int64 => [(IlOpCode.Ldc_I8, new IlInt64Operand(long.MinValue))],
        IlPrimitiveKind.UInt8 or IlPrimitiveKind.UInt16 or IlPrimitiveKind.UInt32 or IlPrimitiveKind.UInt64
            or IlPrimitiveKind.NativeUInt => Constant(kind, 0),

        // the largest nint (all bits but the top one) with every bit flipped
        IlPrimitiveKind.NativeInt => [.. MaxValue(kind)!, (IlOpCode.Not, null)],
        IlPrimitiveKind.Float32 => [(IlOpCode.Ldc_R4, new IlFloat32Operand(float.MinValue))],
        IlPrimitiveKind.Float64 => [(IlOpCode.Ldc_R8, new IlFloat64Operand(double.MinValue))],
        _ => null,
    };

    private static (IlOpCode, IlOperand?)[]? MaxValue(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Int8 => Constant(kind, sbyte.MaxValue),
        IlPrimitiveKind.UInt8 => Constant(kind, byte.MaxValue),
        IlPrimitiveKind.Int16 => Constant(kind, short.MaxValue),
        IlPrimitiveKind.UInt16 => Constant(kind, ushort.MaxValue),
        IlPrimitiveKind.Int32 => Constant(kind, int.MaxValue),
        IlPrimitiveKind.UInt32 => Constant(kind, -1),
        IlPrimitiveKind.Int64 => [(IlOpCode.Ldc_I8, new IlInt64Operand(long.MaxValue))],
        IlPrimitiveKind.UInt64 => [(IlOpCode.Ldc_I8, new IlInt64Operand(-1))],

        // every bit set, then shifted right once without the sign: as wide as nint is on the target
        IlPrimitiveKind.NativeInt =>
            [(IlOpCode.Ldc_I4_M1, null), (IlOpCode.Conv_I, null), (IlOpCode.Ldc_I4_1, null), (IlOpCode.Shr_Un, null)],
        IlPrimitiveKind.NativeUInt => [(IlOpCode.Ldc_I4_M1, null), (IlOpCode.Conv_I, null)],
        IlPrimitiveKind.Float32 => [(IlOpCode.Ldc_R4, new IlFloat32Operand(float.MaxValue))],
        IlPrimitiveKind.Float64 => [(IlOpCode.Ldc_R8, new IlFloat64Operand(double.MaxValue))],
        _ => null,
    };
}
