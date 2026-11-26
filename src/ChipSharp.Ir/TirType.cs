using System.Runtime.CompilerServices;

namespace ChipSharp.Ir;

/// <summary>The kinds of TIR types.</summary>
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

    /// <summary>An address: <see cref="TirType.Ptr"/>.</summary>
    Ptr,

    /// <summary>A <see cref="TirStructType"/>.</summary>
    Struct,

    /// <summary>A <see cref="TirArrayType"/>.</summary>
    Array,
}

/// <summary>
/// The type of a TIR value. Integer types are signless, as in LLVM: i32 holds C#'s <c>int</c> and <c>uint</c>
/// alike, and each operation says whether it reads its operands as signed or unsigned
/// (e.g. <see cref="TirBinaryOp.Div"/> and <see cref="TirBinaryOp.DivUnsigned"/>).
/// The scalar types and ptr are single shared instances; a <see cref="TirStructType"/> is equal only to itself
/// and a <see cref="TirArrayType"/> to any array of the same element type and count. <c>==</c> compares them
/// that way.
/// </summary>
public class TirType : IEquatable<TirType>
{
    private protected TirType(TirTypeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>No value: the return type of a function that returns nothing.</summary>
    public static TirType Void { get; } = new(TirTypeKind.Void, "void");

    /// <summary>True or false. Comparisons produce it and branches test it. It takes one byte in memory.</summary>
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

    /// <summary>
    /// An address in memory, as wide as a pointer on the target (like LLVM's <c>ptr</c>). It does not say what it
    /// points to: each load and store names the type it reads or writes. Made by <see cref="TirSlotAddress"/>,
    /// <see cref="TirGlobalAddress"/>, <see cref="TirFieldAddress"/>, <see cref="TirElementAddress"/> and
    /// <see cref="TirIntToPtr"/>; null is <see cref="TirZero"/>. There is no arithmetic on it; it can be compared
    /// (eq, ne and the unsigned comparisons).
    /// </summary>
    public static TirType Ptr { get; } = new(TirTypeKind.Ptr, "ptr");

    public TirTypeKind Kind { get; }

    /// <summary>The name the printer writes, e.g. "i32", "nint", "Program.Point" or "[4 x i32]".</summary>
    public string Name { get; }

    /// <summary>True for i8, i16, i32, i64 and nint (not bool, not ptr).</summary>
    public bool IsInteger => Kind is TirTypeKind.I8 or TirTypeKind.I16 or TirTypeKind.I32 or TirTypeKind.I64
        or TirTypeKind.NativeInt;

    /// <summary>True for f32 and f64.</summary>
    public bool IsFloat => Kind is TirTypeKind.F32 or TirTypeKind.F64;

    /// <summary>True for structs and arrays: values that live in memory and that no arithmetic works on.</summary>
    public bool IsAggregate => Kind is TirTypeKind.Struct or TirTypeKind.Array;

    public static bool operator ==(TirType? left, TirType? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(TirType? left, TirType? right) => !(left == right);

    /// <summary>The same type: the same instance, or for arrays the same element type and count.</summary>
    public virtual bool Equals(TirType? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => obj is TirType other && Equals(other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Name;
}

/// <summary>
/// A struct: a name and an ordered list of named fields of any type but void (structs and arrays too).
/// <see cref="TirLayout"/> places the fields in memory. Struct types are nominal: each one made is a type of its
/// own, equal only to itself, even when another has the same name and fields, so make one per C# struct and use it
/// everywhere (the verifier reports two different structs of one name in a module). Printed by its name;
/// <see cref="TirPrinter"/> lists the fields of every struct a module uses at the top of the module.
/// </summary>
public sealed class TirStructType : TirType
{
    /// <summary>A struct with at least one field; field names are unique within it.</summary>
    /// <param name="name">The C# display name, e.g. "Program.Point".</param>
    /// <param name="fields">Name and type of each field, in order, e.g. <c>("X", TirType.I32)</c>.</param>
    public TirStructType(string name, params IEnumerable<(string Name, TirType Type)> fields)
        : base(TirTypeKind.Struct, CheckedName(name))
    {
        ArgumentNullException.ThrowIfNull(fields);

        var list = new List<TirStructField>();
        foreach (var (fieldName, type) in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
            ArgumentNullException.ThrowIfNull(type);
            if (type == Void)
            {
                throw new TirException($"Struct '{name}': field {fieldName} cannot have type void.");
            }

            if (list.Any(f => f.Name == fieldName))
            {
                throw new TirException($"Struct '{name}' has two fields named {fieldName}.");
            }

            list.Add(new TirStructField(list.Count, fieldName, type));
        }

        if (list.Count == 0)
        {
            throw new TirException($"Struct '{name}' has no fields; a struct needs at least one " +
                "(C# gives an empty struct one byte: give it one i8 field).");
        }

        Fields = list;
    }

    public IReadOnlyList<TirStructField> Fields { get; }

    /// <summary>The field named <paramref name="name"/>, or null when there is none.</summary>
    public TirStructField? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    private static string CheckedName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}

/// <summary>One field of a <see cref="TirStructType"/>.</summary>
public sealed class TirStructField
{
    internal TirStructField(int index, string name, TirType type)
    {
        Index = index;
        Name = name;
        Type = type;
    }

    /// <summary>Position in <see cref="TirStructType.Fields"/>, from 0: what <see cref="TirFieldAddress"/> takes.</summary>
    public int Index { get; }

    public string Name { get; }

    public TirType Type { get; }

    public override string ToString() => $"{Type} {Name}";
}

/// <summary>
/// A fixed number of elements of one type, one after another in memory, printed <c>[4 x i32]</c>
/// (C#'s fixed buffers and arrays of a size known when compiling). Two array types of the same element type and
/// count are the same type.
/// </summary>
public sealed class TirArrayType : TirType
{
    /// <summary>An array of <paramref name="count"/> (at least one) elements of type <paramref name="element"/> (not void).</summary>
    public TirArrayType(TirType element, int count) : base(TirTypeKind.Array, CheckedName(element, count))
    {
        Element = element;
        Count = count;
    }

    public TirType Element { get; }

    public int Count { get; }

    public override bool Equals(TirType? other) =>
        other is TirArrayType array && array.Count == Count && array.Element == Element;

    public override int GetHashCode() => HashCode.Combine(Element, Count);

    private static string CheckedName(TirType element, int count)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element == Void)
        {
            throw new TirException("An array cannot have elements of type void.");
        }

        if (count < 1)
        {
            throw new TirException($"An array of {element} needs at least one element, not {count}.");
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{count} x {element}]");
    }
}
