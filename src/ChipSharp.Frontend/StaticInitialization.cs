using System.Reflection;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// C#'s static initialization: a type's static field initializers and its static constructor, which C# compiles into
/// one method, the type's initializer (<c>.cctor</c>). ChipSharp runs the initializer of every type the program uses,
/// once each, when the program starts, before <c>Main</c>.
/// </summary>
/// <remarks>
/// <para>
/// Which types: a type is used when the program reads or writes one of its static fields (or takes a field's
/// address), and, when the type has a static constructor, also when it calls one of the type's methods; starting the
/// program counts as calling <c>Main</c>. That is when .NET runs an initializer at the latest: C# marks a type without
/// a static constructor <c>beforefieldinit</c>, which lets .NET run its initializer as late as the first use of one of
/// its fields, so for such a type only the fields count.
/// </para>
/// <para>
/// In which order: the types in the order the program first uses them, read the way it runs: from <c>Main</c>'s first
/// instruction to its last, going into each method of the program where it is first called. One exception: a type's
/// initializer runs after the initializers of the types its own code uses (which .NET would start inside it, at their
/// first use). When initializers use each other in a circle, the first one reached runs last and the others see its
/// fields still zero, as in .NET when that one starts first.
/// </para>
/// <para>
/// Everything runs before <c>Main</c>, so an initializer with something to see, such as printing, does it before
/// <c>Main</c> prints anything, even where .NET would run it later.
/// </para>
/// </remarks>
internal static class StaticInitialization
{
    /// <summary>The initializer (<c>.cctor</c>) of <paramref name="type"/>, or null when it has none.</summary>
    public static IlMethod? TypeInitializer(IlTypeDefinition type) =>
        type.Methods.FirstOrDefault(method => method is { Name: ".cctor", IsStatic: true });

    /// <summary>True for a type that has a static constructor: one that C# did not mark <c>beforefieldinit</c>.</summary>
    public static bool HasStaticConstructor(IlTypeDefinition type) => (type.Attributes & TypeAttributes.BeforeFieldInit) == 0;

    /// <summary>
    /// The initializers that <paramref name="method"/>'s own instructions make run, each once, in their order: those of
    /// the types it uses (see the remarks), and first its own type's when <paramref name="isStart"/> (the program
    /// starts there). The methods it calls are not looked into.
    /// </summary>
    public static IEnumerable<IlMethod> InitializersRunBy(IlMethod method, bool isStart)
    {
        ArgumentNullException.ThrowIfNull(method);

        var seen = new HashSet<IlMethod>();
        foreach (var type in UsedTypes(method, isStart))
        {
            if (TypeInitializer(type) is { } initializer && seen.Add(initializer))
            {
                yield return initializer;
            }
        }
    }

    /// <summary>
    /// The initializers among <paramref name="methods"/>, the methods of a program with its entry point first, in the
    /// order they run (see the remarks). The types that only <paramref name="alsoStarted"/> use (interrupt handlers,
    /// which the chip starts, not the program) come after the entry point's, in that order: they too are initialized
    /// before Main, so before any handler can run.
    /// </summary>
    public static IReadOnlyList<IlMethod> RunOrder(IReadOnlyList<IlMethod> methods, IEnumerable<IlMethod>? alsoStarted = null)
    {
        ArgumentNullException.ThrowIfNull(methods);

        var program = methods.ToHashSet();
        var order = new List<IlMethod>();
        var started = new HashSet<IlTypeDefinition>();

        void Initialize(IlTypeDefinition type)
        {
            if (started.Add(type) && TypeInitializer(type) is { } initializer && program.Contains(initializer))
            {
                foreach (var used in TypesUsedByRunning(initializer, program))
                {
                    Initialize(used);
                }

                order.Add(initializer);
            }
        }

        foreach (var start in methods.Take(1).Concat(alsoStarted ?? []))
        {
            foreach (var type in TypesUsedByRunning(start, program, isStart: true))
            {
                Initialize(type);
            }
        }

        // then those of the methods only a virtual call or an interface reaches (an override, an array's IList<T>.IndexOf),
        // in the order the program reached them
        foreach (var method in methods.Skip(1))
        {
            foreach (var type in TypesUsedByRunning(method, program))
            {
                Initialize(type);
            }
        }

        return order;
    }

    /// <summary>
    /// The type whose initializer <paramref name="instruction"/> makes run, or null: the type of the static field it
    /// reads, writes or takes the address of, or the type of the method of the program it calls (a struct's constructor
    /// too, through <c>newobj</c>) when that type has a static constructor.
    /// </summary>
    private static IlTypeDefinition? UsedType(IlInstruction instruction) => instruction switch
    {
        { OpCode: IlOpCode.Ldsfld or IlOpCode.Stsfld or IlOpCode.Ldsflda, Operand: IlFieldOperand { Field.Definition: { } field } } =>
            field.DeclaringType,
        _ when ValueTypes.CalledMethod(instruction) is { } callee && HasStaticConstructor(callee.DeclaringType) =>
            callee.DeclaringType,
        _ => null,
    };

    /// <summary>The types <paramref name="method"/>'s own instructions use, in order; its own type first at the start.</summary>
    private static IEnumerable<IlTypeDefinition> UsedTypes(IlMethod method, bool isStart)
    {
        if (isStart && HasStaticConstructor(method.DeclaringType))
        {
            yield return method.DeclaringType;
        }

        foreach (var instruction in method.Body?.Instructions ?? [])
        {
            if (UsedType(instruction) is { } type)
            {
                yield return type;
            }
        }
    }

    /// <summary>
    /// The types that running <paramref name="start"/> uses, each once, in the order first used: its instructions in
    /// order, going into each method of <paramref name="program"/> it calls where it is first called.
    /// </summary>
    private static List<IlTypeDefinition> TypesUsedByRunning(IlMethod start, HashSet<IlMethod> program, bool isStart = false)
    {
        var types = new List<IlTypeDefinition>();
        var seenTypes = new HashSet<IlTypeDefinition>();
        var seenMethods = new HashSet<IlMethod>();

        void Run(IlMethod method, bool isStart)
        {
            if (!seenMethods.Add(method))
            {
                return;
            }

            if (isStart && HasStaticConstructor(method.DeclaringType) && seenTypes.Add(method.DeclaringType))
            {
                types.Add(method.DeclaringType);
            }

            foreach (var instruction in method.Body?.Instructions ?? [])
            {
                if (UsedType(instruction) is { } type && seenTypes.Add(type))
                {
                    types.Add(type);
                }

                if (ValueTypes.CalledMethod(instruction) is { } callee && program.Contains(callee))
                {
                    Run(callee, isStart: false);
                }
            }
        }

        Run(start, isStart);
        return types;
    }
}
