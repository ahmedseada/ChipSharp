namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What of virtual calls, interfaces and type tests (see <see cref="VirtualMethods"/>) does not compile yet, in C#
/// words. Like everything about objects, they need a heap.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>
    /// A virtual method that is generic (<c>virtual T Pick&lt;T&gt;()</c>), or a generic method of an interface: each type
    /// argument needs a slot of its own.
    /// </summary>
    public static readonly Unsupported GenericVirtualMethods = Coming(
        "generic virtual methods (virtual void Run<T>(), or a generic method of an interface)", true,
        "For now, make the method generic or virtual, not both.", WithGenericClasses);

    /// <summary>
    /// <c>is</c>, <c>as</c> or a cast to a type ChipSharp does not test objects against (<paramref name="what"/>: an
    /// array, string, a library class); unboxing (to a struct, a number) is <see cref="Boxing"/>.
    /// </summary>
    public static Unsupported TypeTestsOf(string what) => Heap($"type tests and casts to {what} (is, as, (T)x)", true,
        "For now, test objects against the program's own classes and interfaces.");
}
