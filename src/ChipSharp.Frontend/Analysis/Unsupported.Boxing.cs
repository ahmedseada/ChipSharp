namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Boxing (see <see cref="Boxing"/>): a value turned into an object makes one on the heap, so on a target without a
/// heap it is "boxing" or "converting a struct to an interface", which needs a heap; the rest of what boxing does not do.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>
    /// <c>unbox</c> of a nullable (its address in a box, which has none: a box holds a T) or a cast of an object to a
    /// value type with <c>castclass</c>, which C# never makes.
    /// </summary>
    public static readonly Unsupported UnboxingAddresses = new("the address of a value in a box (unbox of a nullable, castclass to a value)",
        false, "For now, unbox the value into a variable first: var value = (int?)o;");

    /// <summary>A value of <paramref name="type"/> (a library struct, such as TimeSpan) boxed: ChipSharp boxes numbers, enums and the program's structs.</summary>
    public static Unsupported BoxingOf(string type) => Heap($"boxing a {type} (using it as an object)", false,
        "For now, box the numbers, enums and structs of the program, or keep the value in its own type.");
}
