using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Type objects: what <c>typeof(X)</c> and <c>x.GetType()</c> give, objects of ChipSharp.Library's Type (the substitute
/// for System.Type, src/ChipSharp.Library/Type.cs). There is one per type, a constant object in read-only data (flash on
/// a chip) that the translation makes when first needed (Lowering/ProgramFunctions.TypeObjects.cs): its header the Type
/// class's descriptor, its fields the type's names as .NET gives them (constant strings, like literals) and what kind of
/// type it is. So two of the same type are the same object, as in .NET, the garbage collector never marks one (it is
/// outside the heap), and none needs a heap.
/// <list type="bullet">
/// <item><c>typeof(X)</c> is C#'s <c>ldtoken X</c> and <c>call Type.GetTypeFromHandle</c> (<see cref="TypeOf"/>): X's
/// type object, <c>typeof(T)</c> in generic code that of the type argument of each instance.</item>
/// <item><c>GetType()</c> (<see cref="IsGetType"/>) reads the object's header (null stops the program with .NET's
/// NullReferenceException) and finds the type object of its descriptor (<c>ChipSharp.Type.Of</c>): a class's, a box's
/// (<c>System.Int32</c>), an array's, a delegate's, a string's, a type object's own (<c>System.RuntimeType</c>).</item>
/// <item>The names (<see cref="Names"/>) are those of .NET 10: <c>Name</c> (<c>Node</c>, <c>Box`1</c>, <c>Int32[]</c>),
/// <c>FullName</c> (<c>Program+Node</c>, a generic instance's type arguments with their assemblies, as .NET writes
/// them), <c>Namespace</c> and <c>ToString()</c> (<c>Box`1[Program+Node]</c>); and <c>IsValueType</c>, <c>IsArray</c>,
/// <c>IsEnum</c>, <c>IsClass</c>, <c>IsInterface</c>, <c>IsPrimitive</c> and <c>IsGenericType</c>.</item>
/// </list>
/// Not yet: the rest of reflection (members, attributes, base types, type arguments, <c>Activator</c>), the token of a
/// method or field, and <c>typeof</c> of an open generic type (<c>typeof(List&lt;&gt;)</c>).
/// </summary>
internal static class TypeObjects
{
    /// <summary>The name .NET gives the class of type objects, a type object's own type.</summary>
    public const string RuntimeTypeName = "System.RuntimeType";

    /// <summary>The assembly .NET 10 names for the library's types in a <c>FullName</c>.</summary>
    public const string CoreLibrary = "System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";

    /// <summary>The bits of a type object's flags, as ChipSharp.Library's Type reads them.</summary>
    public const int ValueTypeFlag = 1, ArrayFlag = 2, EnumFlag = 4, InterfaceFlag = 8, PrimitiveFlag = 16, GenericFlag = 32;

    private static readonly IlNamedType SystemType = new("System", "Type", null, "System.Runtime");

    /// <summary>The class of type objects: ChipSharp.Library's Type.</summary>
    public static IlTypeDefinition TypeClass => LibrarySubstitutes.FindType(SystemType)
        ?? throw new InvalidOperationException("ChipSharp.Library has no substitute for System.Type.");

    /// <summary>
    /// The type of <c>typeof(X)</c> that starts at instruction <paramref name="index"/>: <c>ldtoken X</c> followed by
    /// <c>call Type.GetTypeFromHandle</c>; null for any other instruction, and for an open generic type.
    /// </summary>
    public static IlType? TypeOf(IReadOnlyList<IlInstruction> instructions, int index)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return instructions[index] is { OpCode: IlOpCode.Ldtoken, Operand: IlTypeOperand { Type: var type } }
            && index + 1 < instructions.Count && IsGetTypeFromHandle(instructions[index + 1])
            && !Generics.Substitution.IsOpen(type) && !IsOpenDefinition(type)
            ? type
            : null;
    }

    /// <summary>True for <c>Type.GetTypeFromHandle(RuntimeTypeHandle)</c>, the second half of C#'s <c>typeof</c>.</summary>
    public static bool IsGetTypeFromHandle(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand
        {
            Method: { Name: "GetTypeFromHandle", HasThis: false, Definition: null, ParameterTypes.Count: 1 } method,
        },
    } && method.DeclaringType is IlNamedType { FullName: "System.Type" };

    /// <summary>True for the second half of C#'s <c>typeof</c> at <paramref name="index"/>, after its <c>ldtoken</c>.</summary>
    public static bool EndsTypeOf(IReadOnlyList<IlInstruction> instructions, int index) =>
        index > 0 && IsGetTypeFromHandle(instructions[index]) && TypeOf(instructions, index - 1) is not null;

    /// <summary>
    /// True for <c>call</c> or <c>callvirt</c> of object's <c>GetType()</c> (and of Type's, which hides it: a type object's
    /// type).
    /// </summary>
    public static bool IsGetType(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call or IlOpCode.Callvirt,
        Operand: IlMethodOperand { Method: { Name: "GetType", HasThis: true, Definition: null, ParameterTypes.Count: 0 } method },
    } && (ObjectTypes.IsSystemObject(method.DeclaringType) || method.DeclaringType is IlNamedType { FullName: "System.Type" });

    /// <summary>True when instruction <paramref name="index"/> gives a type object: the start of a typeof, or GetType().</summary>
    public static bool GivesTypeObject(IReadOnlyList<IlInstruction> instructions, int index) =>
        TypeOf(instructions, index) is not null || IsGetType(instructions[index]);

    /// <summary>The flags of <paramref name="type"/> (of <paramref name="assembly"/>'s IL): what kind of type it is.</summary>
    public static int FlagsOf(IlType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        switch (plain)
        {
            case IlArrayType:
                return ArrayFlag;

            case IlPrimitiveType { Kind: IlPrimitiveKind.String or IlPrimitiveKind.Object }:
                return 0;

            case IlPrimitiveType { Kind: IlPrimitiveKind.Void }:
                return ValueTypeFlag;

            case IlPrimitiveType:
                return ValueTypeFlag | PrimitiveFlag;
        }

        var generic = plain is IlGenericInstanceType ? GenericFlag : 0;
        var named = plain is IlGenericInstanceType { Definition: IlNamedType definitionName } ? definitionName : plain as IlNamedType;
        var definition = named is null ? null
            : named.IsDefinedInThisAssembly ? assembly.FindType(named.FullName)
            : LibrarySubstitutes.FindType(named);
        return generic | definition switch
        {
            { IsInterface: true } => InterfaceFlag,
            { } found when ValueTypes.IsEnum(found) => ValueTypeFlag | EnumFlag,
            { } found when ValueTypes.IsStruct(found) => ValueTypeFlag,
            null when named?.FullName is "System.Nullable`1" or "System.Span`1" or "System.ReadOnlySpan`1" => ValueTypeFlag,
            null when named is { Namespace: "System" } && named.Name.StartsWith("ValueTuple", StringComparison.Ordinal) =>
                ValueTypeFlag,
            null when named?.FullName is "System.Collections.Generic.IEnumerable`1" or "System.IComparable"
                or "System.IComparable`1" or "System.IEquatable`1" or "System.IDisposable" => InterfaceFlag,
            _ => 0,
        };
    }

    /// <summary>
    /// The names .NET 10 gives the type whose <c>ToString()</c> is <paramref name="text"/> (a descriptor's RuntimeName,
    /// e.g. <c>Box`1[Program+Node][]</c>): its Name, FullName, Namespace and text. <paramref name="program"/> is the
    /// assembly a type argument that is none of the library's comes from, in a FullName.
    /// </summary>
    public static (string Name, string? FullName, string? Namespace) Names(string text, IlAssembly program)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(program);
        var (definition, arguments, ranks) = Split(text);
        var simple = definition[(definition.LastIndexOfAny(['.', '+']) + 1)..];
        var outermost = definition.IndexOf('+', StringComparison.Ordinal) is >= 0 and var plus ? definition[..plus] : definition;
        var ns = outermost.LastIndexOf('.') is > 0 and var dot ? outermost[..dot] : null;
        return (simple + ranks, FullName(text, program), ns);
    }

    /// <summary>True for an instance of a generic type (not an array of one), by its ToString(): <c>Box`1[System.Int32]</c>.</summary>
    public static bool IsGeneric(string text) => Split(text) is { Arguments.Count: > 0, Ranks.Length: 0 };

    /// <summary>
    /// .NET's FullName of the type whose ToString() is <paramref name="text"/>: the same, but that each type argument is
    /// written <c>[FullName, assembly]</c>.
    /// </summary>
    private static string FullName(string text, IlAssembly program)
    {
        var (definition, arguments, ranks) = Split(text);
        return arguments.Count == 0 ? text
            : $"{definition}[{string.Join(",", arguments.Select(argument =>
                $"[{FullName(argument, program)}, {AssemblyOf(argument, program)}]"))}]{ranks}";
    }

    /// <summary>The assembly of the type whose ToString() is <paramref name="text"/>: the program's, or the library's.</summary>
    private static string AssemblyOf(string text, IlAssembly program)
    {
        var (definition, _, _) = Split(text);
        return program.FindType(definition) is not null ? program.DisplayName : CoreLibrary;
    }

    /// <summary>
    /// A type's ToString() cut into its definition (<c>Box`1</c>), its type arguments' texts and its array ranks
    /// (<c>[][]</c>).
    /// </summary>
    private static (string Definition, IReadOnlyList<string> Arguments, string Ranks) Split(string text)
    {
        var end = text.Length;
        while (end >= 2 && text[end - 1] == ']' && text[end - 2] == '[')
        {
            end -= 2;
        }

        var ranks = text[end..];
        if (end == 0 || text[end - 1] != ']')
        {
            return (text[..end], [], ranks);
        }

        // the type arguments: from the '[' that opens the last ']', split at the commas between them
        var depth = 0;
        var open = end - 1;
        for (; open >= 0; open--)
        {
            depth += text[open] == ']' ? 1 : text[open] == '[' ? -1 : 0;
            if (depth == 0)
            {
                break;
            }
        }

        var arguments = new List<string>();
        var start = open + 1;
        depth = 0;
        for (var i = start; i < end - 1; i++)
        {
            depth += text[i] == '[' ? 1 : text[i] == ']' ? -1 : 0;
            if (text[i] == ',' && depth == 0)
            {
                arguments.Add(text[start..i]);
                start = i + 1;
            }
        }

        arguments.Add(text[start..(end - 1)]);
        return (text[..open], arguments, ranks);
    }

    /// <summary>True for a generic type itself, not an instance of it (<c>typeof(Box&lt;&gt;)</c>).</summary>
    private static bool IsOpenDefinition(IlType type) =>
        ValueTypes.WithoutModifiers(type) is IlNamedType { Name: var name } && name.Contains('`', StringComparison.Ordinal);
}
