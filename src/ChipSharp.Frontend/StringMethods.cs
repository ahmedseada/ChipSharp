using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>How the translation calls a library method of strings, chars or parsing (see <see cref="StringMethods"/>).</summary>
internal enum StringCallKind
{
    /// <summary>The runtime function, given the arguments (an array or a span of chars as its address and length).</summary>
    Runtime,

    /// <summary><c>s.Length</c>: the string's length field, after its null check.</summary>
    Length,

    /// <summary><c>s.ToString()</c> of a string: the string itself, after its null check.</summary>
    Self,

    /// <summary>The runtime function, given the descriptor of the array it makes last (char[], string[]); an array value.</summary>
    ArrayResult,

    /// <summary><c>s.AsSpan(...)</c>: the span of the string's chars (see <see cref="Strings"/>), a span value.</summary>
    Span,
}

/// <summary>
/// One library method of strings, chars or parsing and what it becomes: <paramref name="Runtime"/> names the runtime
/// function (<c>ChipSharp.Runtime.&lt;Runtime&gt;</c>, src/ChipSharp.Runtime.Shared/chipsharp_string*.c).
/// </summary>
/// <param name="Type">The library type, by its primitive kind (String, Char, Int32, ...) or full name.</param>
/// <param name="Name">The method's name, e.g. "Substring" or ".ctor".</param>
/// <param name="HasThis">True for an instance method (and a constructor), whose string comes first.</param>
/// <param name="Parameters">Its parameters' types, as <see cref="StringMethods.Key"/> writes them.</param>
/// <param name="Returns">Its return type, the same way.</param>
/// <param name="Runtime">The runtime function's name.</param>
/// <param name="Kind">How it is called.</param>
/// <param name="MakesText">True when it makes a new string or array (on the heap): a heap use.</param>
internal sealed record StringMethod(string Type, string Name, bool HasThis, string[] Parameters, string Returns,
    string Runtime, StringCallKind Kind, bool MakesText)
{
    /// <summary>The method as C# names it, e.g. "string.Substring(int, int)".</summary>
    public string CSharpName => $"{StringMethods.CSharpType(Type)}." +
        $"{(Name == ".ctor" ? "new" : Name)}({string.Join(", ", Parameters.Select(StringMethods.CSharpType))})";

    /// <summary>
    /// The method as the list of library methods names it (see Analysis/LibraryCalls.cs), e.g.
    /// "System.String.Substring(int, int)", "System.String.Length", "System.String.new String(char, int)".
    /// </summary>
    public string LibraryName
    {
        get
        {
            var type = StringMethods.LibraryType(Type);
            var parameters = string.Join(", ", Parameters.Select(StringMethods.CSharpType));
            return Name switch
            {
                ".ctor" => $"{type}.new {type[(type.LastIndexOf('.') + 1)..]}({parameters})",
                "get_Length" => $"{type}.Length",
                "get_Chars" => $"{type}.this[int]",
                "op_Equality" => $"{type}.operator ==({parameters})",
                "op_Inequality" => $"{type}.operator !=({parameters})",
                _ => $"{type}.{Name}({parameters})",
            };
        }
    }
}

/// <summary>
/// The methods of string, char and the whole numbers' Parse and TryParse that ChipSharp compiles, and the runtime
/// function each one becomes (see <see cref="Strings"/> for strings themselves). They have .NET's results, compare
/// ordinally (char by char: .NET compares by the current culture in <c>CompareTo</c>, <c>string.Compare</c>, and in
/// <c>IndexOf</c>, <c>LastIndexOf</c>, <c>StartsWith</c> and <c>EndsWith</c> of a string, which gives the same for text
/// without the characters a culture ignores, but for the order of upper and lower case letters), change case as the
/// invariant culture does (the letters of Latin-1, U+0000 to U+00FF; see chipsharp_string.c), and parse as the
/// invariant culture does. Where .NET throws, the program stops with .NET's message. <c>GetHashCode</c> of a string
/// is the same in every run (FNV-1a of its UTF-16 code units), where .NET's differs from one process to the next.
/// </summary>
/// <remarks>
/// A method that makes a new string (<see cref="StringMethod.MakesText"/>) is a heap use: a target without a heap
/// has it not. <c>ToString</c>, <c>GetHashCode</c> and <c>Equals(object)</c>, which C# calls as object's, are a
/// string's when the value they are called on is one (<see cref="IsStringReceiver"/>).
/// </remarks>
internal static class StringMethods
{
    private const string String = "String";
    private const string Char = "Char";
    private const string Int = "Int32";
    private const string Bool = "Bool";
    private const string Chars = "Char[]";
    private const string Span = "System.ReadOnlySpan`1<Char>";

    private static readonly StringMethod[] Table =
    [
        Read(String, "get_Length", [], Int, "", StringCallKind.Length),
        Read(String, "get_Chars", [Int], Char, "StringChar"),
        Static(String, "op_Equality", [String, String], Bool, "StringEquals"),
        Static(String, "op_Inequality", [String, String], Bool, "StringNotEquals"),
        Static(String, "Equals", [String, String], Bool, "StringEquals"),
        Read(String, "Equals", [String], Bool, "StringEquals"),
        Read(String, "Equals", ["Object"], Bool, "StringEqualsObject"),
        Static(String, "CompareOrdinal", [String, String], Int, "StringCompareOrdinal"),
        Static(String, "Compare", [String, String], Int, "StringCompare"),
        Read(String, "CompareTo", [String], Int, "StringCompare"),
        Static(String, "IsNullOrEmpty", [String], Bool, "StringIsNullOrEmpty"),
        Static(String, "IsNullOrWhiteSpace", [String], Bool, "StringIsNullOrWhiteSpace"),
        Read(String, "IndexOf", [Char], Int, "StringIndexOfChar"),
        Read(String, "IndexOf", [Char, Int], Int, "StringIndexOfCharFrom"),
        Read(String, "IndexOf", [Char, Int, Int], Int, "StringIndexOfCharIn"),
        Read(String, "IndexOf", [String], Int, "StringIndexOf"),
        Read(String, "IndexOf", [String, Int], Int, "StringIndexOfFrom"),
        Read(String, "LastIndexOf", [Char], Int, "StringLastIndexOfChar"),
        Read(String, "LastIndexOf", [Char, Int], Int, "StringLastIndexOfCharFrom"),
        Read(String, "LastIndexOf", [String], Int, "StringLastIndexOf"),
        Read(String, "Contains", [String], Bool, "StringContains"),
        Read(String, "Contains", [Char], Bool, "StringContainsChar"),
        Read(String, "StartsWith", [String], Bool, "StringStartsWith"),
        Read(String, "StartsWith", [Char], Bool, "StringStartsWithChar"),
        Read(String, "EndsWith", [String], Bool, "StringEndsWith"),
        Read(String, "EndsWith", [Char], Bool, "StringEndsWithChar"),
        Read(String, "ToString", [], String, "", StringCallKind.Self),
        Make(String, "Substring", [Int], "StringSubstring"),
        Make(String, "Substring", [Int, Int], "StringSubstringOf"),
        Make(String, "Replace", [Char, Char], "StringReplaceChar"),
        Make(String, "Replace", [String, String], "StringReplace"),
        Make(String, "Trim", [], "StringTrim"),
        Make(String, "TrimStart", [], "StringTrimStart"),
        Make(String, "TrimEnd", [], "StringTrimEnd"),
        Make(String, "Trim", [Char], "StringTrimChar"),
        Make(String, "TrimStart", [Char], "StringTrimStartChar"),
        Make(String, "TrimEnd", [Char], "StringTrimEndChar"),
        Make(String, "PadLeft", [Int], "StringPadLeft"),
        Make(String, "PadLeft", [Int, Char], "StringPadLeftWith"),
        Make(String, "PadRight", [Int], "StringPadRight"),
        Make(String, "PadRight", [Int, Char], "StringPadRightWith"),
        Make(String, "ToUpper", [], "StringToUpper"),
        Make(String, "ToLower", [], "StringToLower"),
        Make(String, "ToUpperInvariant", [], "StringToUpper"),
        Make(String, "ToLowerInvariant", [], "StringToLower"),
        Make(String, "Insert", [Int, String], "StringInsert"),
        Make(String, "Remove", [Int], "StringRemove"),
        Make(String, "Remove", [Int, Int], "StringRemoveCount"),
        new(String, "ToCharArray", true, [], Chars, "StringToCharArray", StringCallKind.ArrayResult, true),
        new(String, "Split", true, [Char, "System.StringSplitOptions"], "String[]", "StringSplit",
            StringCallKind.ArrayResult, true),
        new(String, "Join", false, [String, "String[]"], String, "StringJoin", StringCallKind.Runtime, true),
        new(String, "Concat", false, ["String[]"], String, "StringConcat", StringCallKind.Runtime, true),
        new(String, ".ctor", false, [Chars, Int, Int], "Void", "StringOfCharRange", StringCallKind.Runtime, true),
        new(String, ".ctor", false, [Char, Int], "Void", "StringRepeat", StringCallKind.Runtime, true),
        new(String, ".ctor", false, [Chars], "Void", "StringOfChars", StringCallKind.Runtime, true),
        new(String, ".ctor", false, [Span], "Void", "StringOfChars", StringCallKind.Runtime, true),
        new("System.ReadOnlySpan`1<Char>", "ToString", true, [], String, "StringOfChars", StringCallKind.Runtime, true),
        new("System.Span`1<Char>", "ToString", true, [], String, "StringOfChars", StringCallKind.Runtime, true),
        new("System.MemoryExtensions", "AsSpan", false, [String], Span, "", StringCallKind.Span, true),
        new(String, "op_Implicit", false, [String], Span, "", StringCallKind.Span, true),
        new("System.MemoryExtensions", "AsSpan", false, [String, Int], Span, "", StringCallKind.Span, true),
        new("System.MemoryExtensions", "AsSpan", false, [String, Int, Int], Span, "", StringCallKind.Span, true),

        // char
        Static(Char, "IsDigit", [Char], Bool, "CharIsDigit"),
        Static(Char, "IsLetter", [Char], Bool, "CharIsLetter"),
        Static(Char, "IsLetterOrDigit", [Char], Bool, "CharIsLetterOrDigit"),
        Static(Char, "IsWhiteSpace", [Char], Bool, "CharIsWhiteSpace"),
        Static(Char, "IsUpper", [Char], Bool, "CharIsUpper"),
        Static(Char, "IsLower", [Char], Bool, "CharIsLower"),
        Static(Char, "ToUpper", [Char], Char, "CharToUpper"),
        Static(Char, "ToLower", [Char], Char, "CharToLower"),
        Static(Char, "ToUpperInvariant", [Char], Char, "CharToUpper"),
        Static(Char, "ToLowerInvariant", [Char], Char, "CharToLower"),
        new(Char, "ToString", false, [Char], String, "StringOfChar", StringCallKind.Runtime, true),

        // the whole numbers' Parse and TryParse
        .. Parsing("Int8", "SByte"),
        .. Parsing("UInt8", "Byte"),
        .. Parsing("Int16", "Int16"),
        .. Parsing("UInt16", "UInt16"),
        .. Parsing("Int32", "Int32"),
        .. Parsing("UInt32", "UInt32"),
        .. Parsing("Int64", "Int64"),
        .. Parsing("UInt64", "UInt64"),
    ];

    /// <summary>Every method here, as C# names it, for the list of library methods a program can call.</summary>
    public static IReadOnlyList<string> Members { get; } =
        [.. Table.Where(method => method.Name != "op_Implicit").Select(method => method.LibraryName).Distinct()];

    /// <summary>The library type of the table's type: "System.String" for String, "System.Int32" for Int32, ...</summary>
    public static string LibraryType(string key) => key switch
    {
        "String" or "Char" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" => $"System.{key}",
        "Int8" => "System.SByte",
        "UInt8" => "System.Byte",
        "System.ReadOnlySpan`1<Char>" => "System.ReadOnlySpan<char>",
        "System.Span`1<Char>" => "System.Span<char>",
        _ => key,
    };

    /// <summary>The method <paramref name="reference"/> names (a library method, not linked to C#), or null.</summary>
    public static StringMethod? Find(IlMethodReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Definition is not null || reference.GenericArguments.Count > 0)
        {
            return null;
        }

        var type = Key(StackArrays.Primitive(reference.DeclaringType));
        var parameters = reference.ParameterTypes.Select(Key).ToArray();
        var returns = Key(reference.ReturnType);
        return Table.FirstOrDefault(method => method.Type == type && method.Name == reference.Name
            && (method.HasThis == reference.HasThis || method.Name == ".ctor")
            && method.Returns == returns && method.Parameters.SequenceEqual(parameters));
    }

    /// <summary>
    /// The string method that <paramref name="instruction"/> calls (a call, callvirt or newobj of one), or null. Object's
    /// ToString, GetHashCode and Equals(object) are a string's when the value they are called on is one.
    /// </summary>
    public static StringMethod? Find(IlMethod method, int index)
    {
        ArgumentNullException.ThrowIfNull(method);
        var instruction = method.Body!.Instructions[index];
        if (instruction is not { OpCode: IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj,
            Operand: IlMethodOperand { Method: var callee } })
        {
            return null;
        }

        // a span's ToString, through constrained. (C#'s span.ToString()): the string of its chars
        if (index > 0 && method.Body.Instructions[index - 1] is { OpCode: IlOpCode.Constrained } prefix)
        {
            return instruction.OpCode == IlOpCode.Callvirt && prefix.Operand is IlTypeOperand { Type: var type }
                && callee is { Name: "ToString", ParameterTypes.Count: 0, Definition: null }
                && ObjectTypes.IsSystemObject(callee.DeclaringType) && Key(type) is var key
                ? Table.FirstOrDefault(entry => entry.Type == key && entry.Name == "ToString")
                : null;
        }

        if (instruction.OpCode != IlOpCode.Newobj && ObjectMethod(callee) is { } objectMethod)
        {
            return IsStringReceiver(method, index) ? objectMethod : null;
        }

        return Find(callee) is { } found && (instruction.OpCode == IlOpCode.Newobj) == (found.Name == ".ctor") ? found : null;
    }

    /// <summary>True for the <c>constrained.</c> prefix of a call of a span's ToString (<see cref="Find(IlMethod, int)"/>): nothing of its own.</summary>
    public static bool IsPrefix(IlMethod method, int index)
    {
        ArgumentNullException.ThrowIfNull(method);
        var instructions = method.Body!.Instructions;
        return instructions[index].OpCode == IlOpCode.Constrained && index + 1 < instructions.Count
            && Find(method, index + 1) is not null;
    }

    /// <summary>
    /// True when the value <paramref name="method"/>'s instruction at <paramref name="index"/> (a call of an instance
    /// method) is called on is a string by its IL type: what pushed it, found by walking back in its block, is a local,
    /// a parameter, a field or a call's result of type string, or a literal.
    /// </summary>
    public static bool IsStringReceiver(IlMethod method, int index)
    {
        ArgumentNullException.ThrowIfNull(method);
        var body = method.Body!;
        var callee = ((IlMethodOperand)body.Instructions[index].Operand!).Method;
        var blocks = Lowering.IlBlock.Split(body);
        var start = blocks.Last(block => block.First <= index).First;

        // the receiver is as many values below the top of the stack before the call as the call has parameters
        return IsPushedString(method, index - 1, callee.ParameterTypes.Count, start);
    }

    /// <summary>
    /// True when the value <paramref name="depth"/> below the top of the stack before the instruction at
    /// <paramref name="index"/> (an argument of a call) is a string by its IL type, as <see cref="IsStringReceiver"/> finds it.
    /// </summary>
    public static bool IsStringOperand(IlMethod method, int index, int depth)
    {
        ArgumentNullException.ThrowIfNull(method);
        var start = Lowering.IlBlock.Split(method.Body!).Last(block => block.First <= index).First;
        return IsPushedString(method, index - 1, depth, start);
    }

    /// <summary>True for Console.Write(object) or Console.WriteLine(object): .NET prints what the object's ToString gives.</summary>
    public static bool IsConsoleWriteOfObject(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand
        {
            Method: { Name: "Write" or "WriteLine", HasThis: false, Definition: null, ParameterTypes: [var parameter] } callee,
        },
    } && ObjectTypes.IsSystemObject(parameter) && callee.DeclaringType is IlNamedType { FullName: "System.Console" };

    /// <summary>True when a value of <paramref name="type"/> (null: not known) may be a string: object, or not known.</summary>
    public static bool MayBeString(IlType? type) => type is null || ObjectTypes.IsSystemObject(type);

    /// <summary>The kinds of parameter type <see cref="Key"/> writes, for an array value or span of chars or strings.</summary>
    public static bool IsArrayValue(string key) => key is Chars or Span or "String[]";

    /// <summary>
    /// A type as the table writes it: a primitive by its kind ("String", "Int32", "Bool"), a reference with &amp;, an
    /// array with [], a generic instance with its arguments in &lt;&gt;, any other type by its full name.
    /// </summary>
    public static string Key(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlPrimitiveType primitive => primitive.Kind.ToString(),
        IlByRefType reference => Key(reference.ElementType) + "&",
        IlArrayType array => Key(StackArrays.Primitive(array.ElementType)) + "[]",
        IlGenericInstanceType { Definition: IlNamedType definition } instance =>
            $"{definition.FullName}<{string.Join(",", instance.Arguments.Select(argument => Key(StackArrays.Primitive(argument))))}>",
        IlNamedType named => StackArrays.Primitive(named) is IlPrimitiveType primitive ? primitive.Kind.ToString() : named.FullName,
        var other => other.ToString(),
    };

    /// <summary>A type of the table as C# writes it: "string", "int", "ReadOnlySpan&lt;char&gt;", ...</summary>
    public static string CSharpType(string key) => key switch
    {
        "String" => "string",
        "Char" => "char",
        "Bool" => "bool",
        "Object" => "object",
        "Int8" => "sbyte",
        "UInt8" => "byte",
        "Int16" => "short",
        "UInt16" => "ushort",
        "Int32" => "int",
        "UInt32" => "uint",
        "Int64" => "long",
        "UInt64" => "ulong",
        Span => "ReadOnlySpan<char>",
        "System.Span`1<Char>" => "Span<char>",
        "System.MemoryExtensions" => "MemoryExtensions",
        "System.StringSplitOptions" => "StringSplitOptions",
        [.. var element, '[', ']'] => CSharpType(element) + "[]",
        [.. var element, '&'] => "out " + CSharpType(element),
        _ => key,
    };

    /// <summary>The TIR type a runtime function takes or gives for a parameter or result of the table's type.</summary>
    public static TirType RuntimeType(string key) => key switch
    {
        "String" or "Object" => TirType.Ref,
        "Int64" or "UInt64" => TirType.I64,
        "Void" => TirType.Void,
        [.., '&'] => TirType.Ptr,
        _ => TirType.I32,
    };

    /// <summary>The runtime function of <paramref name="method"/>, as <see cref="StringCallKind.Runtime"/> calls it.</summary>
    public static ExternalFunction RuntimeFunction(StringMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var parameters = new List<TirType>();
        if (method.HasThis && method.Name != ".ctor")
        {
            parameters.Add(method.Type.StartsWith("System.", StringComparison.Ordinal) ? TirType.Ptr : TirType.Ref);
            if (method.Type.StartsWith("System.", StringComparison.Ordinal))
            {
                // a span's ToString: its chars and how many
                parameters.Add(TirType.I32);
            }
        }

        foreach (var parameter in method.Parameters)
        {
            parameters.AddRange(IsArrayValue(parameter) ? [TirType.Ptr, TirType.I32] : [RuntimeType(parameter)]);
        }

        if (method.Kind == StringCallKind.ArrayResult)
        {
            parameters.Add(TirType.Ptr);
        }

        var returns = method.Name == ".ctor" || method.Kind == StringCallKind.ArrayResult ? TirType.Ref
            : RuntimeType(method.Returns);
        return new ExternalFunction($"ChipSharp.Runtime.{method.Runtime}", returns, parameters, method.CSharpName);
    }

    /// <summary>
    /// True for a runtime function (by its display name, <c>ChipSharp.Runtime.&lt;name&gt;</c>) that makes strings or
    /// arrays on the heap: those of the methods that make text, and those kept text and enum names are made with.
    /// </summary>
    public static bool MakesObjects(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        const string Prefix = "ChipSharp.Runtime.";
        if (!displayName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = displayName[Prefix.Length..];
        return name.StartsWith("StringOf", StringComparison.Ordinal)
            || name is "StringConcat" or "StringAligned" or "StringSpanChars"
            || Table.Any(method => method.MakesText && method.Runtime == name);
    }

    /// <summary>The verdict on a call of <paramref name="method"/>: a heap use when it makes text, else fine.</summary>
    public static Unsupported? Verdict(StringMethod method) =>
        method.MakesText ? Unsupported.HeapUse(Unsupported.MakingText) : null;

    /// <summary>Object's ToString, GetHashCode and Equals(object), as a string's (when called on one).</summary>
    private static StringMethod? ObjectMethod(IlMethodReference callee) =>
        callee is { HasThis: true, Definition: null } && ObjectTypes.IsSystemObject(callee.DeclaringType)
            ? (callee.Name, callee.ParameterTypes.Count) switch
            {
                ("ToString", 0) => new StringMethod(String, "ToString", true, [], String, "", StringCallKind.Self, false),
                ("GetHashCode", 0) => new StringMethod(String, "GetHashCode", true, [], Int, "StringHash",
                    StringCallKind.Runtime, false),
                ("Equals", 1) => new StringMethod(String, "Equals", true, ["Object"], Bool, "StringEqualsObject",
                    StringCallKind.Runtime, false),
                _ => null,
            }
            : null;

    /// <summary>
    /// True when the value <paramref name="depth"/> below the top of the stack after the instruction at
    /// <paramref name="index"/> is a string pushed in the block, which starts at <paramref name="start"/>.
    /// </summary>
    private static bool IsPushedString(IlMethod method, int index, int depth, int start)
    {
        for (var i = index; i >= start; i--)
        {
            var instruction = method.Body!.Instructions[i];
            var (pops, pushes) = IlStackEffects.Of(instruction, method);
            if (depth < pushes)
            {
                return instruction.OpCode == IlOpCode.Dup ? IsPushedString(method, i - 1, 0, start)
                    : pushes == 1 && FixedText.IsText(PushedType(method, instruction) ?? IlType.Void);
            }

            depth += pops - pushes;
        }

        return false;
    }

    /// <summary>The IL type of the one value <paramref name="instruction"/> pushes, when it says: null when not.</summary>
    private static IlType? PushedType(IlMethod method, IlInstruction instruction) => instruction switch
    {
        { OpCode: IlOpCode.Ldstr } => IlType.String,
        { LocalIndex: { } local } when instruction.Name.StartsWith("ldloc", StringComparison.Ordinal)
            && !instruction.Name.StartsWith("ldloca", StringComparison.Ordinal) => method.Body!.Locals[local].Type,
        { ArgumentIndex: { } argument } when instruction.Name.StartsWith("ldarg", StringComparison.Ordinal)
            && !instruction.Name.StartsWith("ldarga", StringComparison.Ordinal) =>
            method.IsStatic ? method.Parameters[argument].Type
            : argument == 0 ? null : method.Parameters[argument - 1].Type,
        { OpCode: IlOpCode.Ldfld or IlOpCode.Ldsfld, Operand: IlFieldOperand { Field: var field } } => field.FieldType,
        { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } } => callee.ReturnType,
        { OpCode: IlOpCode.Ldelem_Ref } => null,
        _ => null,
    };

    private static StringMethod Read(string type, string name, string[] parameters, string returns, string runtime,
        StringCallKind kind = StringCallKind.Runtime) => new(type, name, true, parameters, returns, runtime, kind, false);

    private static StringMethod Static(string type, string name, string[] parameters, string returns, string runtime) =>
        new(type, name, false, parameters, returns, runtime, StringCallKind.Runtime, false);

    private static StringMethod Make(string type, string name, string[] parameters, string runtime) =>
        new(type, name, true, parameters, String, runtime, StringCallKind.Runtime, true);

    /// <summary>Parse(string) and TryParse(string, out T) of the whole number of kind <paramref name="kind"/>.</summary>
    private static StringMethod[] Parsing(string kind, string runtime) =>
    [
        Static(kind, "Parse", [String], kind, $"Parse{runtime}"),
        Static(kind, "TryParse", [String, kind + "&"], Bool, $"TryParse{runtime}"),
    ];
}
