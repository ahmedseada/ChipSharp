using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>What a call or instruction of delegates is (see <see cref="Delegates.Call"/>).</summary>
internal enum DelegateCall
{
    /// <summary><c>Delegate.Combine(a, b)</c>: C#'s <c>a + b</c> and <c>+=</c> on delegates, an event's add.</summary>
    Combine,

    /// <summary><c>Delegate.Remove(a, b)</c>: C#'s <c>a - b</c> and <c>-=</c>, an event's remove.</summary>
    Remove,

    /// <summary><c>Delegate.op_Equality(a, b)</c>: C#'s <c>==</c> on delegates.</summary>
    Equality,

    /// <summary><c>Delegate.op_Inequality(a, b)</c>: C#'s <c>!=</c>.</summary>
    Inequality,

    /// <summary><c>d.GetInvocationList()</c>: a new <c>Delegate[]</c> of the delegates it calls, in order.</summary>
    InvocationList,

    /// <summary>
    /// <c>Interlocked.CompareExchange&lt;T&gt;(ref location, value, comparand)</c> of a reference type T: what the add and
    /// remove of a field-like event loop on.
    /// </summary>
    CompareExchange,
}

/// <summary>
/// Delegates, lambdas and closures (on a target with a heap): what ChipSharp compiles of them, which the whole-program
/// pass checks (<c>Analysis/WholeProgramPass.Delegates.cs</c>) and the IL-to-TIR translation follows
/// (<c>Lowering/MethodLowering.Delegates.cs</c>, <c>Lowering/DelegateFunctions.cs</c>).
/// <para>
/// <b>The object.</b> A delegate is an object on the heap of its delegate type (<c>System.Action&lt;int&gt;</c>, the
/// program's <c>delegate int Op(int a, int b)</c>, ...), each type with a descriptor of its own whose base is
/// System.MulticastDelegate's, whose base is System.Delegate's, whose base is System.Object's. Every delegate is laid out
/// as <see cref="ObjectType"/>:
/// <code>
/// header | target (ref) | method (ptr) | invocations (ref)        16 bytes on the Pico, 32 on a PC
/// </code>
/// <list type="bullet">
/// <item>A delegate of an instance method (a lambda's, on its closure or on C#'s cached <c>&lt;&gt;c</c> object; a method
/// group <c>counter.Add</c>; a virtual method, whose method <c>ldvirtftn</c> finds in the object's virtual method table or
/// interface map): <c>target</c> is the object, <c>method</c> the address of the method's function, which takes it as
/// its <c>this</c>.</item>
/// <item>A delegate of a static method: <c>target</c> is null, <c>method</c> the address of the method's thunk
/// (<c>&lt;delegate&gt; Program.Add</c>), a function that takes a ref it does not use, then the method's parameters, and
/// calls the method (or the runtime function of a library method, <c>Console.WriteLine</c>). A static method closed over
/// its first argument (an extension method as a method group, <c>node.Twice</c>) is called itself: the target is that
/// argument.</item>
/// <item>A multicast delegate (what <c>+</c> and <c>+=</c> make of two or more): <c>invocations</c> and <c>target</c> are
/// both a <c>System.Delegate[]</c> of the single delegates it calls, in order (never another multicast one), and
/// <c>method</c> the invoker of its delegate type (<c>ChipSharp.Delegate.InvokeAll</c> of the Invoke's TIR signature),
/// which calls each with what it was given and returns the last one's result. A single delegate has no invocations
/// (null).</item>
/// </list>
/// So every invocation, <c>d(x)</c>, <c>d.Invoke(x)</c> and <c>d?.Invoke(x)</c>, is the same: the null check (.NET's
/// NullReferenceException), two loads and one <c>call indirect method(target, x)</c>. The collector follows
/// <c>target</c> and <c>invocations</c>; <c>method</c> is an address in the code, which it never follows.
/// </para>
/// <para>
/// <b>Equality and the rest.</b> Two delegates are equal (<c>==</c>, <c>Equals</c>, what <c>Remove</c> looks for) as
/// .NET says: the same type, and the same target and method; two multicast ones when their invocations are equal one
/// by one. <c>Combine</c> and <c>Remove</c> keep .NET's order and results: <c>a + null</c> is <c>a</c> itself,
/// <c>Remove</c> takes the last occurrence of the list it is given and gives the one delegate left itself, or null. An
/// event's add and remove (<c>Interlocked.CompareExchange</c> in a loop, as C# makes them) compare and store with
/// interrupts off, so that a handler that sets the field in between makes the loop go round again.
/// </para>
/// <para>
/// <b>Closures</b> are C#'s own: a lambda that captures variables is an instance method of a class C# makes for them
/// (<c>&lt;&gt;c__DisplayClass0_0</c>, one object per scope, so a loop variable is captured anew each time round, as in
/// .NET), and a local function that is never made into a delegate takes a struct of them by <c>ref</c>, which needs no
/// heap. A lambda that captures nothing is a delegate C# makes once and keeps in a static field.
/// </para>
/// </summary>
internal static class Delegates
{
    /// <summary>The descriptor name of System.Delegate, the base of every delegate type's (through MulticastDelegate's).</summary>
    public const string DelegateName = "System.Delegate";

    /// <summary>The descriptor name of System.MulticastDelegate.</summary>
    public const string MulticastName = "System.MulticastDelegate";

    /// <summary>
    /// What the name of an object method a delegate runs starts with, as a <see cref="DispatchTarget"/> names it:
    /// "Delegate.Equals" and "Delegate.GetHashCode" (see <see cref="VirtualDispatch.DelegateTarget"/>).
    /// </summary>
    public const string DelegatePrefix = "Delegate.";

    /// <summary>The field of <see cref="ObjectType"/> that holds the target (a ref).</summary>
    public const int TargetField = 1;

    /// <summary>The field of <see cref="ObjectType"/> that holds the method's address (a ptr).</summary>
    public const int MethodField = 2;

    /// <summary>The field of <see cref="ObjectType"/> that holds a multicast delegate's invocations (a ref to a Delegate[]).</summary>
    public const int InvocationsField = 3;

    /// <summary>The library's delegate types, by full name and arity of the generic ones: their Invoke takes !0, !1, ...</summary>
    private static readonly Dictionary<string, Func<IReadOnlyList<IlType>, IlMethodSignature>> LibraryTypes = Library();

    /// <summary>Every delegate is laid out as this: the header, the target, the method's address, the invocations.</summary>
    public static TirStructType ObjectType { get; } = TirTypeDescriptor.ObjectStruct(DelegateName,
        ("target", TirType.Ref), ("method", TirType.Ptr), ("invocations", TirType.Ref));

    /// <summary>System.Delegate as a signature names it, for a <c>Delegate[]</c>.</summary>
    public static IlNamedType DelegateType { get; } = new("System", "Delegate", null, "System.Runtime");

    /// <summary>
    /// True for a delegate type (<c>System.Action</c>, <c>System.Func&lt;int, int&gt;</c>, the program's own, generic
    /// ones too) and for System.Delegate and System.MulticastDelegate: a reference type whose values are delegates.
    /// </summary>
    public static bool IsDelegateType(IlType type, IlAssembly assembly) =>
        IsBaseType(type) || InvokeSignature(type, assembly) is not null;

    /// <summary>True for System.Delegate and System.MulticastDelegate.</summary>
    public static bool IsBaseType(IlType type) => ValueTypes.WithoutModifiers(type) is IlNamedType
    {
        Namespace: "System", Name: "Delegate" or "MulticastDelegate", DeclaringType: null, IsDefinedInThisAssembly: false,
    };

    /// <summary>
    /// The signature of the Invoke of the delegate type <paramref name="type"/>, its type arguments in place (an instance
    /// method's: <c>instance int32(int32)</c> for <c>Func&lt;int, int&gt;</c>); null for a type that is not a delegate
    /// type (System.Delegate and System.MulticastDelegate are not: they have no Invoke).
    /// </summary>
    public static IlMethodSignature? InvokeSignature(IlType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        switch (ValueTypes.WithoutModifiers(type))
        {
            case IlNamedType { IsDefinedInThisAssembly: false } named:
                return Arity(named) == 0 && LibraryTypes.TryGetValue(named.FullName, out var signature) ? signature([]) : null;

            case IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: false } named } instance:
                return LibraryTypes.TryGetValue(named.FullName, out var generic) && Arity(named) == instance.Arguments.Count
                    ? generic(instance.Arguments)
                    : null;

            case IlNamedType named when Definition(named, assembly) is { } definition:
                return Invoke(definition)?.Signature;

            case IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } named } instance
                when Definition(named, assembly) is { } definition
                    && definition.GenericParameters.Count == instance.Arguments.Count && Invoke(definition) is { } invoke:
                return new Substitution(instance.Arguments, []).Apply(invoke.Signature);

            default:
                return null;
        }
    }

    /// <summary>
    /// The delegate type as one name for it: modifiers left out, an instance of the program's generic delegate type
    /// (<c>Transform&lt;int&gt;</c>, made by <see cref="Instantiations"/>) as the generic type with its arguments, as code
    /// names it. Two names of one type give the same.
    /// </summary>
    public static IlType Canonical(IlType type, IlAssembly assembly)
    {
        var plain = ValueTypes.WithoutModifiers(type);
        return plain is IlNamedType { IsDefinedInThisAssembly: true } named
            && assembly.FindType(named.FullName) is { GenericDefinition: { } generic, TypeArguments: var arguments }
            ? new IlGenericInstanceType(generic.Type, arguments)
            : plain;
    }

    /// <summary>
    /// A key for the delegate types that are one delegate type for interrupt handlers' reach: the generic type's (or the
    /// type's own) full name, whatever its type arguments, which may vary (<c>Action&lt;object&gt;</c> as an
    /// <c>Action&lt;string&gt;</c>).
    /// </summary>
    public static string Family(IlType type, IlAssembly assembly) => Canonical(type, assembly) switch
    {
        IlGenericInstanceType { Definition: IlNamedType named } => named.FullName,
        IlNamedType named => named.FullName,
        var other => other.ToString(),
    };

    /// <summary>True for a delegate type's constructor, which C# calls with the target and the method's address: <c>.ctor(object, native int)</c>.</summary>
    public static bool IsConstructor(IlMethodReference method, IlAssembly assembly) =>
        method is { Name: ".ctor", HasThis: true, ParameterTypes: [IlPrimitiveType { Kind: IlPrimitiveKind.Object },
            IlPrimitiveType { Kind: IlPrimitiveKind.NativeInt }] }
        && InvokeSignature(method.DeclaringType, assembly) is not null;

    /// <summary>True for a delegate type's Invoke: C#'s <c>d(x)</c>, <c>d.Invoke(x)</c>, <c>d?.Invoke(x)</c>.</summary>
    public static bool IsInvoke(IlMethodReference method, IlAssembly assembly) =>
        method is { Name: "Invoke", HasThis: true } && InvokeSignature(method.DeclaringType, assembly) is not null;

    /// <summary>
    /// True for a method of a delegate type of the program (its constructor, Invoke, BeginInvoke and EndInvoke): the
    /// runtime implements them, so they are never part of the program, which calls them as delegates.
    /// </summary>
    public static bool IsDelegateMethod(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return IsDelegateDefinition(method.DeclaringType);
    }

    /// <summary>True for a delegate type of the program: one derived from System.MulticastDelegate.</summary>
    public static bool IsDelegateDefinition(IlTypeDefinition type) =>
        type.BaseType is IlNamedType { Namespace: "System", Name: "MulticastDelegate", IsDefinedInThisAssembly: false };

    /// <summary>The method of System.Delegate (or System.Threading.Interlocked) that <paramref name="method"/> is, or null.</summary>
    public static DelegateCall? Call(IlMethodReference method, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Definition is not null)
        {
            return null;
        }

        if (method is { Name: "CompareExchange", HasThis: false, GenericArguments: [var argument],
                DeclaringType: IlNamedType { FullName: "System.Threading.Interlocked", IsDefinedInThisAssembly: false },
                ParameterTypes: [IlByRefType { ElementType: IlGenericParameterType { IsMethodParameter: true } }, _, _] })
        {
            return ObjectTypes.TryMapType(argument, assembly, out _) ? DelegateCall.CompareExchange : null;
        }

        if (!IsBaseType(method.DeclaringType))
        {
            return null;
        }

        var delegates = method.ParameterTypes.Count == 2 && method.ParameterTypes.All(IsBaseType);
        return (method.Name, method.HasThis) switch
        {
            ("Combine", false) when delegates => DelegateCall.Combine,
            ("Remove", false) when delegates => DelegateCall.Remove,
            ("op_Equality", false) when delegates => DelegateCall.Equality,
            ("op_Inequality", false) when delegates => DelegateCall.Inequality,
            ("GetInvocationList", true) when method.ParameterTypes.Count == 0 => DelegateCall.InvocationList,
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="instruction"/> makes a delegate, or an array of them, on the heap: a delegate type's
    /// <c>newobj</c>, Combine, Remove and GetInvocationList. Never in an interrupt handler (<see cref="ClassRules.Allocates"/>).
    /// </summary>
    public static bool Allocates(IlInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        // a delegate type of the program is named by its own methods (a library type needs no assembly to be known)
        IlAssembly Owner(IlMethodReference method) => method.Definition?.Assembly ?? LibrarySubstitutes.Assembly;
        return instruction switch
        {
            { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } } =>
                IsConstructor(constructor, Owner(constructor)),
            { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: { Definition: null } callee } } =>
                Call(callee, LibrarySubstitutes.Assembly) is DelegateCall.Combine or DelegateCall.Remove
                    or DelegateCall.InvocationList,
            _ => false,
        };
    }

    /// <summary>
    /// The slot whose method the <c>ldvirtftn</c> at <paramref name="index"/> loads (C#'s delegate of a virtual method,
    /// <c>Func&lt;int&gt; f = shape.Area;</c>): what a virtual call through it would run, on the object on top of the stack.
    /// Null for any other instruction, and for an <c>ldvirtftn</c> of a method that is not virtual.
    /// </summary>
    public static DispatchSlot? LoadedSlot(IReadOnlyList<IlInstruction> instructions, int index) =>
        instructions[index] is { OpCode: IlOpCode.Ldvirtftn, Operand: IlMethodOperand { Method: var method } }
            ? VirtualMethods.SlotCalled(method)
            : null;

    /// <summary>
    /// The method of the program whose address <paramref name="instruction"/> takes, which a delegate calls: an
    /// <c>ldftn</c>'s, or an <c>ldvirtftn</c>'s of a method that is not virtual (a virtual one's are what its slot runs,
    /// see <see cref="LoadedSlot"/>). Null for a library method, a generic one and any other instruction.
    /// </summary>
    public static IlMethod? AddressTaken(IReadOnlyList<IlInstruction> instructions, int index) =>
        instructions[index] switch
        {
            { OpCode: IlOpCode.Ldftn, Operand: IlMethodOperand { Method.Definition: { } method } }
                when !Instantiations.IsGeneric(method) => method,
            { OpCode: IlOpCode.Ldvirtftn, Operand: IlMethodOperand { Method.Definition: { } method } }
                when LoadedSlot(instructions, index) is null && !Instantiations.IsGeneric(method) => method,
            _ => null,
        };

    /// <summary>
    /// The methods each delegate type (by <see cref="Family"/>) of <paramref name="methods"/> can call: those whose
    /// address the program takes for it (an <c>ldftn</c> or <c>ldvirtftn</c> right before the type's <c>newobj</c>), with
    /// what a virtual one's slot runs on the classes <paramref name="dispatch"/> knows. What an interrupt handler that
    /// invokes a delegate of the type may run.
    /// </summary>
    public static Dictionary<string, HashSet<IlMethod>> TargetsByType(IEnumerable<IlMethod> methods, VirtualDispatch dispatch)
    {
        var targets = new Dictionary<string, HashSet<IlMethod>>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var instructions = method.Body?.Instructions ?? [];
            for (var i = 0; i + 1 < instructions.Count; i++)
            {
                if (instructions[i + 1] is not { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }
                    || !IsConstructor(constructor, method.Assembly))
                {
                    continue;
                }

                var family = Family(constructor.DeclaringType, method.Assembly);
                if (!targets.TryGetValue(family, out var found))
                {
                    targets.Add(family, found = []);
                }

                if (AddressTaken(instructions, i) is { } direct)
                {
                    found.Add(direct);
                }
                else if (LoadedSlot(instructions, i) is { } slot)
                {
                    found.UnionWith(dispatch.Targets(slot, null).Select(target => target.Method).OfType<IlMethod>());
                }
            }
        }

        return targets;
    }

    /// <summary>The Invoke of a delegate type of the program, or null when it has none.</summary>
    private static IlMethod? Invoke(IlTypeDefinition type) =>
        IsDelegateDefinition(type) ? type.Methods.FirstOrDefault(method => method is { Name: "Invoke", IsStatic: false }) : null;

    /// <summary>The type of the program (or an instance of a generic one) that <paramref name="named"/> names, or null.</summary>
    private static IlTypeDefinition? Definition(IlNamedType named, IlAssembly assembly) =>
        named.IsDefinedInThisAssembly ? assembly.FindType(named.FullName) : null;

    /// <summary>The number of type parameters a generic type's name says (<c>Func`2</c>: 2), 0 for any other.</summary>
    private static int Arity(IlNamedType named) =>
        named.Name.IndexOf('`', StringComparison.Ordinal) is >= 0 and var tick && int.TryParse(named.Name[(tick + 1)..],
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var arity)
            ? arity
            : 0;

    /// <summary>
    /// The delegate types of System a program may use, with their Invoke: Action and Func with up to 16 parameters,
    /// Predicate, Comparison, Converter, EventHandler and EventHandler&lt;TEventArgs&gt;.
    /// </summary>
    private static Dictionary<string, Func<IReadOnlyList<IlType>, IlMethodSignature>> Library()
    {
        static IlMethodSignature Signature(IlType returns, params IlType[] parameters) => new(true, returns, parameters);

        static IlType Argument(IReadOnlyList<IlType> arguments, int index) => arguments[index];

        var types = new Dictionary<string, Func<IReadOnlyList<IlType>, IlMethodSignature>>(StringComparer.Ordinal)
        {
            ["System.Action"] = _ => Signature(IlType.Void),
            ["System.Predicate`1"] = a => Signature(IlType.Bool, Argument(a, 0)),
            ["System.Comparison`1"] = a => Signature(IlType.Int32, Argument(a, 0), Argument(a, 0)),
            ["System.Converter`2"] = a => Signature(Argument(a, 1), Argument(a, 0)),
            ["System.EventHandler"] = _ => Signature(IlType.Void, IlType.Object,
                new IlNamedType("System", "EventArgs", null, "System.Runtime")),
            ["System.EventHandler`1"] = a => Signature(IlType.Void, IlType.Object, Argument(a, 0)),
        };

        for (var n = 1; n <= 16; n++)
        {
            types[$"System.Action`{n}"] = a => Signature(IlType.Void, [.. a]);
        }

        for (var n = 1; n <= 17; n++)
        {
            types[$"System.Func`{n}"] = a => Signature(a[^1], [.. a.Take(a.Count - 1)]);
        }

        return types;
    }
}
