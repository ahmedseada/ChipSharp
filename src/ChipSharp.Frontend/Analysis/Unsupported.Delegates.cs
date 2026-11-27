namespace ChipSharp.Frontend.Analysis;

/// <summary>What delegates (see <see cref="Delegates"/>) need, and what they cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    /// <summary>A delegate made (a lambda, a method group, <c>+</c> and <c>-</c> of delegates): an object, a heap use.</summary>
    public static readonly Unsupported DelegateObjects = Heap("delegates and lambdas (each delegate is an object)", true,
        "For now, call the method directly, or choose it with a switch.");

    /// <summary>The object C# makes for what a lambda captures (or for the lambdas that capture nothing): a heap use.</summary>
    public static readonly Unsupported Closures = Heap("lambdas and the variables they capture (closures)", true,
        "For now, pass what the method needs as arguments, or use a local function that is called directly.");

    public static readonly Unsupported DelegateArrays = new(
        "delegates that take or return arrays or spans (Action<int[]>, Func<Span<int>>)", true,
        "For now, keep the array in a field that the delegate's method reads, or call the method directly.");

    public static readonly Unsupported DelegateOfValue = new(
        "delegates of a method of a struct, or of an extension method of a value or an array", true,
        "For now, make the delegate of a lambda that calls the method: () => point.Length().");

    public static readonly Unsupported AsynchronousDelegates = new("BeginInvoke and EndInvoke of delegates", true,
        "For now, call the delegate: Invoke, or d(...).");
}
