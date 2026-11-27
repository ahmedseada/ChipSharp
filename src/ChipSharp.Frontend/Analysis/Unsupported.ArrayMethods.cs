namespace ChipSharp.Frontend.Analysis;

/// <summary>What the methods of System.Array (<see cref="ArrayMethods"/>) cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    /// <summary>The <see cref="Arrival"/> of what needs delegates: Array.Exists, Find, ForEach, ...</summary>
    private const string WithDelegates = "with M3 (delegates)";

    public static readonly Unsupported ArrayMethodsWithDelegates = new(
        "the methods of System.Array that take a delegate (Array.Exists, Find, FindIndex, FindAll, TrueForAll, ForEach, " +
        "ConvertAll, Sort with a Comparison)", true,
        "For now, write the loop: for (int i = 0; i < array.Length; i++) if (...) { ... }.")
    {
        Arrival = WithDelegates,
    };

    public static readonly Unsupported ArraySortElements = new(
        "Array.Sort and Array.BinarySearch of arrays of enums, structs, text or objects", true,
        "For now, sort an array of numbers (a key for each element), or write the loop.");

    public static readonly Unsupported ArrayIndexOfElements = new(
        "Array.IndexOf and Array.LastIndexOf of arrays of structs, text, object or classes with an Equals of their own",
        true, "For now, write the loop and compare what makes two elements equal: for (int i = 0; i < array.Length; " +
        "i++) if (...) return i;");

    public static readonly Unsupported ArrayCopyTypes = new(
        "Array.Copy and CopyTo between arrays of different element types (int[] into long[])", true,
        "For now, copy the elements one by one: for (int i = 0; i < length; i++) destination[i] = source[i];");

    public static readonly Unsupported ArrayClone = Heap("copying an array into a new one with array.Clone()", false,
        "For now, make the copy with new and Array.Copy into an array of your own.");

    public static readonly Unsupported ArrayResize = Heap("resizing an array with Array.Resize", false,
        "For now, make an array as big as it will ever need to be, and keep its length in a variable.");

    public static readonly Unsupported ResizeReference = new(
        "Array.Resize of something other than a local, a parameter or a field (an element, a ref parameter)", false,
        "For now, pass the variable itself: Array.Resize(ref buffer, size).");
}
