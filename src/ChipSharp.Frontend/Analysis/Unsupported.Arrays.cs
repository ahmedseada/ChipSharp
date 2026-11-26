using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>What arrays on the stack (<see cref="StackArrays"/>) cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    private const string WithTheHeap = "with the heap in M3";

    public static readonly Unsupported ArrayEscapes = new(
        "an array that leaves the method that creates it (returned, or stored in a field or in another array)", false,
        "For now, keep each array in the method that creates it; the methods it calls can take it as a parameter.")
    {
        Arrival = WithTheHeap,
    };

    public static readonly Unsupported ArraySizes = new("an array whose size is known only when the program runs", false,
        "For now, give the array a fixed size: a constant, such as new int[16].")
    {
        Arrival = WithTheHeap,
    };

    public static readonly Unsupported ArrayReuse = new(
        "an array made in a loop while the one made there before is still in use", false,
        "For now, make the array once, before the loop, and reuse it.")
    {
        Arrival = WithTheHeap,
    };

    public static readonly Unsupported ArrayComparisons = new("comparing arrays (or an array with null)", false,
        "For now, leave the comparison out: an array on the stack is never null.");

    public static readonly Unsupported ArraysOfReferences = new("arrays of strings, objects or arrays", true,
        "For now, use arrays of whole numbers or bools.");

    public static readonly Unsupported MultiDimensionalArrays = new("multi-dimensional arrays (int[,])", true,
        "For now, use a one-dimensional array and compute the index: row * width + column.");

    /// <summary>When the feature arrives, as the end of <see cref="Heading"/>: "after M0", "with the heap in M3".</summary>
    public string Arrival { get; private init; } = $"after {SupportedSubset.Milestone}";

    /// <summary>
    /// What an array of <paramref name="elementType"/> stands for when it is not supported where it is: an array
    /// kept where it outlives its method (a field, a return value) when the element type is supported, else what
    /// the element type needs (floating point, structs, ...). Null for a generic parameter's T[].
    /// </summary>
    public static Unsupported? ForArrayElement(IlType elementType, IlAssembly assembly)
    {
        if (StackArrays.TryMapElementType(elementType, out _))
        {
            return ArrayEscapes;
        }

        var element = ForType(elementType, assembly);
        return elementType is IlArrayType or IlMultiDimensionalArrayType
            || element == Strings || element == ClassesAndObjects || element == Interfaces || element == Delegates
            ? ArraysOfReferences
            : element;
    }

    private static Unsupported? ForArrayType(IlType type, IlAssembly assembly) => type switch
    {
        IlArrayType array => ForArrayElement(array.ElementType, assembly),
        _ => MultiDimensionalArrays,
    };
}
