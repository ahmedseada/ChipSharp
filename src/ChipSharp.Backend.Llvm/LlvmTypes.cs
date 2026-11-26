using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The LLVM type of each TIR type for one target: bool is i1, i8 to i64 stay, f32 is float, f64 is double,
/// and nint is i32 or i64 depending on the target's pointer size.
/// </summary>
internal sealed class LlvmTypes
{
    private readonly int _pointerBits;

    private LlvmTypes(int pointerBits)
    {
        _pointerBits = pointerBits;
    }

    public static LlvmTypes For(TargetDescription target) => target.PointerSize is 4 or 8
        ? new LlvmTypes(target.PointerSize * 8)
        : throw new ChipSharpException(
            $"Target '{target.Name}' has {target.PointerSize}-byte pointers; the llvm code generator supports " +
            "4-byte and 8-byte pointers only.");

    public string Of(TirType type) => type.Kind switch
    {
        TirTypeKind.Void => "void",
        TirTypeKind.F32 => "float",
        TirTypeKind.F64 => "double",
        _ => "i" + Bits(type).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The width in bits: 1 for bool, 32 for i32 and f32, the pointer width for nint.</summary>
    public int Bits(TirType type) => type.Kind switch
    {
        TirTypeKind.Bool => 1,
        TirTypeKind.I8 => 8,
        TirTypeKind.I16 => 16,
        TirTypeKind.I32 or TirTypeKind.F32 => 32,
        TirTypeKind.I64 or TirTypeKind.F64 => 64,
        TirTypeKind.NativeInt => _pointerBits,
        _ => throw new ChipSharpException($"The llvm code generator does not support the TIR type {type} yet."),
    };

    /// <summary>The alignment of a slot holding the type, in bytes: its size (a bool takes one byte).</summary>
    public int Alignment(TirType type) => Math.Max(1, Bits(type) / 8);
}
