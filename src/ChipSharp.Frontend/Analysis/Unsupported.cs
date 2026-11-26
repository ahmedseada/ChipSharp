using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// A C# feature the program uses that <see cref="SupportedSubset"/> does not have yet, in C# words, with a hint
/// for what to write instead. <see cref="Message"/> is e.g. "text strings arrive after M0. For now, print numbers
/// and bools with Console.WriteLine." The problems of a check that have equal features are one group in its summary.
/// </summary>
/// <param name="What">The feature as C# programmers know it, e.g. "arrays" or "checked arithmetic".</param>
/// <param name="IsPlural">True when <paramref name="What"/> takes "arrive" rather than "arrives".</param>
/// <param name="Hint">What to do for now: a sentence; empty for a library method (the summary lists the ones there are).</param>
internal sealed record Unsupported(string What, bool IsPlural, string Hint)
{
    private const string StringsHint = "For now, print numbers and bools with Console.WriteLine.";
    private const string ObjectsHint = "For now, use static methods with whole-number and bool values.";
    private const string ReferencesHint = "For now, pass values in and return results.";
    private const string VariablesHint = "For now, use separate variables.";
    private const string FailuresHint = "For now, report failures through return values";

    public static readonly Unsupported CreatingObjects = new("creating objects (new ...)", false, ObjectsHint);
    public static readonly Unsupported ClassesAndObjects = new("classes and objects", true, ObjectsHint);
    public static readonly Unsupported Interfaces = new("interfaces", true, ObjectsHint);
    public static readonly Unsupported Null = new("null", false, ObjectsHint);
    public static readonly Unsupported TypeTests = new("type tests and casts on objects (is, as, (T)x)", true, ObjectsHint);
    public static readonly Unsupported Boxing = new("boxing (using a value as an object)", false,
        "For now, keep values in their own types, such as int and bool.");
    public static readonly Unsupported InstanceCalls = new("instance or virtual method calls", true,
        "For now, call static methods of your own program.");
    public static readonly Unsupported InstanceMethods = new("instance methods", true, "For now, make the method static.");
    public static readonly Unsupported GenericMethods = new("generic methods", true,
        "For now, write a method for each type you need.");
    public static readonly Unsupported GenericTypes = new("generic types", true,
        "For now, put the method in a class that is not generic.");
    public static readonly Unsupported MethodsWithoutBody = new("methods without a body (extern, DllImport)", true,
        "For now, write the method in C#.");
    public static readonly Unsupported Strings = new("text strings", true, StringsHint);
    public static readonly Unsupported StringInterpolation = new("string interpolation ($\"...\")", false, StringsHint);
    public static readonly Unsupported Characters = new("characters (char)", true, "For now, use whole numbers.");
    public static readonly Unsupported Arrays = new("arrays", true, VariablesHint);
    public static readonly Unsupported Stackalloc = new("stackalloc", false, VariablesHint);
    public static readonly Unsupported Fields = new("fields", true,
        "For now, keep values in local variables and pass them as parameters.");
    public static readonly Unsupported Structs = new("structs", true, "For now, use separate whole-number and bool variables.");
    public static readonly Unsupported Enums = new("enums", true, "For now, use int constants.");
    public static readonly Unsupported CheckedArithmetic = new("checked arithmetic", false,
        $"For now, leave out checked: {SupportedSubset.Milestone} arithmetic wraps around on overflow, like unchecked C#.");
    public static readonly Unsupported FloatingPoint = new("floating point (float/double)", false,
        "For now, use whole numbers: int, long and the other integer types.");
    public static readonly Unsupported Exceptions = new("exceptions", true, $"{FailuresHint}.");
    public static readonly Unsupported TryCatchFinally = new("try/catch/finally", true,
        $"{FailuresHint}; using, lock and foreach over a collection make a try/finally too.");
    public static readonly Unsupported Addresses = new("taking addresses (ref, out, struct methods)", false, ReferencesHint);
    public static readonly Unsupported References = new("references and pointers (ref, out, in, *)", true, ReferencesHint);
    public static readonly Unsupported Delegates = new("delegates, lambdas and function pointers", true,
        "For now, call methods directly.");
    public static readonly Unsupported TypeHandles = new("typeof and type handles", true, "For now, leave them out.");
    public static readonly Unsupported Arguments = new("using the command-line arguments (args)", false,
        "For now, leave args unused: Main may take string[] args only if it never uses them.");
    public static readonly Unsupported OtherInstruction = new("this IL instruction", false,
        $"{SupportedSubset.Milestone} compiles static methods over bools and whole numbers.");
    public static readonly Unsupported OtherType = new("this type", false,
        $"{SupportedSubset.Milestone} supports bool, the whole-number types (sbyte to ulong, nint, nuint) and void.");

    /// <summary>True for a library method the program calls: it "is not available in M0" rather than "arrives after M0".</summary>
    public bool IsLibraryMethod { get; private init; }

    /// <summary>
    /// What is not supported, as a clause: "arrays arrive after M0",
    /// "the library method System.Math.Abs(int) is not available in M0".
    /// </summary>
    public string Heading => IsLibraryMethod
        ? $"{What} is not available in {SupportedSubset.Milestone}"
        : $"{What} {(IsPlural ? "arrive" : "arrives")} after {SupportedSubset.Milestone}";

    /// <summary>The sentence a problem line ends with: what arrives after the milestone, then the hint.</summary>
    public string Message => Hint.Length == 0 ? $"{Heading}." : $"{Heading}. {Hint}";

    /// <summary>A call to a library method <see cref="KnownExternals"/> does not have, e.g. "System.Math.Abs(int)".</summary>
    public static Unsupported LibraryMethod(string method) =>
        new($"the library method {method}", false, "") { IsLibraryMethod = true };

    /// <summary><see cref="OtherType"/> for one type, so that two types are two features: "the type System.Random".</summary>
    public static Unsupported TypeNamed(string type) => OtherType with { What = $"the type {type}" };

    /// <summary><see cref="OtherInstruction"/> for one instruction: "the IL instruction cpblk".</summary>
    public static Unsupported InstructionNamed(string name) => OtherInstruction with { What = $"the IL instruction {name}" };

    /// <summary>
    /// True for the type C# builds an interpolated string (<c>$"..."</c>) with. Its local, the address of that local
    /// and the calls on it are all one feature: <see cref="StringInterpolation"/>.
    /// </summary>
    public static bool IsInterpolatedStringHandler(IlType type) => type is IlNamedType
    {
        Namespace: "System.Runtime.CompilerServices", Name: "DefaultInterpolatedStringHandler", IsDefinedInThisAssembly: false,
    };

    /// <summary>What an instruction outside <see cref="SupportedSubset.OpCodes"/> stands for in C#.</summary>
    public static Unsupported ForInstruction(IlOpCode opCode)
    {
        var name = opCode.GetName();
        return opCode switch
        {
            IlOpCode.Newobj => CreatingObjects,
            IlOpCode.Ldnull => Null,
            IlOpCode.Castclass or IlOpCode.Isinst => TypeTests,
            IlOpCode.Box or IlOpCode.Unbox or IlOpCode.Unbox_Any => Boxing,
            IlOpCode.Callvirt or IlOpCode.Constrained => InstanceCalls,
            IlOpCode.Ldstr => Strings,
            IlOpCode.Newarr or IlOpCode.Ldlen => Arrays,
            IlOpCode.Localloc => Stackalloc,
            IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld or IlOpCode.Ldsfld or IlOpCode.Ldsflda or IlOpCode.Stsfld => Fields,
            IlOpCode.Initobj or IlOpCode.Ldobj or IlOpCode.Stobj or IlOpCode.Cpobj or IlOpCode.Sizeof => Structs,
            IlOpCode.Ldc_R4 or IlOpCode.Ldc_R8 or IlOpCode.Conv_R4 or IlOpCode.Conv_R8 or IlOpCode.Conv_R_Un
                or IlOpCode.Ckfinite => FloatingPoint,
            IlOpCode.Throw or IlOpCode.Rethrow or IlOpCode.Leave or IlOpCode.Leave_S or IlOpCode.Endfinally
                or IlOpCode.Endfilter => Exceptions,
            IlOpCode.Ldloca or IlOpCode.Ldloca_S or IlOpCode.Ldarga or IlOpCode.Ldarga_S => Addresses,
            IlOpCode.Ldftn or IlOpCode.Ldvirtftn or IlOpCode.Calli => Delegates,
            IlOpCode.Ldtoken => TypeHandles,
            _ when name.Contains(".ovf", StringComparison.Ordinal) => CheckedArithmetic,
            _ when name.StartsWith("ldelem", StringComparison.Ordinal) || name.StartsWith("stelem", StringComparison.Ordinal) => Arrays,
            _ when name.StartsWith("ldind", StringComparison.Ordinal) || name.StartsWith("stind", StringComparison.Ordinal) => References,
            _ => OtherInstruction,
        };
    }

    /// <summary>
    /// What a parameter, local or return type that <see cref="SupportedSubset.TryMapType"/> rejects stands for in C#.
    /// Null for a generic parameter (T): the generic method or type is the problem, reported on its own.
    /// </summary>
    public static Unsupported? ForType(IlType type, IlAssembly assembly) => type switch
    {
        IlModifiedType modified => ForType(modified.UnmodifiedType, assembly),
        IlGenericParameterType => null,
        IlPrimitiveType { Kind: IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64 } => FloatingPoint,
        IlPrimitiveType { Kind: IlPrimitiveKind.String } => Strings,
        IlPrimitiveType { Kind: IlPrimitiveKind.Char } => Characters,
        IlPrimitiveType { Kind: IlPrimitiveKind.Object } => ClassesAndObjects,
        IlArrayType or IlMultiDimensionalArrayType => Arrays,
        IlByRefType or IlPointerType or IlPinnedType => References,
        IlFunctionPointerType => Delegates,
        IlNamedType { IsDefinedInThisAssembly: true } named when assembly.FindType(named.FullName) is { } definition =>
            ForDefinition(definition),
        _ when IsInterpolatedStringHandler(type) => StringInterpolation,
        IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } } => GenericTypes,
        _ => OtherType,
    };

    private static Unsupported ForDefinition(IlTypeDefinition definition) => definition switch
    {
        { IsInterface: true } => Interfaces,
        { BaseType: IlNamedType { Namespace: "System", Name: "Enum" } } => Enums,
        { BaseType: IlNamedType { Namespace: "System", Name: "ValueType" } } => Structs,
        { BaseType: IlNamedType { Namespace: "System", Name: "MulticastDelegate" } } => Delegates,
        _ => ClassesAndObjects,
    };
}
