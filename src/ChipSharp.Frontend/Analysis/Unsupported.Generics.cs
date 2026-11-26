namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What generics cannot do without a heap, in C# words (see <see cref="Generics.Instantiations"/>): ChipSharp compiles a
/// generic method or type once for each list of type arguments, which must be values (numbers, bools, enums, structs,
/// fixed text), not objects.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>A class, an interface, object or an array as a type argument: one copy of the code for every object type, on the heap.</summary>
    public static readonly Unsupported GenericsOverClasses = Heap(
        "generics over classes (a class, an interface, object or an array as a type argument)", true,
        "For now, use numbers, bools, enums and structs as type arguments: ChipSharp makes the code once for each.");

    /// <summary>A struct converted to an interface (<c>IDevice d = counter;</c>) or to object: the struct is boxed.</summary>
    public static readonly Unsupported BoxingToInterface = Heap(
        "converting a struct to an interface or to object (boxing it)", false,
        "For now, pass the struct to a generic method instead: static void Run<T>(ref T device) where T : struct, IDevice.");

    /// <summary>
    /// Type arguments that grow without end: a generic method that calls itself with a bigger type argument each time
    /// (<c>F&lt;T&gt;</c> calling <c>F&lt;Pair&lt;T&gt;&gt;</c>), which would need a copy of the code for each.
    /// </summary>
    public static Unsupported GenericNesting(int most) => Rule(
        $"type arguments nested more than {most} generic types deep (a generic method or type that uses itself with a " +
        "bigger type argument each time)",
        "ChipSharp makes a copy of a generic method or type for each list of type arguments, so it needs to know them " +
        "all: give the call type arguments that do not grow each time.");
}
