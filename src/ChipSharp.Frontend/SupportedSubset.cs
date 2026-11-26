using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// What milestone M0 compiles: static methods over whole numbers and bools, with branches, loops, switch and calls,
/// printing through <see cref="KnownExternals"/>. The whole-program pass rejects anything else with a clear message;
/// the IL-to-TIR translation handles exactly this set. Later milestones grow it.
/// </summary>
public static class SupportedSubset
{
    /// <summary>The milestone this subset belongs to, for messages ("... arrives after M0").</summary>
    public const string Milestone = "M0";

    /// <summary>The IL instructions M0 compiles. Checked arithmetic (<c>*.ovf</c>), objects, arrays (but those of
    /// <see cref="StackArrays"/>), strings, fields, floats, addresses and exceptions are not in it.</summary>
    public static FrozenSet<IlOpCode> OpCodes { get; } = new[]
    {
        IlOpCode.Nop,
        IlOpCode.Ldarg_0, IlOpCode.Ldarg_1, IlOpCode.Ldarg_2, IlOpCode.Ldarg_3, IlOpCode.Ldarg_S, IlOpCode.Ldarg,
        IlOpCode.Starg_S, IlOpCode.Starg,
        IlOpCode.Ldloc_0, IlOpCode.Ldloc_1, IlOpCode.Ldloc_2, IlOpCode.Ldloc_3, IlOpCode.Ldloc_S, IlOpCode.Ldloc,
        IlOpCode.Stloc_0, IlOpCode.Stloc_1, IlOpCode.Stloc_2, IlOpCode.Stloc_3, IlOpCode.Stloc_S, IlOpCode.Stloc,
        IlOpCode.Ldc_I4_M1, IlOpCode.Ldc_I4_0, IlOpCode.Ldc_I4_1, IlOpCode.Ldc_I4_2, IlOpCode.Ldc_I4_3,
        IlOpCode.Ldc_I4_4, IlOpCode.Ldc_I4_5, IlOpCode.Ldc_I4_6, IlOpCode.Ldc_I4_7, IlOpCode.Ldc_I4_8,
        IlOpCode.Ldc_I4_S, IlOpCode.Ldc_I4, IlOpCode.Ldc_I8,
        IlOpCode.Dup, IlOpCode.Pop, IlOpCode.Call, IlOpCode.Ret,
        IlOpCode.Br_S, IlOpCode.Brfalse_S, IlOpCode.Brtrue_S, IlOpCode.Beq_S, IlOpCode.Bge_S, IlOpCode.Bgt_S,
        IlOpCode.Ble_S, IlOpCode.Blt_S, IlOpCode.Bne_Un_S, IlOpCode.Bge_Un_S, IlOpCode.Bgt_Un_S, IlOpCode.Ble_Un_S,
        IlOpCode.Blt_Un_S,
        IlOpCode.Br, IlOpCode.Brfalse, IlOpCode.Brtrue, IlOpCode.Beq, IlOpCode.Bge, IlOpCode.Bgt, IlOpCode.Ble,
        IlOpCode.Blt, IlOpCode.Bne_Un, IlOpCode.Bge_Un, IlOpCode.Bgt_Un, IlOpCode.Ble_Un, IlOpCode.Blt_Un,
        IlOpCode.Switch,
        IlOpCode.Add, IlOpCode.Sub, IlOpCode.Mul, IlOpCode.Div, IlOpCode.Div_Un, IlOpCode.Rem, IlOpCode.Rem_Un,
        IlOpCode.And, IlOpCode.Or, IlOpCode.Xor, IlOpCode.Shl, IlOpCode.Shr, IlOpCode.Shr_Un, IlOpCode.Neg, IlOpCode.Not,
        IlOpCode.Conv_I1, IlOpCode.Conv_I2, IlOpCode.Conv_I4, IlOpCode.Conv_I8, IlOpCode.Conv_I,
        IlOpCode.Conv_U1, IlOpCode.Conv_U2, IlOpCode.Conv_U4, IlOpCode.Conv_U8, IlOpCode.Conv_U,
        IlOpCode.Ceq, IlOpCode.Cgt, IlOpCode.Cgt_Un, IlOpCode.Clt, IlOpCode.Clt_Un,

        // arrays on the stack: see StackArrays, whose rule the whole-program pass checks for each of these
        IlOpCode.Newarr, IlOpCode.Ldlen, IlOpCode.Ldelema,
        IlOpCode.Ldelem_I1, IlOpCode.Ldelem_U1, IlOpCode.Ldelem_I2, IlOpCode.Ldelem_U2, IlOpCode.Ldelem_I4,
        IlOpCode.Ldelem_U4, IlOpCode.Ldelem_I8, IlOpCode.Ldelem_I, IlOpCode.Ldelem,
        IlOpCode.Stelem_I1, IlOpCode.Stelem_I2, IlOpCode.Stelem_I4, IlOpCode.Stelem_I8, IlOpCode.Stelem_I,
        IlOpCode.Stelem,
        IlOpCode.Ldind_I1, IlOpCode.Ldind_U1, IlOpCode.Ldind_I2, IlOpCode.Ldind_U2, IlOpCode.Ldind_I4,
        IlOpCode.Ldind_U4, IlOpCode.Ldind_I8, IlOpCode.Ldind_I,
        IlOpCode.Stind_I1, IlOpCode.Stind_I2, IlOpCode.Stind_I4, IlOpCode.Stind_I8, IlOpCode.Stind_I,
        IlOpCode.Conv_Ovf_I, IlOpCode.Conv_Ovf_I_Un, IlOpCode.Ldtoken,
    }.ToFrozenSet();

    /// <summary>
    /// The TIR type for a parameter, local or return type M0 supports: bool, the whole numbers of every width
    /// (TIR types carry no sign; signed and unsigned share one), nint/nuint, and void. Anything else is unsupported.
    /// </summary>
    public static bool TryMapType(IlType type, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        tirType = type is IlPrimitiveType primitive
            ? primitive.Kind switch
            {
                IlPrimitiveKind.Void => TirType.Void,
                IlPrimitiveKind.Bool => TirType.Bool,
                IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 => TirType.I8,
                IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16 => TirType.I16,
                IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32 => TirType.I32,
                IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 => TirType.I64,
                IlPrimitiveKind.NativeInt or IlPrimitiveKind.NativeUInt => TirType.NativeInt,
                _ => null,
            }
            : null;
        return tirType is not null;
    }
}
