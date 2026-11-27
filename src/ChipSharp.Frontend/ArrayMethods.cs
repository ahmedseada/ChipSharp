using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;

namespace ChipSharp.Frontend;

/// <summary>
/// The methods of <c>System.Array</c> ChipSharp compiles, with .NET 10's results and .NET's exceptions (as messages
/// that stop the program, see <see cref="Lowering.ArrayMethodFunctions"/>).
/// </summary>
/// <remarks>
/// <para>
/// Most are compiler intrinsics (<see cref="Call"/>): what <c>Array.Copy</c>, <c>ConstrainedCopy</c>, <c>CopyTo</c>,
/// <c>Clear</c>, <c>Fill</c>, <c>IndexOf</c>, <c>LastIndexOf</c>, <c>Reverse</c>, <c>Resize</c> and <c>Clone</c> do
/// is written as TIR (Lowering/MethodLowering.ArrayMethods.cs), because they work on arrays of every element type
/// arrays hold, the program's own enums, structs and classes too, and those of <c>System.Array</c> that take an
/// <c>Array</c> learn the element type only from the array on IL's evaluation stack. <c>Array.Sort&lt;T&gt;</c> and
/// <c>Array.BinarySearch&lt;T&gt;</c> are a library substitute, plain C# in ChipSharp.Library: .NET's own introsort
/// and binary search, over numbers and bools.
/// </para>
/// <para>
/// None of them makes a collection run but <c>Resize</c> and <c>Clone</c>, which allocate the new array first and copy
/// into it after; so the elements of an array of references are copied, cleared, moved and compared with no collection
/// in between, and the collector, which runs only when an allocation needs it, never sees an array half copied.
/// </para>
/// <para>
/// The methods that take a delegate (<c>Exists</c>, <c>Find</c>, <c>ForEach</c>, <c>ConvertAll</c>, ...) arrive with
/// delegates; those of an <c>IComparer</c> or of <c>object</c> values, and the rest of <c>System.Array</c>, stay library
/// methods that are not available yet.
/// </para>
/// </remarks>
internal static class ArrayMethods
{
    /// <summary>
    /// The methods ChipSharp compiles itself, as C# names them, for the list of library methods a program can call (see
    /// Analysis/LibraryCalls.cs); Sort and BinarySearch are the library substitute's.
    /// </summary>
    public static IReadOnlyList<string> Members { get; } =
    [
        "System.Array.Copy(System.Array, System.Array, int)", "System.Array.Copy(System.Array, System.Array, long)",
        "System.Array.Copy(System.Array, int, System.Array, int, int)",
        "System.Array.Copy(System.Array, long, System.Array, long, long)",
        "System.Array.ConstrainedCopy(System.Array, int, System.Array, int, int)",
        "System.Array.CopyTo(System.Array, int)", "System.Array.CopyTo(System.Array, long)",
        "System.Array.Clear(System.Array)", "System.Array.Clear(System.Array, int, int)",
        "System.Array.Fill(T[], T)", "System.Array.Fill(T[], T, int, int)",
        "System.Array.IndexOf(T[], T)", "System.Array.IndexOf(T[], T, int)", "System.Array.IndexOf(T[], T, int, int)",
        "System.Array.LastIndexOf(T[], T)", "System.Array.LastIndexOf(T[], T, int)",
        "System.Array.LastIndexOf(T[], T, int, int)",
        "System.Array.Reverse(T[])", "System.Array.Reverse(T[], int, int)",
        "System.Array.Resize(ref T[], int)", "System.Array.Clone()",
    ];

    /// <summary>
    /// What a call or callvirt of <paramref name="method"/> is, when it is a method of <c>System.Array</c> ChipSharp
    /// compiles itself, or one that takes a delegate (<see cref="ArrayOperation.WithDelegate"/>); null for any other
    /// method, Sort and BinarySearch (library substitutes) and <c>Array.Empty</c> (see <see cref="StackArrays"/>) too.
    /// </summary>
    public static ArrayMethodCall? Call(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method is not { Definition: null, DeclaringType: IlNamedType { Namespace: "System", Name: "Array",
                DeclaringType: null, IsDefinedInThisAssembly: false } })
        {
            return null;
        }

        var parameters = method.ParameterTypes.Select(Shape).ToArray();
        if (method.GenericArguments is [var argument])
        {
            var element = StackArrays.Primitive(argument);
            if (method.ParameterTypes.Any(IsDelegate))
            {
                return new ArrayMethodCall(ArrayOperation.WithDelegate, element);
            }

            ArrayOperation? generic = (method.Name, method.HasThis, string.Concat(parameters)) switch
            {
                ("Fill", false, "[T") => ArrayOperation.Fill,
                ("Fill", false, "[Tii") => ArrayOperation.FillRange,
                ("IndexOf", false, "[T") => ArrayOperation.IndexOf,
                ("IndexOf", false, "[Ti") => ArrayOperation.IndexOfFrom,
                ("IndexOf", false, "[Tii") => ArrayOperation.IndexOfRange,
                ("LastIndexOf", false, "[T") => ArrayOperation.LastIndexOf,
                ("LastIndexOf", false, "[Ti") => ArrayOperation.LastIndexOfFrom,
                ("LastIndexOf", false, "[Tii") => ArrayOperation.LastIndexOfRange,
                ("Reverse", false, "[") => ArrayOperation.Reverse,
                ("Reverse", false, "[ii") => ArrayOperation.ReverseRange,
                ("Resize", false, "&i") => ArrayOperation.Resize,
                _ => null,
            };
            return generic is { } operation ? new ArrayMethodCall(operation, element) : null;
        }

        if (method.GenericArguments.Count > 0)
        {
            return method.ParameterTypes.Any(IsDelegate) ? new ArrayMethodCall(ArrayOperation.WithDelegate, null) : null;
        }

        ArrayOperation? plain = (method.Name, method.HasThis, string.Concat(parameters)) switch
        {
            ("Copy", false, "AAi") => ArrayOperation.Copy,
            ("Copy", false, "AAl") => ArrayOperation.CopyLong,
            ("Copy", false, "AiAii") or ("ConstrainedCopy", false, "AiAii") => ArrayOperation.CopyRange,
            ("Copy", false, "AlAll") => ArrayOperation.CopyRangeLong,
            ("CopyTo", true, "Ai") => ArrayOperation.CopyTo,
            ("CopyTo", true, "Al") => ArrayOperation.CopyToLong,
            ("Clear", false, "A") => ArrayOperation.Clear,
            ("Clear", false, "Aii") => ArrayOperation.ClearRange,
            ("Clone", true, "") => ArrayOperation.Clone,
            _ => null,
        };
        return plain is { } found ? new ArrayMethodCall(found, null) : null;
    }

    /// <summary>
    /// True for <c>Array.Sort&lt;T&gt;(T[])</c>, <c>Sort&lt;T&gt;(T[], int, int)</c>, <c>BinarySearch&lt;T&gt;(T[], T)</c>
    /// and <c>BinarySearch&lt;T&gt;(T[], int, int, T)</c>, which the library substitute has (linked to it or not: a T it
    /// cannot be made for leaves the reference as it is); <paramref name="element"/> is T.
    /// </summary>
    public static bool IsSortOrSearch(IlMethodReference method, out IlType element)
    {
        ArgumentNullException.ThrowIfNull(method);
        element = IlType.Void;
        if (method is not { HasThis: false, Name: "Sort" or "BinarySearch", GenericArguments: [var argument],
                DeclaringType: IlNamedType { Namespace: "System", Name: "Array", DeclaringType: null,
                    IsDefinedInThisAssembly: false } })
        {
            return false;
        }

        var shape = string.Concat(method.ParameterTypes.Select(Shape));
        element = StackArrays.Primitive(argument);
        return (method.Name, shape) is ("Sort", "[" or "[ii") or ("BinarySearch", "[T" or "[iiT");
    }

    /// <summary>
    /// True for an element type Sort and BinarySearch take: a number or a bool (whose order .NET's introsort gives the
    /// same way every time), not an enum, a struct or a reference.
    /// </summary>
    public static bool IsSortable(IlType element) => ValueTypes.WithoutModifiers(StackArrays.Primitive(element)) is IlPrimitiveType
    {
        Kind: >= IlPrimitiveKind.Bool and <= IlPrimitiveKind.NativeUInt,
    };

    /// <summary>
    /// Why IndexOf and LastIndexOf cannot compare elements of <paramref name="element"/> the way .NET does, or null when
    /// they can: numbers, bools and enums by value (a float NaN equal to NaN, -0.0 to 0.0, as .NET's Equals says), and
    /// arrays and the program's classes by reference, when neither the class nor a class it derives from has an Equals
    /// of its own. A struct, text and <c>object</c> are compared with an Equals that may be the program's.
    /// </summary>
    public static Unsupported? ForIndexOf(IlType element, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(assembly);
        var plain = ValueTypes.WithoutModifiers(StackArrays.Primitive(element));
        if (plain is IlPrimitiveType { Kind: IlPrimitiveKind.String or IlPrimitiveKind.Object }
            || (ValueTypes.Definition(plain, assembly) is { } value && ValueTypes.IsStruct(value)))
        {
            return Unsupported.ArrayIndexOfElements;
        }

        for (var type = ObjectTypes.Class(plain, assembly); type is not null; type = ObjectTypes.BaseClass(type))
        {
            if (type.Methods.Any(method => method is { Name: "Equals", IsStatic: false, Parameters.Count: 1 })
                || type.Assembly.Interfaces(type).Any(face => face is IlGenericInstanceType
                    { Definition: IlNamedType { Namespace: "System", Name: "IEquatable`1" } }))
            {
                return Unsupported.ArrayIndexOfElements;
            }
        }

        return null;
    }

    /// <summary>
    /// The instruction that takes the value the instruction at <paramref name="index"/> of <paramref name="method"/>
    /// pushes, and which of its arguments the value is (from 0, the first pushed): the same one on every way the IL goes
    /// from there (<c>Array.Resize(ref buffer, c ? 4 : 8)</c> branches before the call takes the reference); null when
    /// the ways do not agree, or one leaves the method first.
    /// </summary>
    public static (IlInstruction Consumer, int Argument)? ConsumerOf(IlMethod method, int index)
    {
        ArgumentNullException.ThrowIfNull(method);
        var body = method.Body!;
        (IlInstruction Consumer, int Argument)? found = null;
        var seen = new HashSet<(int Index, int Depth)>();
        var work = new Stack<(int Index, int Depth)>([(index + 1, 1)]); // the value is that deep from the top
        while (work.Count > 0)
        {
            var (i, depth) = work.Pop();
            if (i >= body.Instructions.Count || !seen.Add((i, depth)))
            {
                if (i >= body.Instructions.Count)
                {
                    return null;
                }

                continue;
            }

            var instruction = body.Instructions[i];
            var (pops, pushes) = IlStackEffects.Of(instruction, method);
            if (pops >= depth)
            {
                if (found is { } other && (other.Consumer != instruction || other.Argument != pops - depth))
                {
                    return null;
                }

                found = (instruction, pops - depth);
                continue;
            }

            var next = depth + pushes - pops;
            if (instruction.OpCode is IlOpCode.Ret or IlOpCode.Throw or IlOpCode.Rethrow or IlOpCode.Jmp
                or IlOpCode.Leave or IlOpCode.Leave_S or IlOpCode.Endfinally or IlOpCode.Endfilter)
            {
                return null;
            }

            foreach (var target in IlBlock.Targets(instruction))
            {
                work.Push((body.IndexOf(target), next));
            }

            if (instruction.OpCode is not (IlOpCode.Br or IlOpCode.Br_S))
            {
                work.Push((i + 1, next));
            }
        }

        return found;
    }

    /// <summary>
    /// True for an <c>ldloca</c>, <c>ldarga</c>, <c>ldsflda</c> or <c>ldflda</c> at <paramref name="index"/> whose
    /// address only <c>Array.Resize</c> takes, as C#'s <c>Array.Resize(ref buffer, n)</c> gives it.
    /// </summary>
    public static bool IsResizeReference(IlMethod method, int index) =>
        method.Body!.Instructions[index].OpCode is IlOpCode.Ldloca or IlOpCode.Ldloca_S or IlOpCode.Ldarga
            or IlOpCode.Ldarga_S or IlOpCode.Ldsflda or IlOpCode.Ldflda
        && ConsumerOf(method, index) is ({ OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } }, 0)
        && Call(callee)?.Operation == ArrayOperation.Resize;

    /// <summary>
    /// True when <paramref name="operation"/> writes the elements of the array it takes at <paramref name="argument"/>
    /// (from 0, <c>this</c> first): the destination of a copy, the array Clear, Fill, Reverse and Resize change. The
    /// others only read theirs (the static arrays they read may stay constant tables, see <see cref="StaticArrays"/>).
    /// </summary>
    public static bool Writes(ArrayOperation operation, int argument) => operation switch
    {
        ArrayOperation.Copy or ArrayOperation.CopyLong or ArrayOperation.CopyTo or ArrayOperation.CopyToLong =>
            argument == 1,
        ArrayOperation.CopyRange or ArrayOperation.CopyRangeLong => argument == 2,
        ArrayOperation.Clear or ArrayOperation.ClearRange or ArrayOperation.Fill or ArrayOperation.FillRange
            or ArrayOperation.Reverse or ArrayOperation.ReverseRange or ArrayOperation.Resize => argument == 0,
        ArrayOperation.IndexOf or ArrayOperation.IndexOfFrom or ArrayOperation.IndexOfRange or ArrayOperation.LastIndexOf
            or ArrayOperation.LastIndexOfFrom or ArrayOperation.LastIndexOfRange or ArrayOperation.Clone => false,
        _ => true,
    };

    /// <summary>True for the operations whose arrays are <c>System.Array</c> in the signature: the element type comes from the stack.</summary>
    public static bool TakesAnyArray(ArrayOperation operation) => operation is ArrayOperation.Copy
        or ArrayOperation.CopyLong or ArrayOperation.CopyRange or ArrayOperation.CopyRangeLong or ArrayOperation.CopyTo
        or ArrayOperation.CopyToLong or ArrayOperation.Clear or ArrayOperation.ClearRange or ArrayOperation.Clone;

    /// <summary>A parameter type as a letter: A for Array, [ for T[], &amp; for ref T[], T, i for int, l for long.</summary>
    private static string Shape(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlNamedType { Namespace: "System", Name: "Array", IsDefinedInThisAssembly: false } => "A",
        IlArrayType { ElementType: IlGenericParameterType } => "[",
        IlByRefType { ElementType: IlArrayType { ElementType: IlGenericParameterType } } => "&",
        IlGenericParameterType => "T",
        IlPrimitiveType { Kind: IlPrimitiveKind.Int32 } => "i",
        IlPrimitiveType { Kind: IlPrimitiveKind.Int64 } => "l",
        _ => "?",
    };

    /// <summary>True for a delegate type of the library's that Array's methods take: Predicate, Action, Converter, Comparison.</summary>
    private static bool IsDelegate(IlType type) => ValueTypes.WithoutModifiers(type) is IlGenericInstanceType
    {
        Definition: IlNamedType { Namespace: "System", Name: "Predicate`1" or "Action`1" or "Converter`2" or "Comparison`1" },
    };
}

/// <summary>The methods of System.Array that ChipSharp compiles itself (see <see cref="ArrayMethods"/>), by overload.</summary>
internal enum ArrayOperation
{
    /// <summary><c>Array.Copy(Array, Array, int)</c>.</summary>
    Copy,

    /// <summary><c>Array.Copy(Array, Array, long)</c>.</summary>
    CopyLong,

    /// <summary><c>Array.Copy(Array, int, Array, int, int)</c> and <c>Array.ConstrainedCopy</c>.</summary>
    CopyRange,

    /// <summary><c>Array.Copy(Array, long, Array, long, long)</c>.</summary>
    CopyRangeLong,

    /// <summary><c>array.CopyTo(Array, int)</c>.</summary>
    CopyTo,

    /// <summary><c>array.CopyTo(Array, long)</c>.</summary>
    CopyToLong,

    /// <summary><c>Array.Clear(Array)</c>.</summary>
    Clear,

    /// <summary><c>Array.Clear(Array, int, int)</c>.</summary>
    ClearRange,

    /// <summary><c>Array.Fill&lt;T&gt;(T[], T)</c>.</summary>
    Fill,

    /// <summary><c>Array.Fill&lt;T&gt;(T[], T, int, int)</c>.</summary>
    FillRange,

    /// <summary><c>Array.IndexOf&lt;T&gt;(T[], T)</c>.</summary>
    IndexOf,

    /// <summary><c>Array.IndexOf&lt;T&gt;(T[], T, int)</c>.</summary>
    IndexOfFrom,

    /// <summary><c>Array.IndexOf&lt;T&gt;(T[], T, int, int)</c>.</summary>
    IndexOfRange,

    /// <summary><c>Array.LastIndexOf&lt;T&gt;(T[], T)</c>.</summary>
    LastIndexOf,

    /// <summary><c>Array.LastIndexOf&lt;T&gt;(T[], T, int)</c>.</summary>
    LastIndexOfFrom,

    /// <summary><c>Array.LastIndexOf&lt;T&gt;(T[], T, int, int)</c>.</summary>
    LastIndexOfRange,

    /// <summary><c>Array.Reverse&lt;T&gt;(T[])</c>.</summary>
    Reverse,

    /// <summary><c>Array.Reverse&lt;T&gt;(T[], int, int)</c>.</summary>
    ReverseRange,

    /// <summary><c>Array.Resize&lt;T&gt;(ref T[], int)</c>: a new array on the heap.</summary>
    Resize,

    /// <summary><c>array.Clone()</c>: a new array on the heap, as an object (<c>(int[])values.Clone()</c> casts it back).</summary>
    Clone,

    /// <summary>A method of Array that takes a delegate (<c>Exists</c>, <c>Find</c>, ...): not yet.</summary>
    WithDelegate,
}

/// <summary>
/// A call of a method of System.Array that ChipSharp compiles itself: which one, and its T (null for those that take an
/// <c>Array</c>, whose element type is the array's on the stack).
/// </summary>
internal sealed record ArrayMethodCall(ArrayOperation Operation, IlType? Element);
