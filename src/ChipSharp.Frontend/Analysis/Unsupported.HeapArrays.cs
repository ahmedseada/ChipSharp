namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Arrays on the heap (see <see cref="HeapArrays"/>): what the heap makes work, and what arrays still cannot do.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>
    /// True for a feature that compiles on a target with a heap, and only there: a heap use. Checked without a target,
    /// or for a target with a heap, it is no problem at all (<see cref="TargetRules"/> drops it); on a target without a
    /// heap it is the feature that needs one (<see cref="WithoutHeap"/>), with the words a check gave it before the heap
    /// came: an array that leaves its method, of a size known only when the program runs, null in place of an array, ...
    /// </summary>
    public bool IsHeapUse { get; private init; }

    public static readonly Unsupported ArraysOfText = Heap("arrays of strings", true,
        "For now, keep each string in a variable or a static field of its own, or pick it with a switch.");

    public static readonly Unsupported StaticArrayKept = new(
        "an array in a static field that needs no heap (a constant table, or a fixed array) kept outside its field: " +
        "returned, stored in another field or array, or passed to a method that keeps it", false,
        "For now, read it through its field where it is needed, or copy its elements into an array made with new " +
        "and keep that.");

    public static readonly Unsupported ArrayReferences = new("references to arrays (ref int[], out int[])", true,
        "For now, return the array, or keep it in a field.");

    public static readonly Unsupported ArrayCovariance = new(
        "array covariance (an array of one type of object used as an array of another, object[] items = new int[2][])",
        false, "For now, make the array of the type it is used as: new object[2].");

    /// <summary>
    /// A method of System.Array the program calls (<c>Array.Copy</c>, <c>Array.Fill</c>, <c>array.Clone()</c>, ...),
    /// e.g. "System.Array.Copy(System.Array, System.Array, int)": not available yet, with what to write for now.
    /// </summary>
    public static Unsupported ArrayMethod(string method) => LibraryMethod(method) with
    {
        Hint = "For now, write it as a loop over the elements: for (int i = 0; i < array.Length; i++) { ... }.",
    };

    /// <summary>
    /// <paramref name="feature"/>, which needs a heap, as a heap use: fine on a target with a heap, and
    /// <paramref name="feature"/> itself on a target without one.
    /// </summary>
    public static Unsupported HeapUse(Unsupported feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.IsHeapUse ? feature : feature with { IsHeapUse = true };
    }
}
