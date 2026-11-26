namespace ChipSharp.Ir;

/// <summary>The kinds of TIR types. Later milestones add pointers, structs and arrays.</summary>
public enum TirTypeKind
{
    Void,
    Bool,
    I8,
    I16,
    I32,
    I64,
    F32,
    F64,
    NativeInt,
}

/// <summary>
/// The type of a TIR value. Integer types are signless, as in LLVM: i32 holds C#'s <c>int</c> and <c>uint</c>
/// alike, and each operation says whether it reads its operands as signed or unsigned
/// (e.g. <see cref="TirBinaryOp.Div"/> and <see cref="TirBinaryOp.DivUnsigned"/>).
/// Every type is a single shared instance, so types compare with <c>==</c>.
/// </summary>
public sealed class TirType
{
    private TirType(TirTypeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>No value: the return type of a function that returns nothing.</summary>
    public static TirType Void { get; } = new(TirTypeKind.Void, "void");

    /// <summary>True or false. Comparisons produce it and branches test it.</summary>
    public static TirType Bool { get; } = new(TirTypeKind.Bool, "bool");

    /// <summary>8 bits: C#'s <c>sbyte</c> and <c>byte</c>.</summary>
    public static TirType I8 { get; } = new(TirTypeKind.I8, "i8");

    /// <summary>16 bits: C#'s <c>short</c>, <c>ushort</c> and <c>char</c>.</summary>
    public static TirType I16 { get; } = new(TirTypeKind.I16, "i16");

    /// <summary>32 bits on every target: C#'s <c>int</c> and <c>uint</c>.</summary>
    public static TirType I32 { get; } = new(TirTypeKind.I32, "i32");

    /// <summary>64 bits: C#'s <c>long</c> and <c>ulong</c>.</summary>
    public static TirType I64 { get; } = new(TirTypeKind.I64, "i64");

    /// <summary>C#'s <c>float</c>.</summary>
    public static TirType F32 { get; } = new(TirTypeKind.F32, "f32");

    /// <summary>C#'s <c>double</c>.</summary>
    public static TirType F64 { get; } = new(TirTypeKind.F64, "f64");

    /// <summary>
    /// C#'s <c>nint</c> and <c>nuint</c>: as wide as a pointer. The code generator takes the width from the
    /// target (<c>TargetDescription.PointerSize</c>); TIR does not know it.
    /// </summary>
    public static TirType NativeInt { get; } = new(TirTypeKind.NativeInt, "nint");

    public TirTypeKind Kind { get; }

    /// <summary>The name the printer writes, e.g. "i32" or "nint".</summary>
    public string Name { get; }

    /// <summary>True for i8, i16, i32, i64 and nint (not bool).</summary>
    public bool IsInteger => Kind is TirTypeKind.I8 or TirTypeKind.I16 or TirTypeKind.I32 or TirTypeKind.I64
        or TirTypeKind.NativeInt;

    /// <summary>True for f32 and f64.</summary>
    public bool IsFloat => Kind is TirTypeKind.F32 or TirTypeKind.F64;

    public override string ToString() => Name;
}
