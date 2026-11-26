using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// Arrays without a heap: an array made with <c>newarr</c> of a constant length lives on the stack of the method
/// that makes it, so it needs no heap and no garbage collector, as long as it never outlives that method.
/// </summary>
/// <remarks>
/// <para>
/// The rule (checked by the whole-program pass, and again by the IL-to-TIR translation): an array may be kept in
/// locals, indexed, measured (<c>.Length</c>) and passed as an argument to the program's own methods, which run
/// while the method that made it still runs. It may not be returned, stored in a field or in another array, or
/// passed to a library method. A method that takes an array parameter obeys the same rule for it. An array made
/// again in a loop reuses its memory, so the one made there before must no longer be in use.
/// </para>
/// <para>
/// The representation: an array value (on IL's evaluation stack, in a local, in a parameter) is a
/// <see cref="ArrayType"/>, <c>{ ptr data, i32 length }</c>, passed by value. <c>data</c> points to the elements,
/// which live in a slot of <c>[N x T]</c> of the method that made the array (one slot per <c>newarr</c>, zeroed
/// each time it runs); an empty array has a null <c>data</c>. So <c>.Length</c> works on a parameter too, and an
/// element is <c>elementaddr T data, index</c>, after a bounds check against <c>length</c> that stops the program
/// with <see cref="IndexOutOfRange"/> rather than touch memory outside the array.
/// </para>
/// <para>
/// The element types are those of <see cref="SupportedSubset.TryMapType"/> but void. A bool element takes one byte
/// (an i8 holding 0 or 1), as IL's <c>ldelem.u1</c> and <c>stelem.i1</c> read and write it.
/// </para>
/// <para>
/// The instructions (in <see cref="SupportedSubset.OpCodes"/>): newarr, ldlen, ldelem, stelem and ldelema in their
/// integer forms; ldind and stind on an element's address (C# writes <c>a[i] += 1</c> through ldelema); conv.ovf.i
/// and conv.ovf.i.un only as the conversion of a long index; ldtoken only for an array initializer's data.
/// </para>
/// </remarks>
public static class StackArrays
{
    /// <summary>
    /// The runtime function an index outside the array calls, <c>void ChipSharp_Runtime_IndexOutOfRange(void)</c>
    /// in C: it says so, as .NET's IndexOutOfRangeException would, and stops the program.
    /// </summary>
    public const string IndexOutOfRange = "ChipSharp.Runtime.IndexOutOfRange";

    /// <summary>An array value: where its elements start, and how many there are.</summary>
    public static TirStructType ArrayType { get; } =
        new("ChipSharp.Array", ("data", TirType.Ptr), ("length", TirType.I32));

    /// <summary>The field of <see cref="ArrayType"/> that points to the elements.</summary>
    public const int DataField = 0;

    /// <summary>The field of <see cref="ArrayType"/> with the number of elements.</summary>
    public const int LengthField = 1;

    /// <summary>The TIR type of a parameter or local of type <paramref name="type"/>: <see cref="ArrayType"/> for an
    /// array of an element type arrays support.</summary>
    public static bool TryMapType(IlType type, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        tirType = type is IlArrayType array && TryMapElementType(array.ElementType, out _) ? ArrayType : null;
        return tirType is not null;
    }

    /// <summary>
    /// The TIR type an element of type <paramref name="elementType"/> has in memory: the type
    /// <see cref="SupportedSubset.TryMapType"/> gives it, except that a bool is an i8. Not void.
    /// </summary>
    public static bool TryMapElementType(IlType elementType, [NotNullWhen(true)] out TirType? storage)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        storage = SupportedSubset.TryMapType(Primitive(elementType), out var type) && type != TirType.Void
            ? type == TirType.Bool ? TirType.I8 : type
            : null;
        return storage is not null;
    }

    /// <summary>
    /// A type as a signature names it: a type token (newarr's, ldelema's) names C#'s int as the class
    /// <c>[System.Runtime]System.Int32</c>, which is <see cref="IlType.Int32"/>; any other type stays as it is.
    /// </summary>
    public static IlType Primitive(IlType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type is IlNamedType { Namespace: "System", DeclaringType: null, IsDefinedInThisAssembly: false } named
            ? named.Name switch
            {
                "Void" => IlType.Void,
                "Boolean" => IlType.Bool,
                "Char" => IlType.Char,
                "SByte" => IlType.Int8,
                "Byte" => IlType.UInt8,
                "Int16" => IlType.Int16,
                "UInt16" => IlType.UInt16,
                "Int32" => IlType.Int32,
                "UInt32" => IlType.UInt32,
                "Int64" => IlType.Int64,
                "UInt64" => IlType.UInt64,
                "Single" => IlType.Float32,
                "Double" => IlType.Float64,
                "IntPtr" => IlType.NativeInt,
                "UIntPtr" => IlType.NativeUInt,
                "Object" => IlType.Object,
                "String" => IlType.String,
                _ => type,
            }
            : type;
    }

    /// <summary>
    /// The element type an element instruction reads or writes, as a signature names it: int8 for ldelem.i1,
    /// stelem.i1, ldind.i1 and stind.i1, uint8 for ldelem.u1 and ldind.u1, ..., the type token of ldelem, stelem,
    /// ldelema and newarr; null for any other instruction.
    /// </summary>
    public static IlType? ElementType(IlInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return instruction.OpCode switch
        {
            IlOpCode.Ldelem_I1 or IlOpCode.Stelem_I1 or IlOpCode.Ldind_I1 or IlOpCode.Stind_I1 => IlType.Int8,
            IlOpCode.Ldelem_U1 or IlOpCode.Ldind_U1 => IlType.UInt8,
            IlOpCode.Ldelem_I2 or IlOpCode.Stelem_I2 or IlOpCode.Ldind_I2 or IlOpCode.Stind_I2 => IlType.Int16,
            IlOpCode.Ldelem_U2 or IlOpCode.Ldind_U2 => IlType.UInt16,
            IlOpCode.Ldelem_I4 or IlOpCode.Stelem_I4 or IlOpCode.Ldind_I4 or IlOpCode.Stind_I4 => IlType.Int32,
            IlOpCode.Ldelem_U4 or IlOpCode.Ldind_U4 => IlType.UInt32,
            IlOpCode.Ldelem_I8 or IlOpCode.Stelem_I8 or IlOpCode.Ldind_I8 or IlOpCode.Stind_I8 => IlType.Int64,
            IlOpCode.Ldelem_I or IlOpCode.Stelem_I or IlOpCode.Ldind_I or IlOpCode.Stind_I => IlType.NativeInt,
            IlOpCode.Ldelem_R4 or IlOpCode.Stelem_R4 or IlOpCode.Ldind_R4 or IlOpCode.Stind_R4 => IlType.Float32,
            IlOpCode.Ldelem_R8 or IlOpCode.Stelem_R8 or IlOpCode.Ldind_R8 or IlOpCode.Stind_R8 => IlType.Float64,
            IlOpCode.Ldelem_Ref or IlOpCode.Stelem_Ref or IlOpCode.Ldind_Ref or IlOpCode.Stind_Ref => IlType.Object,
            IlOpCode.Ldelem or IlOpCode.Stelem or IlOpCode.Ldelema or IlOpCode.Newarr =>
                instruction.Operand is IlTypeOperand { Type: var type } ? Primitive(type) : null,
            _ => null,
        };
    }

    /// <summary>True for <c>RuntimeHelpers.InitializeArray(Array, RuntimeFieldHandle)</c>, which C# calls to fill an
    /// array of constants from data in the assembly.</summary>
    public static bool IsInitializeArray(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method is
        {
            HasThis: false, Name: "InitializeArray", ParameterTypes.Count: 2, GenericArguments.Count: 0,
            DeclaringType: IlNamedType
            {
                Namespace: "System.Runtime.CompilerServices", Name: "RuntimeHelpers", IsDefinedInThisAssembly: false,
            },
        };
    }

    /// <summary>
    /// True for <c>Array.Empty&lt;T&gt;()</c> of an element type arrays support, which C# calls for <c>[]</c> and for
    /// a <c>params</c> array given no arguments; <paramref name="elementType"/> is T.
    /// </summary>
    public static bool IsEmptyArray(IlMethodReference method, [NotNullWhen(true)] out IlType? elementType)
    {
        ArgumentNullException.ThrowIfNull(method);
        elementType = method is
        {
            HasThis: false, Name: "Empty", ParameterTypes.Count: 0, GenericArguments: [var element],
            DeclaringType: IlNamedType { Namespace: "System", Name: "Array", IsDefinedInThisAssembly: false },
        } && TryMapElementType(element, out _)
            ? element
            : null;
        return elementType is not null;
    }
}
