using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// <c>Span&lt;T&gt;</c> and <c>ReadOnlySpan&lt;T&gt;</c> without a heap: a span is a view of elements that live
/// somewhere else, so it needs no memory of its own.
/// </summary>
/// <remarks>
/// <para>
/// The representation: a span value (on IL's evaluation stack, in a local, a parameter, a return value or a field of a
/// ref struct) is an array value, <see cref="StackArrays.ArrayType"/>, <c>{ ptr data, i32 length }</c>: where its first
/// element is, and how many there are. So an array becomes a span as it is, and a span of a span (a slice) is a new
/// <c>data</c> and <c>length</c>. A <c>ReadOnlySpan&lt;T&gt;</c> is the same; C# makes sure nothing writes through it.
/// The element types are those of arrays (<see cref="StackArrays.TryMapElementType"/>).
/// </para>
/// <para>
/// What a span can view: an array (on the stack, or in a static field: <see cref="StaticArrays"/>), <c>stackalloc</c>
/// (a slot of the method, like the elements of an array on the stack), constant data (<c>static ReadOnlySpan&lt;byte&gt;
/// Digits =&gt; new byte[] { ... }</c>, which C# keeps in the assembly: a constant table, in flash on a chip, never
/// copied), and a variable (<c>MemoryMarshal.CreateSpan(ref x, 1)</c>). C#'s rules for ref structs already keep a
/// span of <c>stackalloc</c> or of a variable from outliving it; a span of an array on the stack obeys the array's rule
/// (see <see cref="Analysis.ArrayEscapeAnalysis"/>).
/// </para>
/// <para>
/// The members are compiler intrinsics (<see cref="Call"/>): what Span's own methods, the <c>AsSpan</c> of arrays,
/// <c>RuntimeHelpers.CreateSpan</c> and the safe part of <c>MemoryMarshal</c> do is written as TIR (see
/// Lowering/MethodLowering.Spans.cs), with .NET's checks and messages. The rest of <c>MemoryExtensions</c> and
/// <c>BinaryPrimitives</c> are library substitutes, plain C# over spans in ChipSharp.Library.
/// </para>
/// </remarks>
internal static class Spans
{
    /// <summary>
    /// The runtime function an index or length outside a span calls: .NET's ArgumentOutOfRangeException ("Specified
    /// argument was out of the range of valid values.").
    /// </summary>
    public const string ArgumentOutOfRange = "ChipSharp.Runtime.ArgumentOutOfRange";

    /// <summary>
    /// The runtime function MemoryMarshal.Read and Write call when the span has fewer bytes than the value: .NET's
    /// ArgumentOutOfRangeException with "(Parameter 'length')".
    /// </summary>
    public const string LengthOutOfRange = "ChipSharp.Runtime.LengthOutOfRange";

    /// <summary>The runtime function CopyTo calls when the destination is shorter: .NET's ArgumentException, "Destination is too short."</summary>
    public const string DestinationTooShort = "ChipSharp.Runtime.DestinationTooShort";

    /// <summary>
    /// The runtime function a MemoryMarshal.Cast calls on a target without unaligned access when the span it gives does
    /// not start where its elements may be: .NET's DataMisalignedException.
    /// </summary>
    public const string DataMisaligned = "ChipSharp.Runtime.DataMisaligned";

    /// <summary>The runtime function a length that does not fit in an int calls: .NET's OverflowException.</summary>
    public const string Overflow = "ChipSharp.Runtime.Overflow";

    private const string InteropServices = "System.Runtime.InteropServices";
    private const string CompilerServices = "System.Runtime.CompilerServices";

    /// <summary>int, in the patterns of signatures.</summary>
    private const IlPrimitiveKind IntKind = IlPrimitiveKind.Int32;

    /// <summary>
    /// The library members ChipSharp compiles itself, as C# names them, for the list of library methods a program can
    /// call (see Analysis/LibraryCalls.cs).
    /// </summary>
    public static IReadOnlyList<string> Members { get; } =
    [
        "System.Span<T>.new Span(T[])", "System.Span<T>.new Span(T[], int, int)", "System.Span<T>.Length",
        "System.Span<T>.IsEmpty", "System.Span<T>.Empty", "System.Span<T>.Slice(int)", "System.Span<T>.Slice(int, int)",
        "System.Span<T>.CopyTo(System.Span<T>)", "System.Span<T>.TryCopyTo(System.Span<T>)", "System.Span<T>.Fill(T)",
        "System.Span<T>.Clear()", "System.Span<T>.operator ==(System.Span<T>, System.Span<T>)",
        "System.ReadOnlySpan<T>.new ReadOnlySpan(T[])", "System.ReadOnlySpan<T>.new ReadOnlySpan(T[], int, int)",
        "System.ReadOnlySpan<T>.Length", "System.ReadOnlySpan<T>.IsEmpty", "System.ReadOnlySpan<T>.Empty",
        "System.ReadOnlySpan<T>.Slice(int)", "System.ReadOnlySpan<T>.Slice(int, int)",
        "System.ReadOnlySpan<T>.CopyTo(System.Span<T>)", "System.ReadOnlySpan<T>.TryCopyTo(System.Span<T>)",
        "System.ReadOnlySpan<T>.operator ==(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>)",
        "System.MemoryExtensions.AsSpan(T[])", "System.MemoryExtensions.AsSpan(T[], int)",
        "System.MemoryExtensions.AsSpan(T[], int, int)",
        "System.Runtime.InteropServices.MemoryMarshal.Read(System.ReadOnlySpan<byte>)",
        "System.Runtime.InteropServices.MemoryMarshal.Write(System.Span<byte>, in T)",
        "System.Runtime.InteropServices.MemoryMarshal.TryRead(System.ReadOnlySpan<byte>, out T)",
        "System.Runtime.InteropServices.MemoryMarshal.TryWrite(System.Span<byte>, in T)",
        "System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Span<T>)",
        "System.Runtime.InteropServices.MemoryMarshal.Cast(System.Span<TFrom>)",
        "System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref T, int)",
        "System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref T, int)",
    ];

    /// <summary>
    /// True for <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c> (modifiers looked through); <paramref name="element"/>
    /// is T as a signature names it (<c>int32</c>), and <paramref name="isReadOnly"/> tells the two apart.
    /// </summary>
    public static bool IsSpan(IlType type, [NotNullWhen(true)] out IlType? element, out bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (ValueTypes.WithoutModifiers(type) is IlGenericInstanceType
            {
                Definition: IlNamedType { Namespace: "System", Name: "Span`1" or "ReadOnlySpan`1", DeclaringType: null,
                    IsDefinedInThisAssembly: false } definition,
                Arguments: [var argument],
            })
        {
            element = StackArrays.Primitive(argument);
            isReadOnly = definition.Name == "ReadOnlySpan`1";
            return true;
        }

        (element, isReadOnly) = (null, false);
        return false;
    }

    /// <summary>True for <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c> of any T.</summary>
    public static bool IsSpan(IlType type) => IsSpan(type, out _, out _);

    /// <summary>
    /// The TIR type of a span of an element type arrays support: <see cref="StackArrays.ArrayType"/>.
    /// <paramref name="assembly"/> is the program's, which defines its structs and enums.
    /// </summary>
    public static bool TryMapType(IlType type, IlAssembly assembly, [NotNullWhen(true)] out TirType? tirType)
    {
        tirType = IsSpan(type, out var element, out _) && StackArrays.TryMapElementType(element, assembly, out _)
            ? StackArrays.ArrayType
            : null;
        return tirType is not null;
    }

    /// <summary>
    /// True for a type whose values may hold a span: a span, or a struct (a ref struct) with a field of such a type. A
    /// method that hands one out (returns it, writes it through a ref or out parameter, or is a method of one) may let a
    /// span outlive the method; see <see cref="Analysis.ArrayEscapeAnalysis"/>.
    /// </summary>
    public static bool IsSpanLike(IlType type, IlAssembly assembly) => IsSpanLike(type, assembly, 0);

    /// <summary>The element type of a span or an array, as a signature names it; null for any other type.</summary>
    public static IlType? ElementOf(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlArrayType array => StackArrays.Primitive(array.ElementType),
        var other when IsSpan(other, out var element, out _) => element,
        _ => null,
    };

    /// <summary>
    /// What a call or newobj of <paramref name="method"/> is, when it is one of the members ChipSharp compiles itself:
    /// the operation and its element type (and the second type of <c>MemoryMarshal.Cast</c>). Null for any other method,
    /// also for a member of a span this does not know (it is a library method, not available).
    /// </summary>
    public static SpanCall? Call(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Definition is not null)
        {
            return null;
        }

        if (IsSpan(method.DeclaringType, out var element, out var isReadOnly))
        {
            return SpanMember(method, element, isReadOnly);
        }

        if (method.DeclaringType is not IlNamedType { DeclaringType: null, IsDefinedInThisAssembly: false } type)
        {
            return null;
        }

        var parameters = method.ParameterTypes.Select(ValueTypes.WithoutModifiers).ToArray();
        var arguments = method.GenericArguments.Select(StackArrays.Primitive).ToArray();
        return (type.FullName, method.Name, method.HasThis, parameters, arguments) switch
        {
            ("System.MemoryExtensions", "AsSpan", false, [IlArrayType], [var t]) => new(SpanOperation.FromArray, t),
            ("System.MemoryExtensions", "AsSpan", false, [IlArrayType, IlPrimitiveType { Kind: IntKind }], [var t]) =>
                new(SpanOperation.FromArrayStart, t),
            ("System.MemoryExtensions", "AsSpan", false,
                [IlArrayType, IlPrimitiveType { Kind: IntKind }, IlPrimitiveType { Kind: IntKind }], [var t]) =>
                new(SpanOperation.FromArrayRange, t),
            ($"{CompilerServices}.RuntimeHelpers", "CreateSpan", false, [IlNamedType { Name: "RuntimeFieldHandle" }], [var t]) =>
                new(SpanOperation.Data, t),
            ($"{InteropServices}.MemoryMarshal", _, false, _, _) => MemoryMarshal(method.Name, parameters, arguments),
            _ => null,
        };
    }

    /// <summary>
    /// True for the instruction that makes a span from a constant data field: <c>ldtoken</c> of the field right before
    /// <c>RuntimeHelpers.CreateSpan&lt;T&gt;</c>, at <paramref name="index"/> in <paramref name="body"/>.
    /// </summary>
    public static bool IsDataToken(IlMethodBody body, int index) =>
        body.Instructions[index] is { OpCode: IlOpCode.Ldtoken, Operand: IlFieldOperand { Field.Definition: not null } }
        && index + 1 < body.Instructions.Count
        && body.Instructions[index + 1] is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var method } }
        && Call(method)?.Operation == SpanOperation.Data;

    /// <summary>The field of constant data an <c>ldtoken</c> or <c>ldsflda</c> names: one C# gave data in the assembly (an RVA).</summary>
    public static IlField? DataField(IlInstruction instruction) =>
        instruction is { OpCode: IlOpCode.Ldtoken or IlOpCode.Ldsflda, Operand: IlFieldOperand { Field.Definition: { IsStatic: true } field } }
        && field.DeclaringType.Name == "<PrivateImplementationDetails>"
        && field.DeclaringType.Assembly.HasFieldData(field)
            ? field
            : null;

    /// <summary>
    /// How many bytes of data a constant data field has: as many as its type takes (C# gives it an int, a long or a
    /// struct of exactly that size); null when that is not known.
    /// </summary>
    public static int? DataSize(IlField field) => ValueTypes.WithoutModifiers(field.Type) switch
    {
        IlPrimitiveType { Kind: IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 } => 1,
        IlPrimitiveType { Kind: IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16 } => 2,
        IlPrimitiveType { Kind: IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32 } => 4,
        IlPrimitiveType { Kind: IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 } => 8,
        IlNamedType { IsDefinedInThisAssembly: true } named when field.DeclaringType.Assembly.FindType(named.FullName) is { } type =>
            field.DeclaringType.Assembly.ClassSize(type),
        _ => null,
    };

    /// <summary>
    /// The bytes one element of <paramref name="storage"/> (a TIR element type) takes in constant data, as C# writes
    /// it: 0 for a type C# never puts there (a struct, nint).
    /// </summary>
    public static int DataElementSize(TirType storage) => storage.Kind switch
    {
        TirTypeKind.I8 => 1,
        TirTypeKind.I16 => 2,
        TirTypeKind.I32 or TirTypeKind.F32 => 4,
        TirTypeKind.I64 or TirTypeKind.F64 => 8,
        _ => 0,
    };

    /// <summary>
    /// The <c>stackalloc</c>s of <paramref name="body"/> that make a span, as C# writes them (Release and Debug): the
    /// size (<c>ldc.i4 16; conv.u</c>, or <c>ldc.i4.2; conv.u; sizeof T; mul.ovf.un</c> for a struct), <c>localloc</c>,
    /// the initializer if there is one (stores through the new memory, or a <c>cpblk</c> from constant data), the number
    /// of elements and <c>newobj Span&lt;T&gt;::.ctor(void*, int32)</c>, with no branch in between. Any other localloc
    /// (into a pointer: unsafe code) is not among them.
    /// </summary>
    public static IReadOnlyList<Stackalloc> FindStackallocs(IlMethodBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = new List<Stackalloc>();
        var instructions = body.Instructions;
        var targets = instructions.SelectMany(Lowering.IlBlock.Targets).ToHashSet();
        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode != IlOpCode.Localloc)
            {
                continue;
            }

            for (var c = i + 1; c < instructions.Count; c++)
            {
                var instruction = instructions[c];
                if (targets.Contains(instruction.Offset) || instruction.Operand is IlBranchOperand or IlSwitchOperand
                    || instruction.OpCode is IlOpCode.Ret or IlOpCode.Localloc)
                {
                    break;
                }

                if (IsPointerConstructor(instruction, out var element))
                {
                    var start = instructions[i - 1].OpCode == IlOpCode.Mul_Ovf_Un && i >= 2 ? i - 2 : i;
                    found.Add(new Stackalloc(start, i, c, element, Integer(instructions[c - 1])));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The uses of constant data in <paramref name="body"/> through <c>ldsflda</c>: <c>ldsflda field; ldc.i4 n; newobj
    /// ReadOnlySpan&lt;T&gt;::.ctor(void*, int32)</c> (C#'s <c>new byte[] { ... }</c> as a span, a table of n elements), or
    /// <c>ldsflda field; ldc.i4 size; [unaligned.] cpblk</c> in a stackalloc's initializer (the bytes it copies).
    /// </summary>
    public static IReadOnlyList<DataUse> FindDataUses(IlMethodBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = new List<DataUse>();
        var instructions = body.Instructions;
        for (var i = 0; i + 2 < instructions.Count; i++)
        {
            if (instructions[i].OpCode != IlOpCode.Ldsflda || DataField(instructions[i]) is not { } field
                || Integer(instructions[i + 1]) is not { } count)
            {
                continue;
            }

            if (IsPointerConstructor(instructions[i + 2], out var element))
            {
                found.Add(new DataUse(i, field, element, count, i + 2));
            }
            else
            {
                var copy = instructions[i + 2].OpCode == IlOpCode.Unaligned && i + 3 < instructions.Count ? i + 3 : i + 2;
                if (instructions[copy].OpCode == IlOpCode.Cpblk)
                {
                    found.Add(new DataUse(i, field, null, count, copy));
                }
            }
        }

        return found;
    }

    /// <summary>True for <c>newobj Span&lt;T&gt;::.ctor(void*, int32)</c> (or ReadOnlySpan's); <paramref name="element"/> is T.</summary>
    public static bool IsPointerConstructor(IlInstruction instruction, [NotNullWhen(true)] out IlType? element)
    {
        element = instruction is { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var method } }
            && Call(method) is { Operation: SpanOperation.FromPointer } call
                ? call.Element
                : null;
        return element is not null;
    }

    /// <summary>The int32 an ldc.i4 instruction pushes, or null for any other instruction.</summary>
    public static int? Integer(IlInstruction instruction) => instruction switch
    {
        { OpCode: >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8 } => (int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0,
        { OpCode: IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4, Operand: IlInt32Operand constant } => constant.Value,
        _ => null,
    };

    private static bool IsSpanLike(IlType type, IlAssembly assembly, int depth) =>
        IsSpan(type)
        || (depth < 8 && ValueTypes.Definition(type, assembly) is { } definition && ValueTypes.IsStruct(definition)
            && ValueTypes.InstanceFields(definition).Any(field => IsSpanLike(field.Type, definition.Assembly, depth + 1)));

    /// <summary>A member of Span&lt;T&gt; or ReadOnlySpan&lt;T&gt; (its signature names T as <c>!0</c>).</summary>
    private static SpanCall? SpanMember(IlMethodReference method, IlType element, bool isReadOnly)
    {
        var parameters = method.ParameterTypes.Select(ValueTypes.WithoutModifiers).ToArray();
        var operation = (method.Name, method.HasThis, parameters) switch
        {
            (".ctor", true, [IlArrayType]) => SpanOperation.FromArray,
            (".ctor", true, [IlArrayType, IlPrimitiveType { Kind: IntKind }, IlPrimitiveType { Kind: IntKind }]) =>
                SpanOperation.FromArrayRange,
            (".ctor", true, [IlPointerType { ElementType: IlPrimitiveType { Kind: IlPrimitiveKind.Void } },
                IlPrimitiveType { Kind: IntKind }]) => SpanOperation.FromPointer,
            (".ctor", true, [IlByRefType]) => SpanOperation.FromReference,
            ("get_Item", true, [IlPrimitiveType { Kind: IntKind }]) => SpanOperation.Item,
            ("get_Length", true, []) => SpanOperation.Length,
            ("get_IsEmpty", true, []) => SpanOperation.IsEmpty,
            ("get_Empty", false, []) => SpanOperation.Empty,
            ("Slice", true, [IlPrimitiveType { Kind: IntKind }]) => SpanOperation.Slice,
            ("Slice", true, [IlPrimitiveType { Kind: IntKind }, IlPrimitiveType { Kind: IntKind }]) =>
                SpanOperation.SliceRange,
            ("CopyTo", true, [var a]) when IsSpan(a) => SpanOperation.CopyTo,
            ("TryCopyTo", true, [var a]) when IsSpan(a) => SpanOperation.TryCopyTo,
            ("Fill", true, [IlGenericParameterType]) when !isReadOnly => SpanOperation.Fill,
            ("Clear", true, []) when !isReadOnly => SpanOperation.Clear,
            ("ToArray", true, []) => SpanOperation.ToArray,
            ("ToString", true, []) => SpanOperation.ToText,
            ("op_Implicit", false, [IlArrayType]) => SpanOperation.FromArray,
            ("op_Implicit", false, [var a]) when IsSpan(a) && !isReadOnly => SpanOperation.ToReadOnly,
            ("op_Equality", false, [var a, var b]) when IsSpan(a) && IsSpan(b) => SpanOperation.Equal,
            ("op_Inequality", false, [var a, var b]) when IsSpan(a) && IsSpan(b) => SpanOperation.NotEqual,
            _ => (SpanOperation?)null,
        };
        return operation is { } known ? new SpanCall(known, element) : null;
    }

    /// <summary>The safe part of MemoryMarshal: reading and writing values as bytes, and spans of other types.</summary>
    private static SpanCall? MemoryMarshal(string name, IlType[] parameters, IlType[] arguments) =>
        (name, parameters, arguments) switch
        {
            ("Read", [var source], [var t]) when IsSpan(source) => new(SpanOperation.Read, t),
            ("Write", [var destination, IlByRefType], [var t]) when IsSpan(destination) => new(SpanOperation.Write, t),
            ("TryRead", [var source, IlByRefType], [var t]) when IsSpan(source) => new(SpanOperation.TryRead, t),
            ("TryWrite", [var destination, IlByRefType], [var t]) when IsSpan(destination) => new(SpanOperation.TryWrite, t),
            ("AsBytes", [var span], [var t]) when IsSpan(span) => new(SpanOperation.AsBytes, t),
            ("Cast", [var span], [var from, var to]) when IsSpan(span) => new(SpanOperation.Cast, from, to),
            ("CreateSpan" or "CreateReadOnlySpan", [IlByRefType, IlPrimitiveType { Kind: IntKind }], [var t]) =>
                new(SpanOperation.FromReferenceAndLength, t),
            _ => null,
        };
}

/// <summary>What a member ChipSharp compiles itself does (see <see cref="Spans.Call"/>).</summary>
internal enum SpanOperation
{
    /// <summary><c>new Span&lt;T&gt;(array)</c>, the conversion of an array, <c>array.AsSpan()</c>: the array as it is.</summary>
    FromArray,

    /// <summary><c>array.AsSpan(start)</c>: from start to the end.</summary>
    FromArrayStart,

    /// <summary><c>new Span&lt;T&gt;(array, start, length)</c>, <c>array.AsSpan(start, length)</c>.</summary>
    FromArrayRange,

    /// <summary><c>new Span&lt;T&gt;(void*, int)</c>, as C# makes a span of stackalloc or of constant data.</summary>
    FromPointer,

    /// <summary><c>new Span&lt;T&gt;(ref T)</c>: one element.</summary>
    FromReference,

    /// <summary><c>MemoryMarshal.CreateSpan(ref T, int)</c> and <c>CreateReadOnlySpan</c>: as many elements as it says, unchecked.</summary>
    FromReferenceAndLength,

    /// <summary>The conversion of a Span&lt;T&gt; to a ReadOnlySpan&lt;T&gt;: the span as it is.</summary>
    ToReadOnly,

    /// <summary><c>Span&lt;T&gt;.Empty</c>.</summary>
    Empty,

    /// <summary>The indexer: a reference to an element, after the bounds check.</summary>
    Item,

    Length,

    IsEmpty,

    /// <summary><c>Slice(start)</c>, and C#'s <c>s[start..]</c>.</summary>
    Slice,

    /// <summary><c>Slice(start, length)</c>, and C#'s <c>s[a..b]</c>.</summary>
    SliceRange,

    CopyTo,

    TryCopyTo,

    Fill,

    Clear,

    /// <summary><c>ToArray()</c>: a new array, which needs the heap.</summary>
    ToArray,

    /// <summary><c>ToString()</c>: text built while the program runs.</summary>
    ToText,

    /// <summary><c>==</c>: the same elements of the same memory, not equal elements.</summary>
    Equal,

    NotEqual,

    /// <summary><c>RuntimeHelpers.CreateSpan&lt;T&gt;(field)</c>, as C# makes a span of constant data of more than bytes.</summary>
    Data,

    /// <summary><c>MemoryMarshal.Read&lt;T&gt;(bytes)</c>: a value from the first bytes, at any alignment.</summary>
    Read,

    Write,

    TryRead,

    TryWrite,

    /// <summary><c>MemoryMarshal.AsBytes</c>: the same memory as bytes.</summary>
    AsBytes,

    /// <summary><c>MemoryMarshal.Cast&lt;TFrom, TTo&gt;</c>: the same memory as elements of another type.</summary>
    Cast,
}

/// <summary>
/// A call of a member ChipSharp compiles itself: <paramref name="Operation"/> on elements (or values) of
/// <paramref name="Element"/>; <paramref name="Target"/> is the type <c>MemoryMarshal.Cast</c> casts to.
/// </summary>
internal sealed record SpanCall(SpanOperation Operation, IlType Element, IlType? Target = null);

/// <summary>
/// A stackalloc that makes a span (see <see cref="Spans.FindStackallocs"/>): its instructions run from
/// <paramref name="Start"/> (the size's sizeof or multiplication, else the localloc) through <paramref name="Localloc"/>
/// to <paramref name="Constructor"/>, the newobj of the span, of <paramref name="Count"/> elements of
/// <paramref name="Element"/> (null when the number is known only when the program runs).
/// </summary>
internal sealed record Stackalloc(int Start, int Localloc, int Constructor, IlType Element, int? Count)
{
    /// <summary>True for an instruction index inside it, after the localloc and up to the span's constructor.</summary>
    public bool Contains(int index) => index > Localloc && index <= Constructor;
}

/// <summary>
/// A use of constant data through ldsflda (see <see cref="Spans.FindDataUses"/>), at <paramref name="Index"/>: a span of
/// <paramref name="Count"/> elements of <paramref name="Element"/>, made by the constructor at <paramref name="End"/>; or,
/// when <paramref name="Element"/> is null, <paramref name="Count"/> bytes copied by the cpblk at <paramref name="End"/>.
/// </summary>
internal sealed record DataUse(int Index, IlField Field, IlType? Element, int Count, int End);
