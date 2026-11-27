using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Arrays as the interfaces of collections: a <c>T[]</c> is an <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
/// <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c> (and <c>IEnumerable</c>)
/// in conversions, calls, <c>is</c> and casts, as in .NET (<c>IEnumerable&lt;int&gt; values = numbers;</c>,
/// <c>list.AddRange(array)</c>, <c>o is IList&lt;int&gt;</c>, a <c>foreach</c> over an <c>IEnumerable&lt;T&gt;</c> that
/// is an array).
/// <list type="bullet">
/// <item>ChipSharp.Library's <c>SzArray&lt;T&gt;</c> (.NET's SZArrayHelper) is a class whose methods are those of an
/// array as these interfaces, and take the array as their <c>this</c>: no object of it is made, but for each element type
/// T whose arrays the program uses as objects (an array where an interface or <c>object</c> is wanted: a local, a
/// parameter, a field, a return value, a type test; <see cref="Converted"/>) the whole-program pass takes
/// <c>SzArray&lt;T&gt;</c> to be made (<see cref="VirtualDispatch"/>), so a call through one of the interfaces on an object
/// of the program reaches its methods (one that only an array can be there is a plain call).</item>
/// <item>The descriptor of <c>T[]</c> gets <c>SzArray&lt;T&gt;</c>'s interface map
/// (<c>ProgramFunctions.ArrayInterfaces.cs</c>), so a call through an interface, a cast and a type test find it in the
/// array's header as in a class's; the method then has the array back with a cast to <c>T[]</c>.</item>
/// <item>An array used as an object is on the heap (the array analysis keeps it there), so on a target without a heap
/// this needs one.</item>
/// </list>
/// An array of a class is the interfaces of its own element type only, not of the element's base classes (which .NET's
/// covariance of arrays gives: an <c>IList&lt;Animal&gt;</c> that is a <c>Dog[]</c>); the variance of the interfaces
/// themselves is there (an <c>IEnumerable&lt;Animal&gt;</c> that is a <c>Dog[]</c>).
/// </summary>
internal static class ArrayInterfaces
{
    /// <summary>ChipSharp.Library's class of an array's interfaces, as the program's IL would name it.</summary>
    private static readonly IlNamedType SzArray = new("ChipSharp.Library.Collections", "SzArray`1", null, null);

    /// <summary>
    /// The class whose interface map arrays of <paramref name="arrayType"/> have: the instance of <c>SzArray&lt;T&gt;</c>
    /// for its element type; null when there is none (an element type an instance cannot have).
    /// </summary>
    public static IlTypeDefinition? ClassOf(IlArrayType arrayType, IlAssembly assembly) =>
        Generics.Instantiations.Type(
            new IlGenericInstanceType(SzArray, [StackArrays.Primitive(ValueTypes.WithoutModifiers(arrayType.ElementType))]),
            assembly);

    /// <summary>True when <paramref name="type"/> is an instance of <c>SzArray&lt;T&gt;</c>: the class of an array type's interfaces.</summary>
    public static bool IsArrayClass(IlTypeDefinition type) =>
        type.GenericDefinition is { } generic && generic.FullName == "ChipSharp.Library.Collections.SzArray`1"
        && generic.Assembly == LibrarySubstitutes.Assembly;

    /// <summary>The array type whose interfaces <paramref name="type"/>, an instance of <c>SzArray&lt;T&gt;</c>, is.</summary>
    public static IlArrayType ArrayTypeOf(IlTypeDefinition type) => new(type.TypeArguments[0]);

    /// <summary>
    /// The array types of the arrays that instruction <paramref name="index"/> of <paramref name="method"/> takes where an
    /// interface or <c>object</c> is wanted: an argument of a call, a local, a field, the return value, or what a type
    /// test tests. What is on the stack comes from the instruction of the same block that put it there
    /// (<see cref="VirtualMethods.StackType"/>); one from another block the translation finds.
    /// </summary>
    public static IEnumerable<IlArrayType> Converted(IlMethod method, int index)
    {
        var body = method.Body!;
        var assembly = method.Assembly;
        var instruction = body.Instructions[index];
        IEnumerable<(IlType? Type, int Depth)> wanted = instruction switch
        {
            // an interface's method called on the array too
            { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: { HasThis: true } callee } } =>
                callee.ParameterTypes.Select((type, k) => (Type: (IlType?)type, Depth: callee.ParameterTypes.Count - 1 - k))
                    .Append((callee.DeclaringType, callee.ParameterTypes.Count)),
            { OpCode: IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj, Operand: IlMethodOperand { Method: var callee } } =>
                callee.ParameterTypes.Select((type, k) => (Type: (IlType?)type, Depth: callee.ParameterTypes.Count - 1 - k)),
            { OpCode: >= IlOpCode.Stloc_0 and <= IlOpCode.Stloc_3 or IlOpCode.Stloc_S or IlOpCode.Stloc, LocalIndex: { } local }
                when local < body.Locals.Count => [(body.Locals[local].Type, 0)],
            { OpCode: IlOpCode.Stfld or IlOpCode.Stsfld, Operand: IlFieldOperand { Field: var field } } => [(field.FieldType, 0)],
            { OpCode: IlOpCode.Ret } when !method.ReturnType.Equals(IlType.Void) => [(method.ReturnType, 0)],
            { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var tested } } => [(tested, 0)],
            _ => [],
        };

        foreach (var (type, depth) in wanted)
        {
            if (type is not null && IsObjectOrInterface(type, assembly)
                && VirtualMethods.StackType(method, index, depth) is { } stacked
                && ValueTypes.WithoutModifiers(stacked) is IlArrayType array)
            {
                yield return array;
            }
        }
    }

    /// <summary>True for object or an interface an object may implement.</summary>
    private static bool IsObjectOrInterface(IlType type, IlAssembly assembly) =>
        ObjectTypes.IsSystemObject(type) || VirtualMethods.IsInterface(type, assembly);
}
