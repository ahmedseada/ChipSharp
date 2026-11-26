using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>What arrays on the stack (<see cref="StackArrays"/>) cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    public static readonly Unsupported ArrayEscapes = Heap(
        "an array that leaves the method that creates it (returned, or stored in a field or in another array)", false,
        "For now, keep each array in the method that creates it; the methods it calls can take it as a parameter.");

    public static readonly Unsupported ArraySizes = Heap(
        "an array whose size is known only when the program runs", false,
        "For now, give the array a fixed size: a constant, such as new int[16].");

    public static readonly Unsupported ArrayReuse = Heap(
        "an array made in a loop while the one made there before is still in use", false,
        "For now, make the array once, before the loop, and reuse it.");

    public static readonly Unsupported ArrayComparisons = Heap("comparing arrays (or an array with null)", false,
        "For now, leave the comparison out: an array on the stack is never null.");

    public static readonly Unsupported ArraysOfReferences = Heap("arrays of strings, objects or arrays", true,
        "For now, use arrays of numbers, bools, structs or enums.");

    public static readonly Unsupported MultiDimensionalArrays = new("multi-dimensional arrays (int[,])", true,
        "For now, use a one-dimensional array and compute the index: row * width + column.");

    /// <summary>
    /// What an array type stands for where it is not supported: its elements' problem (see
    /// <see cref="WholeProgramPass.ForArrayElement"/>, which knows the program's structs), or a multi-dimensional array.
    /// </summary>
    private static Unsupported? ForArrayType(IlType type, IlAssembly assembly) => type switch
    {
        IlArrayType array => WholeProgramPass.ForArrayElement(array.ElementType, assembly),
        _ => MultiDimensionalArrays,
    };
}
