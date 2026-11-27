using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// <c>Nullable&lt;T&gt;</c> (C#'s <c>int?</c>), with no heap: a struct of two fields, as .NET lays it out, its
/// <c>hasValue</c> (a bool) and its <c>value</c> (a T), one TIR struct type per T, named as C# writes it (<c>int?</c>).
/// ChipSharp compiles its members itself, where the IL calls them (see Lowering/MethodLowering.Nullable.cs): C# turns
/// <c>x + y</c>, <c>x == 5</c>, <c>x ?? 0</c>, <c>x is int n</c> and the rest into calls of <c>HasValue</c>,
/// <c>GetValueOrDefault</c> and its constructor, so the program's own code is all there is. A nullable is a value like
/// any struct: in locals, parameters, return values, fields, arrays and static fields, of any value type T (a number,
/// a bool, a char, an enum, a struct); boxed, it is null or a boxed T (see <see cref="Boxing"/>).
/// </summary>
internal static class Nullables
{
    /// <summary>The struct of <c>T?</c>, by T's TIR type (a struct's is its own) and its C# name.</summary>
    private static readonly ConditionalWeakTable<TirType, ConcurrentDictionary<string, TirStructType>> Structs = new();

    /// <summary>The field of a nullable's struct that says whether it has a value (a bool).</summary>
    public const int HasValueField = 0;

    /// <summary>The field of a nullable's struct that holds the value (zero when there is none).</summary>
    public const int ValueField = 1;

    /// <summary>T, when <paramref name="type"/> is <c>Nullable&lt;T&gt;</c> (modifiers looked through); null for any other type.</summary>
    public static IlType? ValueOf(IlType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ValueTypes.WithoutModifiers(type) is IlGenericInstanceType
        {
            Definition: IlNamedType
            {
                Namespace: "System", Name: "Nullable`1", DeclaringType: null, IsDefinedInThisAssembly: false,
            },
            Arguments: [var value],
        }
            ? StackArrays.Primitive(ValueTypes.WithoutModifiers(value))
            : null;
    }

    /// <summary>True for <c>Nullable&lt;T&gt;</c>.</summary>
    public static bool IsNullable(IlType type) => ValueOf(type) is not null;

    /// <summary>
    /// The TIR struct of <paramref name="type"/> when it is <c>T?</c> of a value type T ChipSharp compiles (not a
    /// reference, a span or a nullable itself); false for any other type.
    /// </summary>
    public static bool TryMapType(IlType type, IlAssembly assembly, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        tirType = null;
        if (ValueOf(type) is not { } value || IsNullable(value) || Spans.IsSpan(value, out _, out _)
            || !ValueTypes.TryMapType(value, assembly, out var valueType)
            || valueType == TirType.Void || valueType == TirType.Ptr || valueType == StackArrays.ArrayType)
        {
            return false;
        }

        tirType = StructOf(valueType, CSharpNames.Type(value));
        return true;
    }

    /// <summary>The struct of a nullable whose value is of TIR type <paramref name="valueType"/>, named <paramref name="name"/>?.</summary>
    public static TirStructType StructOf(TirType valueType, string name) => Structs.GetOrCreateValue(valueType)
        .GetOrAdd(name, _ => new TirStructType($"{name}?", ("hasValue", TirType.Bool), ("value", valueType)));

    /// <summary>The member of <c>Nullable&lt;T&gt;</c> that <paramref name="callee"/> names, when ChipSharp compiles it; null otherwise.</summary>
    public static NullableMember? MemberOf(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        if (callee.Definition is not null || callee.GenericArguments.Count > 0 || ValueOf(callee.DeclaringType) is null)
        {
            return null;
        }

        return (callee.Name, callee.HasThis, callee.ParameterTypes) switch
        {
            (".ctor", true, [_]) => NullableMember.Constructor,
            ("get_HasValue", true, []) => NullableMember.HasValue,
            ("get_Value", true, []) => NullableMember.Value,
            ("GetValueOrDefault", true, []) => NullableMember.GetValueOrDefault,
            ("GetValueOrDefault", true, [_]) => NullableMember.GetValueOrDefaultOr,
            ("ToString", true, []) => NullableMember.Text,
            ("GetHashCode", true, []) => NullableMember.HashCode,
            ("Equals", true, [IlPrimitiveType { Kind: IlPrimitiveKind.Object }]) => NullableMember.EqualsObject,
            ("op_Implicit", false, [_]) => NullableMember.Implicit,
            ("op_Explicit", false, [_]) => NullableMember.Explicit,
            _ => null,
        };
    }
}

/// <summary>The members of <c>Nullable&lt;T&gt;</c> that ChipSharp compiles (see <see cref="Nullables"/>).</summary>
internal enum NullableMember
{
    /// <summary><c>new T?(value)</c>: a nullable with the value.</summary>
    Constructor,

    HasValue,

    /// <summary>The value, or InvalidOperationException's message ("Nullable object must have a value.") when there is none.</summary>
    Value,

    /// <summary>The value, or zero when there is none.</summary>
    GetValueOrDefault,

    /// <summary><c>GetValueOrDefault(other)</c>: the value, or <c>other</c> when there is none.</summary>
    GetValueOrDefaultOr,

    /// <summary>ToString: the value's text, or "" when there is none.</summary>
    Text,

    /// <summary>GetHashCode: the value's hash code, or 0 when there is none.</summary>
    HashCode,

    /// <summary><c>Equals(object)</c>: with no value, whether the other is null; else whether the value equals it.</summary>
    EqualsObject,

    /// <summary><c>(T?)value</c>.</summary>
    Implicit,

    /// <summary><c>(T)nullable</c>: its Value.</summary>
    Explicit,
}
