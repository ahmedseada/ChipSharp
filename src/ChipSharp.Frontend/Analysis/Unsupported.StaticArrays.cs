namespace ChipSharp.Frontend.Analysis;

/// <summary>What arrays in static fields (<see cref="StaticArrays"/>) cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    private const string Declared = "where the field is declared (static int[] buffer = new int[16];)";

    public static readonly Unsupported StaticArrayAssignments = Heap(
        "an array in a static field that is assigned again, or anywhere but in its field initializer or static " +
        "constructor", false, $"For now, assign it once, {Declared}, and change its elements instead.");

    public static readonly Unsupported StaticArrayInitializers = Heap(
        "an array in a static field that is not made with a constant size and constant elements", false,
        "For now, make it where the field is declared, with a constant size (new int[16]) or constant elements " +
        "({ 1, 2, 3 }); a method that Main calls first can set the elements.");

    public static readonly Unsupported StaticArrayNull = Heap(
        "a static field of an array type that holds null (it is never given an array)", false,
        $"For now, give it an array {Declared}.");

    public static readonly Unsupported StaticArrayReferences = Heap(
        "references to a static field that holds an array (ref, out)", true,
        "For now, use the field itself: it always holds the same array.");
}
