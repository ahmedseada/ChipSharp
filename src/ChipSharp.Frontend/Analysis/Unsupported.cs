using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// A C# feature the program uses that <see cref="SupportedSubset"/> does not have yet, in C# words, with when it is
/// planned and a hint for what to write instead. <see cref="Message"/> is e.g. "building text ($"...", +, ...) arrives
/// with M3 (the heap). For now, print the parts one after another with Console.Write." The problems of a check that
/// have equal features are one group in its summary.
/// </summary>
/// <param name="What">The feature as C# programmers know it, e.g. "arrays" or "checked arithmetic".</param>
/// <param name="IsPlural">True when <paramref name="What"/> takes "arrive" rather than "arrives".</param>
/// <param name="Hint">What to do for now: a sentence; empty for a library method (the summary lists the ones there are).</param>
internal sealed partial record Unsupported(string What, bool IsPlural, string Hint)
{
    /// <summary>The <see cref="Arrival"/> of what needs objects on the heap: classes, strings, generics, ...</summary>
    private const string WithTheHeap = "with M3 (the heap)";

    /// <summary>The <see cref="Arrival"/> of the rest: planned, without a milestone yet.</summary>
    private const string Later = "later";

    private const string ObjectsHint = "For now, use structs and static methods.";
    private const string ReferencesHint = "For now, pass values in and return results.";
    private const string FailuresHint = "For now, report failures through return values";

    public static readonly Unsupported CreatingObjects = Heap("creating objects (new ...)", false, ObjectsHint);
    public static readonly Unsupported ClassesAndObjects = Heap("classes and objects", true, ObjectsHint);
    public static readonly Unsupported Interfaces = Heap("interfaces", true,
        "For now, implement the interface on a struct and use it through a constraint: static void Run<T>(ref T device) " +
        "where T : struct, IDevice.");
    public static readonly Unsupported TypeTests = Heap("type tests and casts on objects (is, as, (T)x)", true,
        ObjectsHint);
    public static readonly Unsupported Boxing = Heap("boxing (using a value as an object)", false,
        "For now, keep values in their own types, such as int and bool, and give a method that takes any of them a type " +
        "parameter instead of object: static void Show<T>(T value) where T : struct.");
    public static readonly Unsupported InstanceCalls = Heap("instance or virtual method calls", true,
        "For now, call static methods, or the methods of your own structs.");
    public static readonly Unsupported GenericMethods = Heap("generic methods", true,
        "For now, write a method for each type you need.");
    public static readonly Unsupported GenericTypes = Heap("generic types", true,
        "For now, write a type for each type argument you need.");
    public static readonly Unsupported MethodsWithoutBody = new("methods without a body (extern, DllImport)", true,
        "For now, write the method in C#.");
    public static readonly Unsupported Strings = Heap("text strings", true,
        "For now, keep text in locals, parameters, return values and static fields.");
    public static readonly Unsupported TextReferences = Heap("references to text (ref string, out string)", true,
        "For now, return the text instead.");
    public static readonly Unsupported BuildingText = Heap(
        "building text ($\"...\", +, ToString, string.Format and the other methods of string)", false,
        "For now, print the parts one after another with Console.Write.");
    public static readonly Unsupported Characters = Heap("characters (char)", true, "For now, use whole numbers.");
    public static readonly Unsupported Arrays = Heap("arrays", true,
        "For now, use arrays of numbers, bools, structs or enums, each kept in the method that creates it.");
    public static readonly Unsupported Stackalloc = new("stackalloc", false,
        "For now, use an array with a constant size, such as new int[16]: ChipSharp keeps it on the stack too.");
    public static readonly Unsupported Fields = Heap("instance fields of classes (fields without static)", true,
        "For now, make the fields static, or make the class a struct.");
    public static readonly Unsupported Structs = new("structs", true, "For now, use separate whole-number and bool variables.");
    public static readonly Unsupported Enums = new("enums", true, "For now, use int constants.");
    public static readonly Unsupported CheckedArithmetic = new("checked arithmetic", false,
        "For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.");
    public static readonly Unsupported FiniteCheck = new("checking that a float is finite (ckfinite)", false,
        "For now, compare it: x >= -double.MaxValue && x <= double.MaxValue is false for NaN and the infinities.");
    public static readonly Unsupported Exceptions = new("exceptions", true, $"{FailuresHint}.");
    public static readonly Unsupported TryCatchFinally = new("try/catch/finally", true,
        $"{FailuresHint}; using, lock and foreach over a collection make a try/finally too.");
    public static readonly Unsupported Addresses = new("taking addresses (ref, out, struct methods)", false, ReferencesHint);
    public static readonly Unsupported References = new("references and pointers (ref, out, in, *)", true, ReferencesHint);
    public static readonly Unsupported UnsafeCode = new("unsafe code (pointers, &x, sizeof, fixed)", false,
        "For now, use ref parameters, ref locals and structs.");
    public static readonly Unsupported ExplicitLayout = new("structs with an explicit layout (LayoutKind.Explicit)", true,
        "For now, leave out [StructLayout(LayoutKind.Explicit)] and [FieldOffset].");
    public static readonly Unsupported RefFields = new("ref fields", true, "For now, keep references in parameters and locals.");
    // delegates and lambdas compile (see Delegates): what is left of this is function pointers
    public static readonly Unsupported Delegates = new("function pointers (delegate*, &Method)", true,
        "For now, use a delegate (Func<int, int>, Action), or call the method directly.");
    public static readonly Unsupported TypeHandles = new("typeof and type handles", true, "For now, leave them out.");
    public static readonly Unsupported Arguments = new("using the command-line arguments (args)", false,
        "For now, leave args unused: Main may take string[] args only if it never uses them.");
    public static readonly Unsupported OtherInstruction = new("this IL instruction", false,
        "For now, write that code another way.");
    public static readonly Unsupported OtherType = new("this type", false,
        "For now, use bool, the whole-number types (sbyte to ulong, nint, nuint), float, double, string (for fixed " +
        "text), and the program's own structs and enums.");

    /// <summary>
    /// When the feature is planned, as the end of <see cref="Heading"/>: "with M3 (the heap)" for what needs objects on
    /// the heap, else "later".
    /// </summary>
    public string Arrival { get; private init; } = Later;

    /// <summary>True for a library method the program calls: it "is not available yet", not "arrives later".</summary>
    public bool IsLibraryMethod { get; private init; }

    /// <summary>True for a static field of the library the program uses: it "is not available yet" too.</summary>
    public bool IsLibraryField { get; private init; }

    /// <summary>
    /// What is not supported, as a clause: "classes and objects arrive with M3 (the heap)", "checked arithmetic arrives
    /// later", "the library method System.Math.Abs(int) is not available yet".
    /// </summary>
    public string Heading => IsRule ? What
        : IsLibraryMethod || IsLibraryField ? $"{What} is not available yet"
        : $"{What} {(IsPlural ? "arrive" : "arrives")} {Arrival}";

    /// <summary>The sentence a problem line ends with: what is missing and when it arrives, then the hint.</summary>
    public string Message => Hint.Length == 0 ? $"{Heading}." : $"{Heading}. {Hint}";

    /// <summary>A call to a library method <see cref="KnownExternals"/> does not have, e.g. "System.Math.Abs(int)".</summary>
    public static Unsupported LibraryMethod(string method) =>
        new($"the library method {method}", false, "") { IsLibraryMethod = true };

    /// <summary>A static field of another assembly the program reads or writes, e.g. "nint.Zero".</summary>
    public static Unsupported LibraryField(string field) =>
        new($"the library field {field}", false, "For now, use constants or the static fields of your own types.")
        {
            IsLibraryField = true,
        };

    /// <summary><see cref="OtherType"/> for one type, so that two types are two features: "the type System.Random".</summary>
    public static Unsupported TypeNamed(string type) => OtherType with { What = $"the type {type}" };

    /// <summary><see cref="OtherInstruction"/> for one instruction: "the IL instruction cpblk".</summary>
    public static Unsupported InstructionNamed(string name) => OtherInstruction with { What = $"the IL instruction {name}" };

    /// <summary>
    /// True for the type C# builds an interpolated string (<c>$"..."</c>) with. Its local, the address of that local
    /// and the calls on it are all one feature: <see cref="BuildingText"/>.
    /// </summary>
    public static bool IsInterpolatedStringHandler(IlType type) => type is IlNamedType
    {
        Namespace: "System.Runtime.CompilerServices", Name: "DefaultInterpolatedStringHandler", IsDefinedInThisAssembly: false,
    };

    /// <summary>A feature that needs objects on the heap, so it arrives <see cref="WithTheHeap"/>.</summary>
    private static Unsupported Heap(string what, bool isPlural, string hint) =>
        new(what, isPlural, hint) { Arrival = WithTheHeap };

    /// <summary>What an instruction outside <see cref="SupportedSubset.OpCodes"/> stands for in C#.</summary>
    public static Unsupported ForInstruction(IlOpCode opCode)
    {
        var name = opCode.GetName();
        return opCode switch
        {
            IlOpCode.Newobj => CreatingObjects,
            IlOpCode.Castclass or IlOpCode.Isinst => TypeTests,
            IlOpCode.Box or IlOpCode.Unbox or IlOpCode.Unbox_Any => Boxing,
            IlOpCode.Callvirt or IlOpCode.Constrained => InstanceCalls,
            IlOpCode.Newarr or IlOpCode.Ldlen => Arrays,
            IlOpCode.Localloc => Stackalloc,
            IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld => Fields,
            IlOpCode.Ldsflda => Addresses,
            IlOpCode.Initobj or IlOpCode.Ldobj or IlOpCode.Stobj or IlOpCode.Cpobj => Structs,
            IlOpCode.Sizeof => UnsafeCode,
            IlOpCode.Ckfinite => FiniteCheck,
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
        IlPrimitiveType { Kind: IlPrimitiveKind.String } => Strings,
        IlPrimitiveType { Kind: IlPrimitiveKind.Char } => Characters,
        IlPrimitiveType { Kind: IlPrimitiveKind.Object } => ClassesAndObjects,
        IlArrayType or IlMultiDimensionalArrayType => ForArrayType(type, assembly),
        IlByRefType or IlPointerType or IlPinnedType => References,
        IlFunctionPointerType => Delegates,
        IlNamedType { IsDefinedInThisAssembly: true } named when assembly.FindType(named.FullName) is { } definition =>
            ForDefinition(definition),
        _ when IsInterpolatedStringHandler(type) => BuildingText,
        // an instance of a generic type of the program: its type arguments' problem, else what the type is
        IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } named } instance =>
            Generics.Instantiations.Refusal(instance, assembly)
                ?? (assembly.FindType(named.FullName) is { } generic ? ForDefinition(generic) : GenericTypes),
        _ => OtherType,
    };

    private static Unsupported ForDefinition(IlTypeDefinition definition) => definition switch
    {
        { IsInterface: true } => Interfaces,
        { BaseType: IlNamedType { Namespace: "System", Name: "Enum" } } => Enums,
        { BaseType: IlNamedType { Namespace: "System", Name: "ValueType" } } => Structs,
        { BaseType: IlNamedType { Namespace: "System", Name: "MulticastDelegate" } } => Delegates,
        { GenericParameters.Count: > 0 } => GenericClasses,
        _ => ClassesAndObjects,
    };
}
