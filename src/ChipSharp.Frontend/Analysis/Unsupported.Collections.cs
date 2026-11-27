namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What of exceptions and collections (see <see cref="Exceptions"/>, <see cref="TryFinally"/>) does not compile, in C#
/// words: catching an exception arrives later, and a throw, whose exception is an object, needs a heap.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>
    /// A try block with a catch (or a filter or a fault): ChipSharp has no exceptions to catch yet. A try/finally, a
    /// <c>using</c> and a <c>foreach</c> compile (<see cref="TryFinally"/>).
    /// </summary>
    public static readonly Unsupported TryCatch = new("catching exceptions (try/catch)", false,
        "For now, report failures through return values: a throw stops the program, as an exception nothing catches does.");

    /// <summary>A <c>throw</c>: the exception is an object, so it needs a heap.</summary>
    public static readonly Unsupported Throwing = HeapUse(Heap("throw (an exception is an object)", false,
        "For now, report failures through return values."));
}
